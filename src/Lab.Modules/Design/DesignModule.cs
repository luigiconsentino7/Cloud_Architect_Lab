using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Scene;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Design.Contracts;
using Lab.Modules.Evaluation.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Jobs;
using Lab.Modules.Shared.Persistence;
using Lab.Rules;
using Lab.Rules.Functional;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Design;

public sealed record AttemptView(
    Guid Id,
    Guid ChallengeId,
    int Number,
    string Provider,
    string Mode,
    string Status,
    JsonNode Scene,
    int DraftVersion,
    IReadOnlyList<FlowDefinition> Flows,
    IReadOnlyList<int> HintLevelsUsed,
    DateTime StartedAt,
    DateTime? SubmittedAt,
    Guid? SubmissionEvaluationId);

public sealed record DraftRequest(JsonNode Scene);

public sealed record FlowsRequest(IReadOnlyList<FlowDefinition> Flows);

public sealed record FindingView(string RuleId, string Title, string Dimension, string Severity, string Message, IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);

public sealed record ValidationView(
    string IrHash,
    IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyList<UseCaseFunctionalResult> Functional,
    ArchitectureGraph Graph);

public sealed record HintView(int Level, string Text, IReadOnlyList<int> HintLevelsUsed);

public sealed record EvaluationTicket(Guid EvaluationId, Guid AttemptId, string Kind);

