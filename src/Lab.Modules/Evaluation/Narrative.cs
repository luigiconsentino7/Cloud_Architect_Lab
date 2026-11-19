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
        var request = new NarrationRequest(title, evaluation.FinalScore ?? 0, evaluation.Rank ?? "", ReviewDigest.FromResult(result));
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
