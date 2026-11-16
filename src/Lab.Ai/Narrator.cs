using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Lab.Ai;

/// <summary>
/// Narrativa da revisão: o modelo reescreve a revisão determinística em texto corrido, de mentor. Só pode citar números
/// que estão na revisão; se citar outro, ganha uma chance de corrigir e, se insistir, a narrativa é descartada.
/// </summary>
public sealed class ReviewNarrator(AiRuntime runtime, PromptLibrary prompts, ILogger<ReviewNarrator> logger) : IReviewNarrator
{
    public const int MaxParagraphs = 4;
    public const int MaxParagraphLength = 900;

    private static readonly JsonElement Schema = JsonSerializer.SerializeToElement(new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["paragraphs"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["minItems"] = 1, ["maxItems"] = MaxParagraphs },
        },
        ["required"] = new JsonArray("paragraphs"),
    });

    public async Task<ReviewNarrative?> NarrateAsync(NarrationRequest request, CancellationToken cancellationToken)
    {
        var client = prompts.Has(PromptRoles.Narrator) ? await runtime.ClientAsync(cancellationToken) : null;
        if (client is null) return null;

        var prompt = prompts.Get(PromptRoles.Narrator);
        var options = runtime.ChatOptions(temperature: 0.3f, maxOutputTokens: 900);
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "narrativa", "Narrativa da revisão em parágrafos curtos");
        var messages = new List<ChatMessage> { new(ChatRole.System, prompt.Text), new(ChatRole.User, UserMessage(request)) };
        try
        {
            using var deadline = runtime.Deadline(cancellationToken);
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var response = await client.GetResponseAsync(messages, options, deadline.Token);
                var paragraphs = Paragraphs(response.Text);
                var problems = Check(paragraphs, request);
                if (problems.Count == 0) return new ReviewNarrative(paragraphs, runtime.Options.Ollama.Model, prompt.Id);

                logger.LogInformation("Narrativa rejeitada (tentativa {Attempt}): {Problems}", attempt, string.Join("; ", problems));
                messages.Add(new ChatMessage(ChatRole.Assistant, response.Text));
                messages.Add(new ChatMessage(ChatRole.User,
                    $"A narrativa não pode ser usada: {string.Join("; ", problems)}. Reescreva usando só números que aparecem na <revisao>, copiados como estão."));
            }

            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Falha do modelo (fora do ar, timeout) sobe para o job tentar de novo com backoff.
            logger.LogWarning(ex, "A IA local falhou ao narrar a revisão; a revisão determinística continua valendo.");
            runtime.Invalidate();
            throw;
        }
    }

    public static string UserMessage(NarrationRequest request) =>
        string.Create(CultureInfo.InvariantCulture,
            $"Desafio: {request.Title}\nNota final: {request.FinalScore} (rank {request.Rank})\n<revisao>\n{request.ReviewJson}\n</revisao>");

    public static IReadOnlyList<string> Paragraphs(string? text) =>
        (AiText.ParseObject(text)?["paragraphs"] as JsonArray ?? [])
            .Select(p => p is JsonValue v && v.TryGetValue<string>(out var s) ? AiText.Clean(s, MaxParagraphLength) : "")
            .Where(p => p.Length > 0)
            .Take(MaxParagraphs)
            .ToList();

    /// <summary>Problemas que impedem a narrativa de ir para a tela (vazia, ou com número que a revisão não tem).</summary>
    public static IReadOnlyList<string> Check(IReadOnlyList<string> paragraphs, NarrationRequest request)
    {
        if (paragraphs.Count == 0) return ["a resposta veio vazia"];
        var allowed = AiText.Numbers(request.ReviewJson).Append(request.FinalScore).Concat(AiText.Numbers(request.Title));
        var unknown = AiText.UnknownNumbers(string.Join(' ', paragraphs), allowed);
        return unknown.Count == 0
            ? []
            : [$"cita números que não estão na revisão ({string.Join(", ", unknown.Select(n => n.ToString("#,0.###", CultureInfo.GetCultureInfo("pt-BR"))))})"];
    }
}
