using System.Text.Json.Nodes;

namespace Lab.Ai;

/// <summary>
/// O que o narrador recebe: a revisão determinística enxuta (cabe no contexto de um modelo pequeno), com os números já
/// arredondados como devem ser citados.
/// </summary>
public static class ReviewDigest
{
    public const int MaxImprovements = 5;

    /// <param name="result">O <c>EvaluationResult</c> serializado (camelCase), como fica gravado na avaliação.</param>
    public static string FromResult(JsonNode result)
    {
        var score = result["score"];
        var review = result["review"];
        var cost = result["cost"];
        var compact = new JsonObject
        {
            ["nota"] = Int(score?["finalScore"]),
            ["rank"] = Str(score?["rank"]),
            ["teto"] = score?["cap"] is JsonObject cap && cap["applied"]?.GetValue<bool>() == true
                ? new JsonObject { ["valor"] = Int(cap["value"]), ["motivo"] = Str(cap["reason"]) }
                : null,
            ["dimensoes"] = new JsonArray((score?["dimensions"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(d => (JsonNode)new JsonObject { ["dimensao"] = Str(d["dimension"]), ["nota"] = Round(d["score"], 0) }).ToArray()),
            ["resumo"] = new JsonArray((review?["summary"] as JsonArray ?? []).OfType<JsonObject>().Select(s => (JsonNode?)Str(s["text"])).ToArray()),
            ["melhorias"] = new JsonArray((review?["improvements"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxImprovements)
                .Select(i => (JsonNode)new JsonObject
                {
                    ["titulo"] = Str(i["title"]),
                    ["severidade"] = Str(i["severity"]),
                    ["dimensao"] = Str(i["dimension"]),
                    ["pontosPerdidos"] = Round(i["impact"], 1),
                    ["evidencias"] = new JsonArray((i["evidence"] as JsonArray ?? []).Take(3).Select(e => (JsonNode?)Str(e)).ToArray()),
                    ["comoCorrigir"] = Str(i["how"]),
                }).ToArray()),
            ["pontosFortes"] = new JsonArray((review?["strengths"] as JsonArray ?? []).OfType<JsonObject>().Take(5)
                .Select(s => (JsonNode?)$"{Str(s["title"])}: {Str(s["detail"])}").ToArray()),
            ["followUps"] = new JsonArray((review?["followUps"] as JsonArray ?? []).Take(3).Select(f => (JsonNode?)Str(f)).ToArray()),
            ["custoMensalUsd"] = Round(cost?["monthlyUsd"], 0),
            ["orcamentoMensalUsd"] = Round(cost?["budgetUsd"], 0),
        };
        return compact.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)Math.Round(d) : null;

    private static double? Round(JsonNode? node, int digits) => node is JsonValue v && v.TryGetValue<double>(out var d) ? Math.Round(d, digits) : null;
}
