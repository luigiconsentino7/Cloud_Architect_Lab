using System.Text.Json.Nodes;
using Lab.Ai;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Seeds;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Profile.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Persistence;
using Lab.Modules.Shared.Realtime;
using Lab.Pipeline.Seeds;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lab.Modules.Conversations;

public sealed record CreateConversationRequest(string Kind, Guid? ChallengeId);

/// <param name="Provider">Obrigatório no chat de contexto: o desafio criado pela IA já sai no provedor escolhido.</param>
public sealed record SendMessageRequest(string Text, string? Provider = null, string? Difficulty = null, string? Mode = null);

public sealed record MessageView(Guid Id, int Number, string Role, string Text, string Source, string? Model, string? PromptVersion, DateTime CreatedAt, JsonNode? Payload);

public sealed record ConversationView(Guid Id, string Kind, Guid? ChallengeId, string Title, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<MessageView> Messages);

public sealed record ConversationSummary(Guid Id, string Kind, Guid? ChallengeId, string Title, DateTime UpdatedAt);

public sealed record SendMessageResponse(MessageView User, MessageView Assistant);

public sealed class ConversationException(string message, int status = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Chat de contexto: busca guiada sempre; com a IA local, a IA escolhe e personaliza uma das sementes encontradas e o
/// desafio é montado e validado pelo instanciador. Chat do entrevistador: IA ancorada na parte pública ou respostas curadas.
/// </summary>
public sealed class ConversationService(
    LabDbContext db,
    IProfileQueries profiles,
    IChallengeLibrary library,
    LabContent content,
    AiRuntime ai,
    IChallengePersonalizer personalizer,
    IInterviewer interviewer,
    ILabNotifier notifier,
    TimeProvider clock,
    ILogger<ConversationService> logger)
{
    public const int MaxMessageLength = 4000;
    private const int MaxCandidates = 6;

    public async Task<ConversationEntity> CreateAsync(CreateConversationRequest request, CancellationToken cancellationToken)
    {
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is not (ConversationKind.Context or ConversationKind.Interview))
            throw new ConversationException($"Tipo de conversa inválido: '{request.Kind}' (use '{ConversationKind.Context}' ou '{ConversationKind.Interview}').");

        var profileId = await profiles.CurrentProfileIdAsync(cancellationToken);
        string title = "";
        if (kind == ConversationKind.Interview)
        {
            if (request.ChallengeId is not { } challengeId) throw new ConversationException("O entrevistador precisa do desafio (challengeId).");
            var brief = await library.BriefAsync(challengeId, cancellationToken) ?? throw new ConversationException("Desafio não encontrado.", StatusCodes.Status404NotFound);

            // Uma conversa de entrevista por desafio: reabrir o desafio retoma a mesma conversa.
            var existing = await db.Conversations.Where(c => c.Kind == kind && c.ChallengeId == challengeId)
                .OrderByDescending(c => c.UpdatedAt).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null) return existing;
            title = $"Entrevista: {brief.Title}";
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var conversation = new ConversationEntity
        {
            Id = Guid.CreateVersion7(),
            ProfileId = profileId,
            Kind = kind,
            ChallengeId = kind == ConversationKind.Interview ? request.ChallengeId : null,
            Title = title,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(cancellationToken);
        return conversation;
    }

    public async Task<SendMessageResponse> SendAsync(Guid conversationId, SendMessageRequest request, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new ConversationException("Conversa não encontrada.", StatusCodes.Status404NotFound);
        var text = request.Text?.Trim() ?? "";
        if (text.Length == 0) throw new ConversationException("Escreva uma mensagem.");
        if (text.Length > MaxMessageLength) throw new ConversationException($"Mensagem longa demais (máximo de {MaxMessageLength} caracteres).");
        if (conversation.Kind == ConversationKind.Context && string.IsNullOrWhiteSpace(request.Provider))
            throw new ConversationException("Escolha o provedor de nuvem antes de pedir o desafio.");

        var difficulty = ParseDifficulty(request.Difficulty);
        var user = await AddAsync(conversation, "user", text, MessageSource.User, cancellationToken: cancellationToken);
        if (conversation.Title.Length == 0) conversation.Title = AiText.Clean(text, 60);
        await db.SaveChangesAsync(cancellationToken);

        var assistant = conversation.Kind == ConversationKind.Context
            ? await ContextReplyAsync(conversation, text, request, difficulty, cancellationToken)
            : await InterviewReplyAsync(conversation, user, cancellationToken);
        conversation.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        if (conversation.Kind == ConversationKind.Interview) await notifier.ChatCompleted(new ChatCompletedEvent(conversation.Id, assistant.Id));
        return new SendMessageResponse(View(user), View(assistant));
    }

    public async Task<ConversationView?> ViewAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        if (conversation is null) return null;
        var messages = await db.ConversationMessages.AsNoTracking().Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Number).ToListAsync(cancellationToken);
        return new ConversationView(conversation.Id, conversation.Kind, conversation.ChallengeId, conversation.Title, conversation.CreatedAt,
            conversation.UpdatedAt, messages.Select(View).ToList());
    }

    private async Task<ConversationMessageEntity> ContextReplyAsync(
        ConversationEntity conversation, string text, SendMessageRequest request, Difficulty? difficulty, CancellationToken cancellationToken)
    {
        var matched = library.Search(text, difficulty, MaxCandidates).Where(s => s.MatchedTerms.Count > 0).ToList();
        var status = await ai.StatusAsync(cancellationToken);
        var note = "";
        if (status.Mode == AiMode.Ollama)
        {
            var level = difficulty ?? Difficulty.Senior;
            var ids = matched.Select(m => m.SeedId).ToList();
            ids.AddRange(library.Search(null, difficulty, 50).Select(s => s.SeedId).Where(id => !ids.Contains(id)).Take(MaxCandidates - ids.Count));
            var candidates = ids.Select(content.FindSeed).OfType<Seed>().Where(s => s.Difficulties.Count == 0 || s.Difficulties.Contains(level))
                .Select(s => SeedCandidate.From(s, level)).ToList();
            var result = await personalizer.PersonalizeAsync(new PersonalizationRequest(text, level.ToString().ToLowerInvariant(), candidates), cancellationToken);
            if (result is not null)
            {
                try
                {
                    var created = await library.CreateAsync(ChallengeOrders.Personalized(result, text, request.Provider!, level.ToString().ToLowerInvariant(), request.Mode), cancellationToken);
                    var reply = $"Montei o desafio “{created.Title}” a partir do seu contexto. {result.Delta.Reason}".Trim();
                    return await AddAsync(conversation, "assistant", reply, MessageSource.Ai, result.Model, result.PromptVersion, new
                    {
                        kind = "challenge",
                        challengeId = created.Id,
                        title = created.Title,
                        seedId = created.SeedId,
                        provider = created.Provider,
                        difficulty = created.Difficulty,
                        narrative = result.Delta.Narrative,
                        twists = result.Delta.Twists,
                        variables = result.Delta.Variables,
                        adjustments = result.Adjustments,
                    }, cancellationToken);
                }
                catch (SeedException ex)
                {
                    logger.LogInformation("Delta da IA não formou um desafio válido: {Error}", ex.Message);
                }
            }

            note = "A IA local não conseguiu montar um desafio válido com esse contexto, então busquei na biblioteca. ";
        }
        else
        {
            note = "Sem IA ativa, eu busco na biblioteca curada. ";
        }

        var suggestions = matched.Count > 0 ? matched.Take(3).ToList() : library.Search(null, difficulty, 3).ToList();
        var terms = matched.SelectMany(m => m.MatchedTerms).Distinct(StringComparer.Ordinal).Take(6).ToList();
        var message = matched.Count > 0
            ? $"{note}Estes desafios casam com {(terms.Count == 1 ? "o termo" : "os termos")} {string.Join(", ", terms.Select(t => $"“{t}”"))}:"
            : $"{note}Nenhum desafio casou com esses termos; algumas sugestões para começar:";
        return await AddAsync(conversation, "assistant", message, MessageSource.Library, payload: new
        {
            kind = "suggestions",
            provider = request.Provider,
            difficulty = difficulty?.ToString().ToLowerInvariant(),
            mode = request.Mode,
            suggestions,
        }, cancellationToken: cancellationToken);
    }

    private async Task<ConversationMessageEntity> InterviewReplyAsync(ConversationEntity conversation, ConversationMessageEntity user, CancellationToken cancellationToken)
    {
        var brief = await library.BriefAsync(conversation.ChallengeId!.Value, cancellationToken)
            ?? throw new ConversationException("Desafio não encontrado.", StatusCodes.Status404NotFound);
        var history = (await db.ConversationMessages.AsNoTracking()
                .Where(m => m.ConversationId == conversation.Id && m.Number < user.Number)
                .OrderByDescending(m => m.Number).Take(Interviewer.MaxHistoryTurns).ToListAsync(cancellationToken))
            .OrderBy(m => m.Number).Select(m => new ChatTurn(m.Role, m.Text)).ToList();
        var context = new InterviewContext(brief.Title, brief.Statement, brief.Clarifications.Select(c => new CuratedAnswer(c.Question, c.Keywords, c.Answer)).ToList());

        var answer = await interviewer.AnswerAsync(new InterviewRequest(context, history, user.Text),
            async delta => await notifier.ChatDelta(new ChatDeltaEvent(conversation.Id, delta)), cancellationToken);
        return await AddAsync(conversation, "assistant", answer.Text, answer.Source, answer.Model, answer.PromptVersion,
            answer.MatchedQuestion is null ? null : new { matchedQuestion = answer.MatchedQuestion }, cancellationToken);
    }

    private static Difficulty? ParseDifficulty(string? difficulty)
    {
        if (string.IsNullOrWhiteSpace(difficulty)) return null;
        try
        {
            return Lab.Content.SeedLoader.ParseDifficulty(difficulty);
        }
        catch (Lab.Content.ContentException ex)
        {
            throw new ConversationException(ex.Message);
        }
    }

    private async Task<ConversationMessageEntity> AddAsync(
        ConversationEntity conversation, string role, string text, string source, string? model = null, string? promptVersion = null, object? payload = null,
        CancellationToken cancellationToken = default)
    {
        var last = await db.ConversationMessages.Where(m => m.ConversationId == conversation.Id).MaxAsync(m => (int?)m.Number, cancellationToken) ?? 0;
        var pending = db.ChangeTracker.Entries<ConversationMessageEntity>()
            .Where(e => e.State == EntityState.Added && e.Entity.ConversationId == conversation.Id).Select(e => e.Entity.Number).DefaultIfEmpty(0).Max();
        var message = new ConversationMessageEntity
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            Number = Math.Max(last, pending) + 1,
            Role = role,
            Text = text,
            Source = source,
            Model = model,
            PromptVersion = promptVersion,
            PayloadJson = payload is null ? null : Json.Serialize(payload),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.ConversationMessages.Add(message);
        return message;
    }

    private static MessageView View(ConversationMessageEntity m) =>
        new(m.Id, m.Number, m.Role, m.Text, m.Source, m.Model, m.PromptVersion, m.CreatedAt, m.PayloadJson is null ? null : JsonNode.Parse(m.PayloadJson));
}

