using System.Text.Json.Nodes;
using Lab.Ai;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Jobs;
using Lab.Modules.Shared.Persistence;
using Lab.Modules.Shared.Realtime;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Evaluation;

public sealed record NarrativeJob(Guid EvaluationId);

public static class NarrativeScheduling
{
    public const string JobType = "evaluation.narrate";

    /// <summary>Marca a narrativa como pendente e grava o job no mesmo contexto (o chamador salva).</summary>
    public static void Schedule(LabDbContext db, TimeProvider clock, EvaluationEntity evaluation)
    {
        evaluation.NarrativeJson = Json.Serialize(new NarrativeState(NarrativeState.Pending, [], null, null,
            "A IA local está escrevendo a narrativa da revisão.", clock.GetUtcNow().UtcDateTime));
        JobScheduler.Add(db, clock, JobType, new NarrativeJob(evaluation.Id));
    }

    public static NarrativeState? Read(EvaluationEntity evaluation) =>
        evaluation.NarrativeJson is null ? null : Json.Deserialize<NarrativeState>(evaluation.NarrativeJson);
}

/// <summary>
/// Escreve a narrativa de uma submissão avaliada. A nota e a revisão determinística já estão gravadas: aqui só entra
/// texto, e só se passar na checagem de números citados.
/// </summary>
public sealed class NarrativeJobHandler(
    LabDbContext db,
    IChallengeQueries challenges,
    IReviewNarrator narrator,
    AiRuntime ai,
    ILabNotifier notifier,
    TimeProvider clock) : IJobHandler
{
    public string Type => NarrativeScheduling.JobType;

    public async Task HandleAsync(Guid jobId, string payloadJson, CancellationToken cancellationToken)
    {
        var job = Json.Deserialize<NarrativeJob>(payloadJson);
        var evaluation = await db.Evaluations.FirstOrDefaultAsync(e => e.Id == job.EvaluationId, cancellationToken);
        if (evaluation?.ResultJson is null || evaluation.Kind != EvaluationKind.Submission) return;
        if (NarrativeScheduling.Read(evaluation)?.Status == NarrativeState.Ready) return;

        var title = (await challenges.FindAsync(evaluation.ChallengeId, cancellationToken))?.Title ?? "";
        var result = JsonNode.Parse(evaluation.ResultJson)!;
        var request = new NarrationRequest(title, evaluation.FinalScore ?? 0, evaluation.Rank ?? "", NarrationInput.ReviewJson(result));
        var narrative = await narrator.NarrateAsync(request, cancellationToken);

        NarrativeState state;
        if (narrative is not null)
        {
            state = new NarrativeState(NarrativeState.Ready, narrative.Paragraphs, narrative.Model, narrative.PromptVersion, null, clock.GetUtcNow().UtcDateTime);
        }
        else
        {
            var status = await ai.StatusAsync(cancellationToken);
            state = status.Mode == AiMode.None
                ? new NarrativeState(NarrativeState.Unavailable, [], null, null, status.Detail, clock.GetUtcNow().UtcDateTime)
                : new NarrativeState(NarrativeState.Rejected, [], status.Model, null,
                    "A narrativa do modelo citava números que não estão na revisão e foi descartada; a revisão determinística continua valendo.",
                    clock.GetUtcNow().UtcDateTime);
        }

        evaluation.NarrativeJson = Json.Serialize(state);
        await db.SaveChangesAsync(cancellationToken);
        await notifier.NarrativeReady(new NarrativeReadyEvent(evaluation.Id, evaluation.AttemptId, state.Status));
    }

    public async Task OnFailedAsync(string payloadJson, string error, CancellationToken cancellationToken)
    {
        var job = Json.Deserialize<NarrativeJob>(payloadJson);
        var evaluation = await db.Evaluations.FirstOrDefaultAsync(e => e.Id == job.EvaluationId, cancellationToken);
        if (evaluation is null) return;
        evaluation.NarrativeJson = Json.Serialize(new NarrativeState(NarrativeState.Failed, [], null, null, error, clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        await notifier.NarrativeReady(new NarrativeReadyEvent(evaluation.Id, evaluation.AttemptId, NarrativeState.Failed));
    }
}

/// <summary>
/// O que o narrador recebe: a revisão determinística enxuta (cabe no contexto de um modelo pequeno), com os números já
/// arredondados como devem ser citados.
/// </summary>
public static class NarrationInput
{
    public const int MaxImprovements = 5;

    public static string ReviewJson(JsonNode result)
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