public sealed class AttemptService(LabDbContext db, IChallengeQueries challenges, IEvaluationScheduler scheduler, LabContent content,
    JobSignal jobs, TimeProvider clock) : IAttemptQueries
{
    /// <summary>Cena acima disto é recusada (defesa contra travar o app sem querer).</summary>
    public const int MaxSceneBytes = 5 * 1024 * 1024;

    public async Task<AttemptInfo?> FindAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = await db.Attempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == attemptId, cancellationToken);
        return attempt is null ? null : new AttemptInfo(attempt.Id, attempt.ChallengeId, attempt.Number, attempt.Provider, attempt.Mode,
            attempt.Status, Json.Deserialize<List<int>>(attempt.HintsUsedJson), attempt.SubmissionEvaluationId);
    }

    public async Task<SnapshotInfo?> SnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default) =>
        await db.Snapshots.AsNoTracking().Where(s => s.Id == snapshotId)
            .Select(s => new SnapshotInfo(s.Id, s.AttemptId, s.Kind, s.SceneJson, s.FlowsJson, s.IrJson, s.IrHash))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task SetStatusAsync(Guid attemptId, string status, CancellationToken cancellationToken = default)
    {
        var attempt = await db.Attempts.FirstAsync(a => a.Id == attemptId, cancellationToken);
        attempt.Status = status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AttemptEntity?> StartAsync(Guid challengeId, CancellationToken cancellationToken)
    {
        var challenge = await challenges.FindAsync(challengeId, cancellationToken);
        if (challenge is null) return null;
        var number = await db.Attempts.CountAsync(a => a.ChallengeId == challengeId, cancellationToken) + 1;
        var attempt = new AttemptEntity
        {
            Id = Guid.CreateVersion7(),
            ChallengeId = challengeId,
            Number = number,
            Provider = challenge.Provider,
            Mode = challenge.Mode,
            StartedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);
        return attempt;
    }

    public async Task<ValidationView> ValidateAsync(AttemptEntity attempt, CancellationToken cancellationToken)
    {
        var spec = await challenges.SpecAsync(attempt.ChallengeId, cancellationToken)
            ?? throw new InvalidOperationException("Desafio da tentativa não existe.");
        var compilation = Compile(attempt, spec);
        var context = new RuleContext(compilation.Graph, spec, content.Catalog);
        var findings = RuleEngine.Run(context);
        return new ValidationView(
            compilation.IrHash,
            compilation.Diagnostics,
            findings.Select(f => new FindingView(f.RuleId, content.RuleTexts.Find(f.RuleId)?.Title ?? f.RuleId,
                f.Dimension.ToString().ToLowerInvariant(), f.Severity.ToString().ToLowerInvariant(), f.Message, f.NodeIds, f.EdgeIds)).ToList(),
            FunctionalVerifier.Verify(context),
            compilation.Graph);
    }

    /// <summary>Snapshot + avaliação + job na mesma transação; o worker acorda logo depois.</summary>
    public async Task<EvaluationTicket> ScheduleAsync(AttemptEntity attempt, string kind, CancellationToken cancellationToken)
    {
        var spec = await challenges.SpecAsync(attempt.ChallengeId, cancellationToken)
            ?? throw new InvalidOperationException("Desafio da tentativa não existe.");
        var compilation = Compile(attempt, spec);
        var snapshot = new SnapshotEntity
        {
            Id = Guid.CreateVersion7(),
            AttemptId = attempt.Id,
            Kind = kind == "submission" ? "submit" : "simulate",
            SceneJson = attempt.DraftSceneJson,
            FlowsJson = attempt.FlowsJson,
            IrJson = Json.Serialize(compilation.Graph),
            IrHash = compilation.IrHash,
            DiagnosticsJson = Json.Serialize(compilation.Diagnostics),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Snapshots.Add(snapshot);
        var evaluationId = scheduler.Schedule(db, attempt.Id, attempt.ChallengeId, snapshot.Id, kind);
        if (kind == "submission")
        {
            attempt.Status = AttemptStatus.Submitted;
            attempt.SubmittedAt = snapshot.CreatedAt;
            attempt.SubmissionEvaluationId = evaluationId;
        }

        await db.SaveChangesAsync(cancellationToken);
        jobs.Notify();
        return new EvaluationTicket(evaluationId, attempt.Id, kind);
    }

    public CompilationResult Compile(AttemptEntity attempt, ChallengeSpec spec) => SceneCompiler.Compile(new SceneCompileRequest
    {
        Scene = JsonNode.Parse(attempt.DraftSceneJson),
        Flows = Json.Deserialize<List<FlowDefinition>>(attempt.FlowsJson),
        Provider = spec.Provider,
        Catalog = content.Catalog,
        Challenge = spec,
    });

    public static AttemptView View(AttemptEntity a) => new(a.Id, a.ChallengeId, a.Number, a.Provider, a.Mode, a.Status,
        JsonNode.Parse(a.DraftSceneJson)!, a.DraftVersion, Json.Deserialize<List<FlowDefinition>>(a.FlowsJson),
        Json.Deserialize<List<int>>(a.HintsUsedJson), a.StartedAt, a.SubmittedAt, a.SubmissionEvaluationId);
}

public static class DesignEndpoints
{
    public static void MapDesign(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/challenges/{challengeId:guid}/attempts", async (Guid challengeId, AttemptService attempts, CancellationToken ct) =>
        {
            var attempt = await attempts.StartAsync(challengeId, ct);
            return attempt is null ? Results.NotFound() : Results.Created($"/api/attempts/{attempt.Id}", AttemptService.View(attempt));
        }).WithTags("Desenho");

        app.MapGet("/api/challenges/{challengeId:guid}/attempts", async (Guid challengeId, LabDbContext db, CancellationToken ct) =>
        {
            var list = await db.Attempts.Where(a => a.ChallengeId == challengeId).OrderBy(a => a.Number).ToListAsync(ct);
            return Results.Ok(list.Select(AttemptService.View));
        }).WithTags("Desenho");

        var group = app.MapGroup("/api/attempts/{id:guid}").WithTags("Desenho");

        group.MapGet("", async (Guid id, LabDbContext db, CancellationToken ct) =>
            await db.Attempts.FirstOrDefaultAsync(a => a.Id == id, ct) is { } attempt ? Results.Ok(AttemptService.View(attempt)) : Results.NotFound());

        group.MapPut("/draft", async (Guid id, DraftRequest request, [FromHeader(Name = "If-Match")] string? ifMatch, LabDbContext db,
            HttpContext http, CancellationToken ct) =>
        {
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attempt is null) return Results.NotFound();
            if (attempt.Status != AttemptStatus.Draft) return Conflict("A tentativa já foi submetida; o desenho não pode mais mudar.");
            if (!int.TryParse(ifMatch?.Trim('"', 'W', '/', ' '), out var version))
                return Results.Problem(detail: "Envie If-Match com a versão do rascunho.", statusCode: StatusCodes.Status428PreconditionRequired);
            if (version != attempt.DraftVersion)
            {
                return Results.Problem(detail: $"O rascunho mudou em outra aba (versão {attempt.DraftVersion}, a sua é {version}).",
                    statusCode: StatusCodes.Status412PreconditionFailed, title: "Conflito de edição",
                    extensions: new Dictionary<string, object?> { ["currentVersion"] = attempt.DraftVersion });
            }

            var scene = request.Scene.ToJsonString();
            if (scene.Length > AttemptService.MaxSceneBytes) return Results.Problem(detail: "Cena grande demais (limite de 5 MB).", statusCode: StatusCodes.Status413PayloadTooLarge);
            attempt.DraftSceneJson = scene;
            attempt.DraftVersion++;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Results.Problem(detail: "O rascunho mudou em outra aba.", statusCode: StatusCodes.Status412PreconditionFailed, title: "Conflito de edição");
            }

            http.Response.Headers.ETag = $"\"{attempt.DraftVersion}\"";
            return Results.Ok(new { draftVersion = attempt.DraftVersion });
        });

        group.MapPut("/flows", async (Guid id, FlowsRequest request, LabDbContext db, CancellationToken ct) =>
        {
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attempt is null) return Results.NotFound();
            if (attempt.Status != AttemptStatus.Draft) return Conflict("A tentativa já foi submetida.");
            attempt.FlowsJson = Json.Serialize(request.Flows);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { flows = request.Flows.Count });
        });

        group.MapPost("/validate", async (Guid id, LabDbContext db, AttemptService attempts, CancellationToken ct) =>
            await db.Attempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } attempt
                ? Results.Ok(await attempts.ValidateAsync(attempt, ct))
                : Results.NotFound());

        // Sugestões para tipar formas desenhadas à mão (aliases do catálogo + IA local para as que não casaram).
        group.MapPost("/label-mappings", async (Guid id, LabDbContext db, LabelMappingService mappings, CancellationToken ct) =>
            await db.Attempts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is { } attempt
                ? Results.Ok(await mappings.SuggestAsync(attempt, ct))
                : Results.NotFound());

        group.MapPost("/hints", async (Guid id, LabDbContext db, IChallengeQueries challenges, CancellationToken ct) =>
        {
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attempt is null) return Results.NotFound();
            var spec = await challenges.SpecAsync(attempt.ChallengeId, ct);
            var used = Json.Deserialize<List<int>>(attempt.HintsUsedJson);
            var next = spec?.Hints.OrderBy(h => h.Level).FirstOrDefault(h => !used.Contains(h.Level));
            if (next is null) return Conflict("Não há mais dicas para este desafio.");
            if (attempt.Status == AttemptStatus.Draft)
            {
                used.Add(next.Level);
                attempt.HintsUsedJson = Json.Serialize(used);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new HintView(next.Level, next.Text, used));
        });

        group.MapPost("/simulate", async (Guid id, LabDbContext db, AttemptService attempts, CancellationToken ct) =>
        {
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attempt is null) return Results.NotFound();
            if (attempt.Status != AttemptStatus.Draft) return Conflict("A tentativa já foi submetida.");
            if (attempt.Mode != "treino") return Conflict("A simulação de rascunho existe só no modo Treino.");
            var ticket = await attempts.ScheduleAsync(attempt, "draft", ct);
            return Results.Accepted($"/api/evaluations/{ticket.EvaluationId}", ticket);
        });

        group.MapPost("/submit", async (Guid id, [FromHeader(Name = "Idempotency-Key")] string? key, LabDbContext db, AttemptService attempts, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(key) || key.Length > 100)
                return Results.Problem(detail: "Envie o header Idempotency-Key (até 100 caracteres).", statusCode: StatusCodes.Status400BadRequest);
            var attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (attempt is null) return Results.NotFound();
            if (attempt.SubmitIdempotencyKey == key && attempt.SubmissionEvaluationId is { } existing)
                return Results.Accepted($"/api/evaluations/{existing}", new EvaluationTicket(existing, attempt.Id, "submission"));
            if (attempt.Status != AttemptStatus.Draft) return Conflict("A tentativa já foi submetida com outra chave.");
            attempt.SubmitIdempotencyKey = key;
            var ticket = await attempts.ScheduleAsync(attempt, "submission", ct);
            return Results.Accepted($"/api/evaluations/{ticket.EvaluationId}", ticket);
        });

    }

    private static IResult Conflict(string detail) => Results.Problem(detail: detail, statusCode: StatusCodes.Status409Conflict, title: "Operação não permitida agora");
}
