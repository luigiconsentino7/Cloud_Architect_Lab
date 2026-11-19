using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Lab.Ai;

/// <summary>
/// Sugere o componente do catálogo para uma forma desenhada à mão cujo rótulo não casou com nenhum alias. O modelo
/// descreve a função do rótulo, decide se é um componente ou só uma anotação e só então escolhe o id (nos evals reais,
/// os modelos tipavam anotações como serviço ou recusavam tudo quando "nenhum" era uma opção entre 30 ids); o schema só
/// aceita ids da paleta do provedor ou "nenhum".
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
            // O modelo decidiu que é uma anotação: nada a sugerir (a resposta sem o campo vale pelo catalogId).
            if (json?["componente"] is JsonValue gate && gate.TryGetValue<bool>(out var isComponent) && !isComponent) return null;
            var catalogId = json?["catalogId"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
            if (catalogId is null || catalogId == None || request.Candidates.All(c => c.CatalogId != catalogId)) return null;
            // A descrição da função que o modelo deu ao rótulo faz de motivo da sugestão.
            var reason = json?["funcao"] is JsonValue r && r.TryGetValue<string>(out var text) ? AiText.Clean(text, 200) : "";
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
                ["funcao"] = new JsonObject { ["type"] = "string" },
                ["componente"] = new JsonObject { ["type"] = "boolean" },
                ["catalogId"] = new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(candidates.Select(c => (JsonNode)JsonValue.Create(c.CatalogId)).Append(JsonValue.Create(None)).ToArray()),
                },
            },
            ["required"] = new JsonArray("funcao", "componente", "catalogId"),
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
