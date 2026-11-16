using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Lab.Ai;

/// <summary>
/// Contexto → desafio com o modelo local. O modelo escolhe uma das sementes candidatas e devolve só um delta
/// (narrativa curta, variáveis dentro das faixas, pegadinhas da lista); o resto é determinístico.
/// </summary>
public sealed class ChallengePersonalizer(AiRuntime runtime, PromptLibrary prompts, ILogger<ChallengePersonalizer> logger) : IChallengePersonalizer
{
    public const int MaxContextLength = 2000;

    public async Task<PersonalizationResult?> PersonalizeAsync(PersonalizationRequest request, CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0 || !prompts.Has(PromptRoles.Personalizer)) return null;
        var client = await runtime.ClientAsync(cancellationToken);
        if (client is null) return null;

        var prompt = prompts.Get(PromptRoles.Personalizer);
        var options = runtime.ChatOptions(temperature: 0.4f, maxOutputTokens: 700);
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema(request.Candidates), "delta_desafio", "Delta de personalização sobre uma semente");
        List<ChatMessage> messages = [new(ChatRole.System, prompt.Text), new(ChatRole.User, UserMessage(request))];
        try
        {
            using var deadline = runtime.Deadline(cancellationToken);
            var response = await client.GetResponseAsync(messages, options, deadline.Token);
            var json = AiText.ParseObject(response.Text);
            var validated = json is null ? null : DeltaValidator.Validate(json, request);
            if (validated is null)
            {
                logger.LogInformation("Personalização descartada: a resposta do modelo não passou na validação.");
                return null;
            }

            return new PersonalizationResult(validated.Value.Delta, runtime.Options.Ollama.Model, prompt.Id, validated.Value.Adjustments);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A IA local falhou ao personalizar o desafio; seguindo com a busca guiada.");
            runtime.Invalidate();
            return null;
        }
    }

    /// <summary>JSON Schema da resposta: o Ollama vira isso numa gramática, então só sai id que existe.</summary>
    public static JsonElement Schema(IReadOnlyList<SeedCandidate> candidates)
    {
        var variableNames = candidates.SelectMany(c => c.Variables.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var twistIds = candidates.SelectMany(c => c.Twists.Select(t => t.Id)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        static JsonArray Enum(IEnumerable<string> values) => new(values.Select(v => (JsonNode)JsonValue.Create(v)).ToArray());

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["seedId"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(candidates.Select(c => c.SeedId)) },
                ["narrative"] = new JsonObject { ["type"] = "string" },
                ["variables"] = variableNames.Count == 0
                    ? new JsonObject { ["type"] = "array", ["maxItems"] = 0 }
                    : new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["name"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(variableNames) },
                                ["value"] = new JsonObject { ["type"] = "number" },
                            },
                            ["required"] = new JsonArray("name", "value"),
                        },
                    },
                ["twists"] = twistIds.Count == 0
                    ? new JsonObject { ["type"] = "array", ["maxItems"] = 0 }
                    : new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string", ["enum"] = Enum(twistIds) } },
                ["clarificationsExtra"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" }, ["a"] = new JsonObject { ["type"] = "string" } },
                        ["required"] = new JsonArray("q", "a"),
                    },
                },
                ["reason"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("seedId", "narrative", "variables", "twists", "reason"),
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    /// <summary>O contexto entra delimitado, como dado: instruções dentro dele não valem.</summary>
    public static string UserMessage(PersonalizationRequest request)
    {
        var text = new StringBuilder();
        text.AppendLine("<contexto_do_usuario>");
        text.AppendLine(AiText.Clean(request.Context, MaxContextLength));
        text.AppendLine("</contexto_do_usuario>");
        text.AppendLine(CultureInfo.InvariantCulture, $"Dificuldade escolhida: {request.Difficulty}");
        text.AppendLine("<sementes>");
        foreach (var seed in request.Candidates)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"- seedId: {seed.SeedId}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  titulo: {seed.Title}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  resumo: {seed.Summary}");
            text.AppendLine(CultureInfo.InvariantCulture, $"  trilha: {seed.Track}; tags: {string.Join(", ", seed.Tags)}");
            if (seed.Variables.Count > 0)
            {
                text.AppendLine("  variaveis:");
                foreach (var (name, range) in seed.Variables.OrderBy(v => v.Key, StringComparer.Ordinal))
                {
                    text.AppendLine(CultureInfo.InvariantCulture,
                        $"    - {name}: de {range.Min.ToString(CultureInfo.InvariantCulture)} a {range.Max.ToString(CultureInfo.InvariantCulture)} ({range.Description})");
                }
            }

            if (seed.Twists.Count > 0)
            {
                text.AppendLine("  pegadinhas:");
                foreach (var twist in seed.Twists) text.AppendLine(CultureInfo.InvariantCulture, $"    - {twist.Id}: {twist.Title}. {twist.Description}");
            }
        }

        text.AppendLine("</sementes>");
        return text.ToString();
    }
}

