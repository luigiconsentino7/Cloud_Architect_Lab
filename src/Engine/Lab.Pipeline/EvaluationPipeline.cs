using System.Diagnostics;
using System.Text.Json.Nodes;
using Lab.Cost;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Content;
using Lab.Ir.Scene;
using Lab.Rules;
using Lab.Rules.Functional;
using Lab.Scoring;
using Lab.Simulation;

namespace Lab.Pipeline;

public enum PipelineStage
{
    Compile,
    Rules,
    Functional,
    Simulation,
    Cost,
    Score,
    Review,
}

public sealed record StageTiming(PipelineStage Stage, double Ms);

/// <param name="Percent">0..100, estimado pelo peso de cada etapa (a simulação é ~65% do tempo).</param>
public sealed record EvaluationProgress(PipelineStage Stage, string Message, double Percent = 0);

/// <summary>Dados da tentativa para o XP (o histórico vem do backend).</summary>
public sealed record XpContext
{
    public bool FirstAttempt { get; init; } = true;
    public IReadOnlyList<int> HintLevelsUsed { get; init; } = [];
    public int PreviousBestXp { get; init; }
}

public sealed record EvaluationRequest
{
    public required ChallengeSpec Challenge { get; init; }
    public required ComponentCatalog Catalog { get; init; }
    public RuleTextLibrary RuleTexts { get; init; } = RuleTextLibrary.Empty;

    /// <summary>Cena do Excalidraw. Ignorada quando <see cref="Graph"/> já vem compilado (CLI, referência).</summary>
    public JsonNode? Scene { get; init; }

    public IReadOnlyList<FlowDefinition> Flows { get; init; } = [];
    public ArchitectureGraph? Graph { get; init; }

    /// <summary>Cenários a simular (nulo = todos do plano de testes).</summary>
    public IReadOnlyList<string>? ScenarioIds { get; init; }

    public int Replications { get; init; } = 1;
    public bool IncludeSeries { get; init; } = true;
    public long EventBudget { get; init; } = 20_000_000;
    public int? MaxParallelism { get; init; }

    /// <summary>Custo mensal da solução de referência (o backend guarda em cache). Nulo = calcular aqui.</summary>
    public double? ReferenceCostUsd { get; init; }

    public bool ComputeReferenceCost { get; init; } = true;
    public XpContext Xp { get; init; } = new();
    public IProgress<EvaluationProgress>? Progress { get; init; }
}

/// <summary>Tudo o que determina o resultado: mesmo manifesto, mesmo resultado, byte a byte.</summary>
public sealed record EvaluationManifest
{
    public required string EngineVersion { get; init; }
    public required string CatalogVersion { get; init; }
    public required string RulesVersion { get; init; }
    public required string ChallengeId { get; init; }
    public required string SeedId { get; init; }
    public int SeedVersion { get; init; }
    public CloudProvider Provider { get; init; }
    public Difficulty Difficulty { get; init; }
    public GameMode Mode { get; init; }
    public IReadOnlyDictionary<string, double> Variables { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> Twists { get; init; } = [];
    public required string IrHash { get; init; }
    public long MasterSeed { get; init; }
    public required string ScenarioSetHash { get; init; }
    public IReadOnlyList<string> ScenarioIds { get; init; } = [];
    public int Replications { get; init; } = 1;

    public string Hash() => Canonical.Hash(this);
}

public sealed record EvaluationResult
{
    public required EvaluationManifest Manifest { get; init; }
    public required ArchitectureGraph Graph { get; init; }
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public IReadOnlyList<UseCaseFunctionalResult> Functional { get; init; } = [];
    public required SimulationReport Simulation { get; init; }
    public required CostReport Cost { get; init; }
    public double? ReferenceCostUsd { get; init; }
    public required ScoreResult Score { get; init; }
    public required Review Review { get; init; }
    public required XpBreakdown Xp { get; init; }
    public IReadOnlyList<StageTiming> Timings { get; init; } = [];

    /// <summary>Hash do resultado sem tempos de relógio — igual entre execuções do mesmo manifesto.</summary>
    public string ResultHash { get; init; } = "";