public static class ConversationEndpoints
{
    public static void MapConversations(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/ai/status", async (AiRuntime ai, CancellationToken ct) => Results.Ok(await ai.StatusAsync(ct))).WithTags("IA");

        var group = app.MapGroup("/api/conversations").WithTags("IA");

        group.MapPost("", async (CreateConversationRequest request, ConversationService conversations, CancellationToken ct) =>
        {
            try
            {
                var conversation = await conversations.CreateAsync(request, ct);
                return Results.Created($"/api/conversations/{conversation.Id}", await conversations.ViewAsync(conversation.Id, ct));
            }
            catch (ConversationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: ex.Status);
            }
        });

        group.MapGet("", async (string? kind, LabDbContext db, int? limit, CancellationToken ct) =>
        {
            var query = db.Conversations.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(c => c.Kind == kind);
            var rows = await query.OrderByDescending(c => c.UpdatedAt).Take(Math.Clamp(limit ?? 20, 1, 100)).ToListAsync(ct);
            return Results.Ok(rows.Select(c => new ConversationSummary(c.Id, c.Kind, c.ChallengeId, c.Title, c.UpdatedAt)));
        });

        group.MapGet("/{id:guid}", async (Guid id, ConversationService conversations, CancellationToken ct) =>
            await conversations.ViewAsync(id, ct) is { } view ? Results.Ok(view) : Results.NotFound());

        group.MapPost("/{id:guid}/messages", async (Guid id, SendMessageRequest request, ConversationService conversations, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await conversations.SendAsync(id, request, ct));
            }
            catch (ConversationException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: ex.Status);
            }
        });
    }
}