/// <summary>Validação semântica do delta: o que não confere é corrigido (faixas) ou descartado (ids), nunca aceito.</summary>
public static class DeltaValidator
{
    public const int MaxTwists = 2;
    public const int MaxNarrative = 700;
    public const int MaxExtraClarifications = 3;

    public static (ChallengeDelta Delta, IReadOnlyList<string> Adjustments)? Validate(JsonObject json, PersonalizationRequest request)
    {
        var seedId = Str(json["seedId"]);
        var seed = request.Candidates.FirstOrDefault(c => c.SeedId == seedId);
        if (seed is null) return null;

        var adjustments = new List<string>();
        var variables = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var item in (json["variables"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Str(item["name"]);
            if (name is null || !seed.Variables.TryGetValue(name, out var range))
            {
                adjustments.Add($"variável '{name}' não existe nesta semente: ignorada");
                continue;
            }

            if (item["value"] is not JsonValue raw || !raw.TryGetValue<double>(out var value) || !double.IsFinite(value))
            {
                adjustments.Add($"{name}: valor inválido, ignorado");
                continue;
            }

            var snapped = Snap(value, range);
            if (Math.Abs(snapped - value) > 1e-9)
                adjustments.Add(string.Create(CultureInfo.InvariantCulture, $"{name}: {value} ajustado para {snapped} (faixa {range.Min}–{range.Max})"));
            variables[name] = snapped;
        }

        var twists = new List<string>();
        foreach (var id in (json["twists"] as JsonArray ?? []).Select(Str).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            if (seed.Twists.All(t => t.Id != id)) adjustments.Add($"pegadinha '{id}' não vale para esta semente/dificuldade: ignorada");
            else if (twists.Count >= MaxTwists) adjustments.Add($"pegadinha '{id}' ignorada (no máximo {MaxTwists})");
            else twists.Add(id);
        }

        var narrative = AiText.Clean(Str(json["narrative"]), MaxNarrative);
        var unknown = AiText.UnknownNumbers(narrative, variables.Values.Concat(seed.Variables.Values.SelectMany(r => new[] { r.Min, r.Max })));
        if (unknown.Any(n => n >= 100))
        {
            adjustments.Add("narrativa descartada: citava números que não estão no desafio");
            narrative = "";
        }

        var extra = (json["clarificationsExtra"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => new ExtraClarification(AiText.Clean(Str(c["q"]), 200), AiText.Clean(Str(c["a"]), 400)))
            .Where(c => c.Question.Length > 0 && c.Answer.Length > 0)
            .Take(MaxExtraClarifications)
            .ToList();

        var delta = new ChallengeDelta
        {
            SeedId = seed.SeedId,
            Narrative = narrative.Length == 0 ? null : narrative,
            Variables = variables,
            Twists = twists,
            ClarificationsExtra = extra,
            Reason = AiText.Clean(Str(json["reason"]), 300),
        };
        return (delta, adjustments);
    }

    /// <summary>Prende o valor na faixa e no passo da variável.</summary>
    public static double Snap(double value, VariableRange range)
    {
        var clamped = Math.Clamp(value, range.Min, range.Max);
        if (range.Step <= 0) return clamped;
        var stepped = range.Min + (Math.Round((clamped - range.Min) / range.Step) * range.Step);
        return Math.Round(Math.Clamp(stepped, range.Min, range.Max), 6);
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
