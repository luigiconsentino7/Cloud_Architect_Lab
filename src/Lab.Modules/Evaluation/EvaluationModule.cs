using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Scene;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Design.Contracts;
using Lab.Modules.Evaluation.Contracts;
using Lab.Modules.Gamification.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Jobs;
using Lab.Modules.Shared.Outbox;
using Lab.Modules.Shared.Persistence;
using Lab.Modules.Shared.Realtime;
using Lab.Pipeline;
using Lab.Scoring;
using Lab.Simulation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Evaluation;

public sealed class EvaluationScheduler(TimeProvider clock) : IEvaluationScheduler
{
    public const string JobType = "evaluation.run";

    public Guid Schedule(LabDbContext db, Guid attemptId, Guid challengeId, Guid snapshotId, string kind)
    {
        var evaluation = new EvaluationEntity
        {
            Id = Guid.CreateVersion7(),
            AttemptId = attemptId,
            ChallengeId = challengeId,
            SnapshotId = snapshotId,
            Kind = kind,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Evaluations.Add(evaluation);
        JobScheduler.Add(db, clock, JobType, new EvaluationJob(evaluation.Id));
        return evaluation.Id;
    }
}

public sealed record EvaluationJob(Guid EvaluationId);

/// <summary>Roda o pipeline do motor para uma avaliação. Idempotente: uma avaliação concluída não roda de novo.</summary>
public sealed class EvaluationJobHandler(
    LabDbContext db,
    IAttemptQueries attempts,
    IChallengeQueries challenges,
    IGamificationQueries gamification,
    LabContent content,
    ILabNotifier notifier,
    OutboxSignal outbox,
    LabOptions options,
    TimeProvider clock) : IJobHandler
{
    public string Type => EvaluationScheduler.JobType;

    public async Task HandleAsync(Guid jobId, string payloadJson, CancellationToken cancellationToken)
    {
        var job = Json.Deserialize<EvaluationJob>(payloadJson);
        var evaluation = await db.Evaluations.FirstOrDefaultAsync(e => e.Id == job.EvaluationId, cancellationToken)
            ?? throw new InvalidOperationException($"Avaliação {job.EvaluationId} não existe.");
        if (evaluation.Status == EvaluationStatus.Completed) return;

        var attempt = await attempts.FindAsync(evaluation.AttemptId, cancellationToken) ?? throw new InvalidOperationException("Tentativa não existe.");
        var challenge = await challenges.FindAsync(evaluation.ChallengeId, cancellationToken) ?? throw new InvalidOperationException("Desafio não existe.");
        var spec = await challenges.SpecAsync(evaluation.ChallengeId, cancellationToken) ?? throw new InvalidOperationException("Desafio não existe.");
        var snapshot = await attempts.SnapshotAsync(evaluation.SnapshotId, cancellationToken) ?? throw new InvalidOperationException("Snapshot não existe.");
        var submission = evaluation.Kind == EvaluationKind.Submission;

        evaluation.Status = EvaluationStatus.Running;
        evaluation.StartedAt ??= clock.GetUtcNow().UtcDateTime;
        evaluation.Stage = "compile";
        await db.SaveChangesAsync(cancellationToken);
        if (submission) await attempts.SetStatusAsync(attempt.Id, "evaluating", cancellationToken);

        var referenceCost = await challenges.ReferenceCostAsync(challenge.Id, cancellationToken);
        if (referenceCost is null && spec.Reference is not null)
        {
            referenceCost = await Task.Run(() => ReferenceCost.Estimate(spec, content.Catalog, options.MaxParallelism, cancellationToken), cancellationToken);
            if (referenceCost is { } cost) await challenges.SaveReferenceCostAsync(challenge.Id, cost, cancellationToken);
        }

        // Simulação de rascunho só roda os cenários públicos: os ocultos ficam para a submissão.
        var scenarioIds = submission ? null : spec.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Public).Select(s => s.Id).ToList();
        var progress = new ProgressRelay(evaluation, attempt.Id, notifier);
        var request = new EvaluationRequest
        {
            Challenge = spec,
            Catalog = content.Catalog,
            RuleTexts = content.RuleTexts,
            Scene = JsonNode.Parse(snapshot.SceneJson),
            Flows = Json.Deserialize<List<FlowDefinition>>(snapshot.FlowsJson),
            ScenarioIds = scenarioIds,
            ReferenceCostUsd = referenceCost,
            EventBudget = options.EventBudget,
            MaxParallelism = options.MaxParallelism,
            Xp = new XpContext
            {
                FirstAttempt = attempt.Number == 1,
                HintLevelsUsed = attempt.HintLevelsUsed,
                PreviousBestXp = await gamification.BestAttemptXpAsync(challenge.ProfileId, challenge.Id, cancellationToken),
            },
            Progress = progress,
        };

        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var run = Task.Run(() => EvaluationPipeline.Evaluate(request, cancellationToken), cancellationToken);
        while (!run.IsCompleted)
        {
            await Task.WhenAny(run, ticker.WaitForNextTickAsync(cancellationToken).AsTask());
            if (!run.IsCompleted && progress.Apply()) await db.SaveChangesAsync(cancellationToken);
        }

        var result = await run;
        Store(evaluation, result);
        OutboxWriter.Add(db, clock, EvaluationCompleted.Type, Facts(evaluation, attempt, challenge, spec, result));
        if (submission) await attempts.SetStatusAsync(attempt.Id, "evaluated", cancellationToken); // salva tudo junto
        else await db.SaveChangesAsync(cancellationToken);

        outbox.Notify();
        await notifier.EvaluationCompleted(new EvaluationCompletedEvent(evaluation.Id, attempt.Id, evaluation.Kind, result.Score.FinalScore, result.Score.Rank));
    }

