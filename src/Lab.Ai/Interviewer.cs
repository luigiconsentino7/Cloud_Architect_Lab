using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Lab.Ai;

/// <summary>Modo sem IA: responde com o esclarecimento curado que melhor casa com a pergunta, e diz quando não sabe.</summary>
public static class CuratedInterviewer
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "qual", "quais", "como", "quando", "onde", "quem", "quanto", "quantos", "quantas", "para", "pela", "pelo", "pelos", "pelas",
        "com", "sem", "uma", "uns", "umas", "que", "dos", "das", "nos", "nas", "num", "numa", "isso", "esse", "essa", "este", "esta",
        "existe", "tem", "ter", "sao", "ser", "vai", "pode", "posso", "devo", "deve", "precisa", "preciso", "sobre", "entre", "mais",
        "menos", "muito", "tambem", "ainda", "algum", "alguma", "sistema", "desafio",
    };

    public static InterviewAnswer Answer(InterviewContext context, string question)
    {
        var match = Match(context.Clarifications, question);
        if (match is not null) return new InterviewAnswer(match.Answer, AnswerSource.Curated, null, null, match.Question);

        var text = new StringBuilder("Não tenho uma resposta pronta para isso. Sem IA, respondo só com os esclarecimentos curados do desafio");
        if (context.Clarifications.Count == 0)
        {
            text.Append(", e este desafio não traz nenhum: use o enunciado, as restrições e as dicas.");
        }
        else
        {
            text.Append(". Perguntas que sei responder:");
            foreach (var faq in context.Clarifications.Take(6)) text.Append(CultureInfo.InvariantCulture, $"\n• {faq.Question}");
        }

        return new InterviewAnswer(text.ToString(), AnswerSource.Curated, null, null, null);
    }

    /// <summary>Palavras-chave da curadoria valem 3; palavras em comum com a pergunta curada valem 1. Exige 3 pontos.</summary>
    public static CuratedAnswer? Match(IReadOnlyList<CuratedAnswer> answers, string question)
    {
        var normalized = $" {AiText.Normalize(question)} ";
        var words = Words(normalized);
        CuratedAnswer? best = null;
        var bestScore = 0;
        foreach (var answer in answers)
        {
            var score = answer.Keywords.Select(AiText.Normalize).Where(k => k.Length > 0).Distinct(StringComparer.Ordinal)
                .Count(k => normalized.Contains($" {k} ", StringComparison.Ordinal) || (k.Length >= 5 && normalized.Contains(k, StringComparison.Ordinal))) * 3;
            score += Words($" {AiText.Normalize(answer.Question)} ").Intersect(words, StringComparer.Ordinal).Count();
            if (score > bestScore)
            {
                best = answer;
                bestScore = score;
            }
        }

        return bestScore >= 3 ? best : null;
    }

    private static HashSet<string> Words(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !StopWords.Contains(w)).ToHashSet(StringComparer.Ordinal);
}

/// <summary>
/// Entrevistador: com o Ollama, chat livre ancorado na parte pública do desafio (ele não conhece a referência nem os
/// cenários ocultos, então não tem o que vazar); sem IA ou se o modelo falhar, as respostas curadas.
/// </summary>
public sealed class Interviewer(AiRuntime runtime, PromptLibrary prompts, ILogger<Interviewer> logger) : IInterviewer
{
    public const int MaxHistoryTurns = 8;
    public const int MaxQuestionLength = 1500;

    public async Task<InterviewAnswer> AnswerAsync(InterviewRequest request, Func<string, ValueTask>? onDelta, CancellationToken cancellationToken)
    {
        var client = prompts.Has(PromptRoles.Interviewer) ? await runtime.ClientAsync(cancellationToken) : null;
        if (client is null) return CuratedInterviewer.Answer(request.Context, request.Question);

        var prompt = prompts.Get(PromptRoles.Interviewer);
        var text = new StringBuilder();
        try
        {
            using var deadline = runtime.Deadline(cancellationToken);
            await foreach (var update in client.GetStreamingResponseAsync(Messages(prompt, request), runtime.ChatOptions(temperature: 0.5f, maxOutputTokens: 500), deadline.Token))
            {
                var delta = update.Text;
                if (string.IsNullOrEmpty(delta)) continue;
                text.Append(delta);
                if (onDelta is not null) await onDelta(delta);
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A IA local falhou no entrevistador; respondendo com o conteúdo curado.");
            runtime.Invalidate();
            var fallback = CuratedInterviewer.Answer(request.Context, request.Question);
            return fallback with { Text = $"(A IA local não respondeu; segue a resposta curada.)\n\n{fallback.Text}" };
        }

        var answer = text.ToString().Trim();
        return answer.Length == 0
            ? CuratedInterviewer.Answer(request.Context, request.Question)
            : new InterviewAnswer(answer, AnswerSource.Ai, runtime.Options.Ollama.Model, prompt.Id, null);
    }

    public static List<ChatMessage> Messages(PromptTemplate prompt, InterviewRequest request)
    {
        var system = new StringBuilder(prompt.Text);
        system.Append("\n\n<desafio>\n").Append(CultureInfo.InvariantCulture, $"Título: {request.Context.Title}\n").Append(request.Context.Statement).Append("\n</desafio>");
        if (request.Context.Clarifications.Count > 0)
        {
            system.Append("\n<esclarecimentos_curados>");
            foreach (var c in request.Context.Clarifications) system.Append(CultureInfo.InvariantCulture, $"\n- Pergunta: {c.Question}\n  Resposta: {c.Answer}");
            system.Append("\n</esclarecimentos_curados>");
        }

        var messages = new List<ChatMessage> { new(ChatRole.System, system.ToString()) };
        foreach (var turn in request.History.TakeLast(MaxHistoryTurns))
            messages.Add(new ChatMessage(turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User, turn.Text));
        messages.Add(new ChatMessage(ChatRole.User, AiText.Clean(request.Question, MaxQuestionLength)));
        return messages;
    }
}
