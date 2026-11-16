using Lab.Ir.Challenges;

namespace Lab.Simulation;

public sealed record LatencySummary(double P50, double P95, double P99, double Mean, double Max);

public sealed record UseCaseResult
{
    public required string UseCaseId { get; init; }
    public long Requests { get; init; }
    public long Succeeded { get; init; }
    public double SuccessRate { get; init; }
    public LatencySummary Latency { get; init; } = new(0, 0, 0, 0, 0);
    public LatencySummary? AsyncCompletion { get; init; }
    public long AsyncExpected { get; init; }
    public long AsyncCompleted { get; init; }
    public long AsyncFailed { get; init; }
    public IReadOnlyDictionary<string, long> Errors { get; init; } = new Dictionary<string, long>();

    /// <summary>p99 de cada replicação, para o intervalo de confiança.</summary>
    public IReadOnlyList<double> P99ByReplication { get; init; } = [];
}

public sealed record NodeResult
{
    public required string NodeId { get; init; }
    public required string Label { get; init; }
    public required string Archetype { get; init; }
    public long Requests { get; init; }
    public long Throttled { get; init; }
    public long Errors { get; init; }
    public long Rejected { get; init; }
    public long ConnectionErrors { get; init; }
    public long ColdStarts { get; init; }
    public double UtilizationAvg { get; init; }
    public double UtilizationMax { get; init; }
    public int MaxQueue { get; init; }
    public int InstancesMin { get; init; }
    public int InstancesMax { get; init; }
    public double InstancesAvg { get; init; }
    public int ConcurrencyMax { get; init; }
    public int ConcurrencyLimit { get; init; }
    public long BacklogMax { get; init; }
    public double OldestMessageAgeMaxSeconds { get; init; }
    public long DlqMessages { get; init; }
    public double MeanServiceMs { get; init; }
    public double PeakArrivalRps { get; init; }

    /// <summary>Segundos sem aceitar operações (banco relacional durante failover ou queda da zona do primário).</summary>
    public double UnavailableSeconds { get; init; }

    public int Failovers { get; init; }
}

public sealed record IntegrityResult(
    long DuplicateEffects,
    long MonetaryDuplicates,
    long LostEvents,
    long OrderingViolations,
    long DlqMessages);

public sealed record Bottleneck(string NodeId, string Reason, string Detail, double? Needed = null, double? Limit = null);

public sealed record UseCaseSeries(IReadOnlyList<int> Requests, IReadOnlyList<int> Errors, IReadOnlyList<double> P99Ms);

public sealed record NodeSeries(IReadOnlyList<double> Utilization, IReadOnlyList<int> Queue, IReadOnlyList<int> Instances, IReadOnlyList<int> Throttled);

/// <summary>Séries por segundo (do início do cenário até o fim do escoamento).</summary>
public sealed record TimeSeries(
    int Seconds,
    IReadOnlyList<double> OfferedRps,
    IReadOnlyDictionary<string, UseCaseSeries> UseCases,
    IReadOnlyDictionary<string, NodeSeries> Nodes);

public sealed record ScenarioResult
{
    public required string ScenarioId { get; init; }
    public string Name { get; init; } = "";
    public ScenarioKind Kind { get; init; }
    public Visibility Visibility { get; init; }
    public long Seed { get; init; }
    public int Replications { get; init; } = 1;
    public double DurationSeconds { get; init; }
    public double WarmupSeconds { get; init; }
    public double PeakOfferedRps { get; init; }
    public IReadOnlyDictionary<string, UseCaseResult> UseCases { get; init; } = new Dictionary<string, UseCaseResult>();
    public IReadOnlyDictionary<string, NodeResult> Nodes { get; init; } = new Dictionary<string, NodeResult>();
    public IntegrityResult Integrity { get; init; } = new(0, 0, 0, 0, 0);
    public IReadOnlyList<Bottleneck> Bottlenecks { get; init; } = [];

    /// <summary>Fatos observados (ex.: <c>throttled</c>, <c>duplicate-effects</c>) usados para confirmar regras.</summary>
    public IReadOnlyList<string> Facts { get; init; } = [];

    public TimeSeries? Series { get; init; }
    public long Events { get; init; }
    public double WallTimeMs { get; init; }

    /// <summary>As janelas foram encurtadas para caber no orçamento de eventos.</summary>
    public double TimeCompression { get; init; } = 1;

    public bool BudgetExceeded { get; init; }
}

public sealed record SimulationReport
{
    public required string EngineVersion { get; init; }
    public long MasterSeed { get; init; }
    public IReadOnlyList<ScenarioResult> Scenarios { get; init; } = [];
    public double WallTimeMs { get; init; }

    public ScenarioResult? Find(string scenarioId) => Scenarios.FirstOrDefault(s => s.ScenarioId == scenarioId);
}