    public async Task OnFailedAsync(string payloadJson, string error, CancellationToken cancellationToken)
    {
        var job = Json.Deserialize<EvaluationJob>(payloadJson);
        var evaluation = await db.Evaluations.FirstOrDefaultAsync(e => e.Id == job.EvaluationId, cancellationToken);
        if (evaluation is null) return;
        evaluation.Status = EvaluationStatus.Failed;
        evaluation.Error = error;
        evaluation.CompletedAt = clock.GetUtcNow().UtcDateTime;
        if (evaluation.Kind == EvaluationKind.Submission) await attempts.SetStatusAsync(evaluation.AttemptId, "failed", cancellationToken);
        else await db.SaveChangesAsync(cancellationToken);
        await notifier.EvaluationFailed(new EvaluationFailedEvent(evaluation.Id, evaluation.AttemptId, error));
    }

    private void Store(EvaluationEntity evaluation, EvaluationResult result)
    {
        var series = result.Simulation.Scenarios.Where(s => s.Series is not null).ToDictionary(s => s.ScenarioId, s => s.Series!);
        var stripped = result with
        {
            Simulation = result.Simulation with { Scenarios = result.Simulation.Scenarios.Select(s => s with { Series = null }).ToList() },
        };

        evaluation.Status = EvaluationStatus.Completed;
        evaluation.Stage = "completed";
        evaluation.Percent = 100;
        evaluation.Detail = null;
        evaluation.ManifestJson = Json.Serialize(result.Manifest);
        evaluation.ResultJson = Json.Serialize(stripped);
        evaluation.FinalScore = result.Score.FinalScore;
        evaluation.Rank = result.Score.Rank;
        evaluation.CompletedAt = clock.GetUtcNow().UtcDateTime;

        // Reprocessar substitui os resultados anteriores desta avaliação.
        db.ScenarioResults.RemoveRange(db.ScenarioResults.Where(r => r.EvaluationId == evaluation.Id));
        db.Findings.RemoveRange(db.Findings.Where(f => f.EvaluationId == evaluation.Id));
        foreach (var scenario in stripped.Simulation.Scenarios)
        {
            db.ScenarioResults.Add(new ScenarioResultEntity
            {
                Id = Guid.CreateVersion7(),
                EvaluationId = evaluation.Id,
                ScenarioId = scenario.ScenarioId,
                SummaryJson = Json.Serialize(scenario),
                Series = series.TryGetValue(scenario.ScenarioId, out var s) ? Json.Gzip(s) : null,
            });
        }

        foreach (var finding in result.Score.Findings)
        {
            db.Findings.Add(new FindingEntity
            {
                Id = Guid.CreateVersion7(),
                EvaluationId = evaluation.Id,
                RuleId = finding.Finding.RuleId,
                Severity = finding.Finding.Severity.ToString().ToLowerInvariant(),
                Dimension = finding.Finding.Dimension.ToString().ToLowerInvariant(),
                Message = finding.Finding.Message,
                ElementRefsJson = Json.Serialize(finding.Finding.IrIds),
                Confirmed = finding.Confirmed,
            });
        }
    }

