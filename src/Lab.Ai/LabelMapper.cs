using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Lab.Ai;

/// <summary>
/// Sugere o componente do catálogo para uma forma desenhada à mão cujo rótulo não casou com nenhum alias. O schema só
/// aceita ids da paleta do provedor ou "nenhum" (anotação de texto livre), então não sai componente inventado.
/// </summary>
public sealed class LabelMapper(AiRuntime runtime, PromptLibrary prompts, ILogger<LabelMapper> logger) : ILabelMapper
{
    public const string None = "nenhum";

    public async Task<LabelSuggestion?> SuggestAsync(LabelMappingRequest request, CancellationToken cancellationToken)
    {
        if (request.Candidates.Count == 0 || string.IsNullOrWhiteSpace(request.Label) || !prompts.Has(PromptRoles.Mapper)) return null;
        var client = await runtime.ClientAsync(cancellationToken);
        if (client is null) return null;

        var prompt = prompts.Get(PromptRoles.Mapper);
        var options = runtime.ChatOptions(temperature: 0.1f, maxOutputTokens: 120);
        options.ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema(request.Candidates), "mapeamento", "Componente do catálogo para o rótulo");
        List<ChatMessage> messages = [new(ChatRole.System, prompt.Text), new(ChatRole.User, UserMessage(request))];
        try
        {
            using var deadline = runtime.Deadline(cancellationToken);
            var response = await client.GetResponseAsync(messages, options, deadline.Token);
            var json = AiText.ParseObject(response.Text);
            var catalogId = json?["catalogId"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
            if (catalogId is null || catalogId == None || request.Candidates.All(c => c.CatalogId != catalogId)) return null;
            var reason = json?["reason"] is JsonValue r && r.TryGetValue<string>(out var text) ? AiText.Clean(text, 200) : "";
            return new LabelSuggestion(catalogId, reason, runtime.Options.Ollama.Model, prompt.Id);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "A IA local falhou ao sugerir o componente de \"{Label}\".", request.Label);
            runtime.Invalidate();
            return null;
        }
    }

    public static JsonElement Schema(IReadOnlyList<MappingCandidate> candidates) =>
        JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["catalogId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(candidates.Select(c => (JsonNode)JsonValue.Create(c.CatalogId)).Append(JsonValue.Create(None)).ToArray()),
                },
                ["reason"] = new JsonObject { ["type"] = "string" },
            },
            ["required"] = new JsonArray("catalogId", "reason"),
        });

    /// <summary>O rótulo entra delimitado, como dado.</summary>
    public static string UserMessage(LabelMappingRequest request)
    {
        var text = new StringBuilder();
        text.AppendLine("<rotulo>");
        text.AppendLine(AiText.Clean(request.Label, 120));
        text.AppendLine("</rotulo>");
        if (!string.IsNullOrWhiteSpace(request.Near)) text.AppendLine(CultureInfo.InvariantCulture, $"Componente mais próximo no desenho: {AiText.Clean(request.Near, 80)}");
        text.AppendLine("<catalogo>");
        foreach (var c in request.Candidates) text.AppendLine(CultureInfo.InvariantCulture, $"- {c.CatalogId}: {c.DisplayName} ({c.Archetype})");
        text.AppendLine("</catalogo>");
        return text.ToString();
    }
}