    public EvaluationResult WithoutWallClock() => this with
    {
        Timings = [],
        ResultHash = "",
        Simulation = Simulation with
        {
            WallTimeMs = 0,
            Scenarios = Simulation.Scenarios.Select(s => s with { WallTimeMs = 0 }).ToList(),
        },
    };
}

/// <summary>Executa as 7 etapas do motor (spec 05). Erros de compilação não interrompem: o UC afetado é reprovado e o resto segue.</summary>
public static class EvaluationPipeline
{
    public static EvaluationResult Evaluate(EvaluationRequest request, CancellationToken cancellationToken = default) =>
        EngineCulture.Run(() => EvaluateCore(request, cancellationToken));

    private static EvaluationResult EvaluateCore(EvaluationRequest request, CancellationToken cancellationToken)
    {
        var challenge = request.Challenge;
        var timings = new List<StageTiming>();
        var stopwatch = Stopwatch.StartNew();

        void Stage(PipelineStage stage, string message)
        {
            cancellationToken.ThrowIfCancellationRequested();
            request.Progress?.Report(new EvaluationProgress(stage, message, StartPercent(stage)));
        }

        void Done(PipelineStage stage)
        {
            timings.Add(new StageTiming(stage, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1)));
            stopwatch.Restart();
        }

        Stage(PipelineStage.Compile, "Compilando o desenho");
        var compilation = request.Graph is { } prebuilt
            ? new CompilationResult(prebuilt, [], Canonical.Hash(prebuilt))
            : SceneCompiler.Compile(new SceneCompileRequest
            {
                Scene = request.Scene,
                Flows = request.Flows,
                Provider = challenge.Provider,
                Catalog = request.Catalog,
                Challenge = challenge,
            });
        var graph = compilation.Graph;
        Done(PipelineStage.Compile);

        Stage(PipelineStage.Rules, "Aplicando as regras de arquitetura");
        var context = new RuleContext(graph, challenge, request.Catalog);
        var findings = RuleEngine.Run(context);
        Done(PipelineStage.Rules);

        Stage(PipelineStage.Functional, "Testando os fluxos dos casos de uso");
        var functional = FunctionalVerifier.Verify(context);
        Done(PipelineStage.Functional);

        Stage(PipelineStage.Simulation, "Simulando carga, falhas e integridade");
        var simulation = Simulator.Run(new SimulationRequest
        {
            Graph = graph,
            Challenge = challenge,
            Catalog = request.Catalog,
            ScenarioIds = request.ScenarioIds,
            Replications = request.Replications,
            EventBudget = request.EventBudget,
            IncludeSeries = request.IncludeSeries,
            MaxParallelism = request.MaxParallelism ?? Math.Max(1, Environment.ProcessorCount - 1),
            Progress = request.Progress is null ? null : new InlineProgress<SimulationProgress>(p => request.Progress.Report(new EvaluationProgress(
                PipelineStage.Simulation,
                $"Simulando {ScenarioName(challenge, p.ScenarioId)}, {p.Completed} de {p.Total}",
                StartPercent(PipelineStage.Simulation) + (65.0 * p.Completed / Math.Max(1, p.Total))))),
        }, cancellationToken);
        Done(PipelineStage.Simulation);

        Stage(PipelineStage.Cost, "Estimando o custo mensal");
        var baselineId = SloEvaluator.BaselineScenario(challenge)?.Id;
        var peakId = SloEvaluator.PeakScenario(challenge)?.Id;
        var cost = CostModel.Estimate(graph, challenge, request.Catalog,
            baselineId is null ? null : simulation.Find(baselineId),
            peakId is null ? null : simulation.Find(peakId));
        var referenceCost = request.ReferenceCostUsd
            ?? (request.ComputeReferenceCost ? ReferenceCost.Estimate(challenge, request.Catalog, request.MaxParallelism, cancellationToken) : null);
        Done(PipelineStage.Cost);

        Stage(PipelineStage.Score, "Calculando a nota");
        var checks = SloEvaluator.Evaluate(challenge, simulation, graph);
        var score = Scorer.Score(new ScoreInput
        {
            Challenge = challenge,
            Findings = findings,
            Functional = functional,
            Simulation = simulation,
            Checks = checks,
            Cost = cost,
            ReferenceCostUsd = referenceCost,
        });
        var xp = XpCalculator.Compute(new XpInput
        {
            Difficulty = challenge.Difficulty,
            Score = score.FinalScore,
            Rank = score.Rank,
            Mode = challenge.Mode,
            FirstAttempt = request.Xp.FirstAttempt,
            HintLevelsUsed = request.Xp.HintLevelsUsed,
            PreviousBestXp = request.Xp.PreviousBestXp,
        });
        Done(PipelineStage.Score);