    /// <summary>O evento leva os fatos que a gamificação precisa, para ela não ler as tabelas da avaliação.</summary>
    private static EvaluationCompleted Facts(EvaluationEntity evaluation, AttemptInfo attempt, ChallengeInfo challenge, ChallengeSpec spec, EvaluationResult result)
    {
        var checks = result.Score.Checks;
        bool Passed(ScenarioSpec scenario) => checks.Where(c => c.ScenarioId == scenario.Id).ToList() is { Count: > 0 } list && list.All(c => c.Passed);
        double Multiplier(ScenarioSpec scenario)
        {
            var baseline = spec.Workload.BaselineRps <= 0 ? 1 : spec.Workload.BaselineRps;
            var peak = new[] { scenario.Load.Rps, scenario.Load.ToRps, scenario.Load.PeakRps }.Max() ?? baseline;
            return peak / baseline;
        }

        var simulated = spec.TestPlan.Scenarios.Where(s => result.Simulation.Find(s.Id) is not null).ToList();
        var integrity = simulated.Where(s => s.Kind == ScenarioKind.Integrity).ToList();
        var zones = simulated.Where(s => s.Faults.Any(f => f.Type == FaultType.ZoneOutage)).ToList();
        return new EvaluationCompleted
        {
            EvaluationId = evaluation.Id,
            AttemptId = attempt.Id,
            ChallengeId = challenge.Id,
            ProfileId = challenge.ProfileId,
            Kind = evaluation.Kind,
            SeedId = challenge.SeedId,
            Provider = challenge.Provider,
            Difficulty = spec.Difficulty,
            Mode = spec.Mode,
            AttemptNumber = attempt.Number,
            FinalScore = result.Score.FinalScore,
            Rank = result.Score.Rank,
            AttemptXp = result.Xp.AttemptXp,
            HintLevelsUsed = attempt.HintLevelsUsed,
            DimensionScores = result.Score.Dimensions.ToDictionary(d => d.Dimension.ToString().ToLowerInvariant(), d => d.Score),
            Competencies = spec.Competencies,
            HasMonetaryUseCase = spec.UseCases.Any(u => u.Monetary),
            IntegrityScenarioRan = integrity.Count > 0,
            MonetaryIntegrityClean = integrity.Count > 0 && integrity.All(s => result.Simulation.Find(s.Id)!.Integrity.DuplicateEffects == 0),
            UsesOutboxOrCdc = PatternDetector.Detect(result.Graph).Any(p => p.Id == "outbox"),
            DualWriteFinding = result.Findings.Any(f => f.RuleId == "DAT-001"),
            MaxPassedLoadMultiplier = simulated.Where(s => s.Kind == ScenarioKind.Load && s.Load.Profile != LoadShape.Spike && Passed(s)).Select(Multiplier).DefaultIfEmpty(0).Max(),
            MaxPassedSpikeMultiplier = simulated.Where(s => s.Kind == ScenarioKind.Load && s.Load.Profile == LoadShape.Spike && Passed(s)).Select(Multiplier).DefaultIfEmpty(0).Max(),
            ZoneOutagePassed = zones.Count > 0 && zones.All(Passed),
            CostBudgetRatio = result.Cost.BudgetUsd <= 0 ? 0 : result.Cost.MonthlyUsd / result.Cost.BudgetUsd,
        };
    }

    /// <summary>Recebe o progresso das threads do motor; o job grava no banco a cada segundo e o SignalR recebe tudo.</summary>
    private sealed class ProgressRelay(EvaluationEntity evaluation, Guid attemptId, ILabNotifier notifier) : IProgress<EvaluationProgress>
    {
        private readonly Lock _lock = new();
        private EvaluationProgress? _latest;

        public void Report(EvaluationProgress value)
        {
            lock (_lock)
            {
                if (_latest is not null && value.Percent < _latest.Percent) return;
                _latest = value;
            }

            _ = notifier.EvaluationProgress(new EvaluationProgressEvent(evaluation.Id, attemptId, value.Stage.ToString().ToLowerInvariant(), Math.Round(value.Percent, 1), value.Message));
        }

        public bool Apply()
        {
            EvaluationProgress? latest;
            lock (_lock) latest = _latest;
            if (latest is null || (evaluation.Percent >= latest.Percent && evaluation.Detail == latest.Message)) return false;
            evaluation.Stage = latest.Stage.ToString().ToLowerInvariant();
            evaluation.Percent = Math.Round(latest.Percent, 1);
            evaluation.Detail = latest.Message;
            return true;
        }
    }
}

public sealed record EvaluationView(
    Guid Id,
    Guid AttemptId,
    Guid ChallengeId,
    string Kind,
    string Status,
    string Stage,
    double Percent,
    string? Detail,
    int? FinalScore,
    string? Rank,
    string? Error,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    JsonNode? Result);

public sealed record HistoryItem(Guid EvaluationId, Guid AttemptId, Guid ChallengeId, string Title, string Kind, string Status, int? FinalScore, string? Rank, DateTime CreatedAt, DateTime? CompletedAt);

public static class EvaluationEndpoints
{
    public static void MapEvaluation(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/evaluations").WithTags("Avaliação");

        group.MapGet("/{id:guid}", async (Guid id, LabDbContext db, CancellationToken ct) =>
        {
            var e = await db.Evaluations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return e is null ? Results.NotFound() : Results.Ok(new EvaluationView(e.Id, e.AttemptId, e.ChallengeId, e.Kind, e.Status, e.Stage, e.Percent,
                e.Detail, e.FinalScore, e.Rank, e.Error, e.CreatedAt, e.StartedAt, e.CompletedAt, e.ResultJson is null ? null : JsonNode.Parse(e.ResultJson)));
        });

        group.MapGet("", async (LabDbContext db, IChallengeQueries challenges, int? limit, CancellationToken ct) =>
        {
            var rows = await db.Evaluations.AsNoTracking().OrderByDescending(e => e.CreatedAt).Take(Math.Clamp(limit ?? 30, 1, 200)).ToListAsync(ct);
            var titles = new Dictionary<Guid, string>();
            foreach (var id in rows.Select(r => r.ChallengeId).Distinct())
                titles[id] = (await challenges.FindAsync(id, ct))?.Title ?? "";
            return Results.Ok(rows.Select(e => new HistoryItem(e.Id, e.AttemptId, e.ChallengeId, titles.GetValueOrDefault(e.ChallengeId, ""), e.Kind,
                e.Status, e.FinalScore, e.Rank, e.CreatedAt, e.CompletedAt)));
        });

        group.MapGet("/{id:guid}/scenarios/{scenarioId}/series", async (Guid id, string scenarioId, LabDbContext db, CancellationToken ct) =>
        {
            var row = await db.ScenarioResults.AsNoTracking().FirstOrDefaultAsync(r => r.EvaluationId == id && r.ScenarioId == scenarioId, ct);
            return row?.Series is null ? Results.NotFound() : Results.Ok(Json.Gunzip<TimeSeries>(row.Series));
        });

        // Export para revisão com o Claude Code (skill revisar-avaliacao): o mesmo formato do `lab evaluate`.
        app.MapGet("/api/attempts/{attemptId:guid}/export", async (Guid attemptId, LabDbContext db, IAttemptQueries attempts, IChallengeQueries challenges, CancellationToken ct) =>
        {
            var attempt = await attempts.FindAsync(attemptId, ct);
            if (attempt?.SubmissionEvaluationId is not { } evaluationId) return Results.NotFound();
            var evaluation = await db.Evaluations.AsNoTracking().FirstOrDefaultAsync(e => e.Id == evaluationId, ct);
            var snapshot = evaluation is null ? null : await attempts.SnapshotAsync(evaluation.SnapshotId, ct);
            if (evaluation?.ResultJson is null || snapshot is null)
                return Results.Problem(detail: "A avaliação ainda não terminou.", statusCode: StatusCodes.Status409Conflict);
            return Results.Ok(new
            {
                challenge = await challenges.SpecAsync(attempt.ChallengeId, ct),
                ir = JsonNode.Parse(snapshot.IrJson),
                evaluation = JsonNode.Parse(evaluation.ResultJson),
            });
        }).WithTags("Avaliação");

        // A referência só aparece depois da submissão avaliada.
        group.MapGet("/{id:guid}/reference", async (Guid id, LabDbContext db, IChallengeQueries challenges, CancellationToken ct) =>
        {
            var e = await db.Evaluations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();
            if (e.Kind != EvaluationKind.Submission || e.Status != EvaluationStatus.Completed)
                return Results.Problem(detail: "A solução de referência fica disponível depois que a submissão é avaliada.", statusCode: StatusCodes.Status403Forbidden);
            var spec = await challenges.SpecAsync(e.ChallengeId, ct);
            if (spec?.Reference is null) return Results.NotFound();
            var drawn = await challenges.ReferenceSceneAsync(e.ChallengeId, ct) is { } json ? JsonNode.Parse(json) : null;
            return Results.Ok(new
            {
                graph = spec.Reference,
                rationale = spec.ReferenceRationale,
                costUsd = await challenges.ReferenceCostAsync(e.ChallengeId, ct),
                scene = drawn?["scene"],
                flows = drawn?["flows"],
            });
        });
    }
}