        Stage(PipelineStage.Review, "Montando a revisão");
        var review = ReviewBuilder.Build(new ReviewInput
        {
            Challenge = challenge,
            Graph = graph,
            Catalog = request.Catalog,
            Score = score,
            Functional = functional,
            Simulation = simulation,
            Cost = cost,
            RuleTexts = request.RuleTexts,
            ReferenceCostUsd = referenceCost,
        });
        Done(PipelineStage.Review);

        var result = new EvaluationResult
        {
            Manifest = ManifestFor(request, compilation.IrHash, simulation),
            Graph = graph,
            Diagnostics = compilation.Diagnostics,
            Findings = findings,
            Functional = functional,
            Simulation = simulation,
            Cost = cost,
            ReferenceCostUsd = referenceCost,
            Score = score,
            Review = review,
            Xp = xp,
            Timings = timings,
        };
        return result with { ResultHash = Canonical.Hash(result.WithoutWallClock()) };
    }

    private static double StartPercent(PipelineStage stage) => stage switch
    {
        PipelineStage.Compile => 2,
        PipelineStage.Rules => 6,
        PipelineStage.Functional => 10,
        PipelineStage.Simulation => 15,
        PipelineStage.Cost => 82,
        PipelineStage.Score => 90,
        _ => 95,
    };

    private static string ScenarioName(ChallengeSpec challenge, string scenarioId) =>
        challenge.TestPlan.Find(scenarioId) is { Name.Length: > 0 } scenario ? scenario.Name : scenarioId;

    /// <summary>IProgress síncrono: Progress&lt;T&gt; usa o SynchronizationContext e reordenaria os avisos.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public static EvaluationManifest ManifestFor(EvaluationRequest request, string irHash, SimulationReport? simulation = null)
    {
        var challenge = request.Challenge;
        return new EvaluationManifest
        {
            EngineVersion = Simulator.EngineVersion,
            CatalogVersion = request.Catalog.Version,
            RulesVersion = RuleEngine.Version,
            ChallengeId = challenge.Id,
            SeedId = challenge.SeedId,
            SeedVersion = challenge.SeedVersion,
            Provider = challenge.Provider,
            Difficulty = challenge.Difficulty,
            Mode = challenge.Mode,
            Variables = challenge.Variables,
            Twists = challenge.Twists,
            IrHash = irHash,
            MasterSeed = challenge.MasterSeed,
            ScenarioSetHash = Canonical.Hash(challenge.TestPlan),
            ScenarioIds = simulation?.Scenarios.Select(s => s.ScenarioId).ToList()
                ?? request.ScenarioIds?.ToList()
                ?? challenge.TestPlan.Scenarios.Select(s => s.Id).ToList(),
            Replications = Math.Max(1, request.Replications),
        };
    }
}

/// <summary>Custo da solução de referência, simulada nos mesmos cenários que o custo do usuário usa (baseline e pico).</summary>
public static class ReferenceCost
{
    public static double? Estimate(ChallengeSpec challenge, ComponentCatalog catalog, int? maxParallelism = null,
        CancellationToken cancellationToken = default)
    {
        if (challenge.Reference is not { } reference) return null;
        var baselineId = SloEvaluator.BaselineScenario(challenge)?.Id;
        var peakId = SloEvaluator.PeakScenario(challenge)?.Id;
        var scenarioIds = new[] { baselineId, peakId }.OfType<string>().ToList();
        var report = scenarioIds.Count == 0
            ? null
            : Simulator.Run(new SimulationRequest
            {
                Graph = reference,
                Challenge = challenge,
                Catalog = catalog,
                ScenarioIds = scenarioIds,
                IncludeSeries = false,
                MaxParallelism = maxParallelism ?? Math.Max(1, Environment.ProcessorCount - 1),
            }, cancellationToken);
        return CostModel.Estimate(reference, challenge, catalog,
            baselineId is null ? null : report?.Find(baselineId),
            peakId is null ? null : report?.Find(peakId)).MonthlyUsd;
    }
}
