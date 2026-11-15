using Lab.Ir.Catalog;

namespace Lab.Ir.Challenges;

public enum Difficulty
{
    Junior,
    Pleno,
    Senior,
    Staff,
}

public enum GameMode
{
    Treino,
    Entrevista,
    Plantao,
}

public sealed record CapabilityRequirement
{
    public required string Capability { get; init; }

    /// <summary>Alvo da capacidade (ex.: <c>external:spi</c> para <c>external-call</c>).</summary>
    public string? Target { get; init; }

    /// <summary><c>respond</c> = precisa acontecer no segmento síncrono, antes da resposta ao cliente.</summary>
    public string? Before { get; init; }

    public string? Description { get; init; }
}

public sealed record NegativePathRequirement
{
    public required string Dependency { get; init; }
    public string Expect { get; init; } = "fallback";
    public string Description { get; init; } = "";
}

public sealed record UseCaseSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string EntryClient { get; init; } = "client.mobile";
    public bool Monetary { get; init; }
    public bool Critical { get; init; } = true;
    public bool RequiresOrdering { get; init; }
    public bool ReadYourWrites { get; init; }
    public IReadOnlyList<CapabilityRequirement> RequiredCapabilities { get; init; } = [];
    public IReadOnlyList<NegativePathRequirement> NegativePaths { get; init; } = [];
}

public sealed record ExternalSystemSpec
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public LatencyDist Latency { get; init; } = new(100, 400);
    public double Availability { get; init; } = 0.9995;
    public double? RateLimitRps { get; init; }

    /// <summary>Chamadas repetidas com o mesmo identificador não duplicam o efeito (ex.: endToEndId no SPI).</summary>
    public bool Idempotent { get; init; } = true;

    /// <summary>A chamada produz efeito observável (transferência, notificação) e entra na contagem de integridade.</summary>
    public bool SideEffect { get; init; }
}

public sealed record UseCaseLoad
{
    public double Share { get; init; }
    public double PayloadKb { get; init; } = 2;

    /// <summary>Custo de CPU da lógica de negócio por salto de computação, numa vCPU de referência.</summary>
    public LatencyDist ComputeCostMs { get; init; } = new(3, 12);
}

public sealed record HotKeySpec(string Key, double ShareOfWrites);

public sealed record DailyPatternSpec
{
    public double PeakHoursPerDay { get; init; } = 2;
    public double PeakMultiplier { get; init; } = 3;
}

public sealed record WorkloadSpec
{
    public double BaselineRps { get; init; } = 100;
    public IReadOnlyDictionary<string, UseCaseLoad> Mix { get; init; } = new Dictionary<string, UseCaseLoad>();
    public int Keys { get; init; } = 1_000_000;
    public IReadOnlyList<HotKeySpec> HotKeys { get; init; } = [];
    public IReadOnlyDictionary<string, double> Cacheability { get; init; } = new Dictionary<string, double>();
    public DailyPatternSpec DailyPattern { get; init; } = new();
    public double ClientTimeoutMs { get; init; } = 3000;
    public double StoredGb { get; init; } = 50;
}

public enum ScenarioKind
{
    Load,
    Chaos,
    Integrity,
}

public enum Visibility
{
    Public,
    Hidden,
}

public enum LoadShape
{
    Constant,
    Ramp,
    Spike,
}

/// <summary>Perfil de carga. Tempos em segundos.</summary>
public sealed record LoadProfile
{
    public LoadShape Profile { get; init; } = LoadShape.Constant;

    /// <summary>Reaproveita o perfil de outro cenário (ex.: <c>baseline</c>).</summary>
    public string? Ref { get; init; }

    public double? Rps { get; init; }
    public double? FromRps { get; init; }
    public double? ToRps { get; init; }
    public double? RampFor { get; init; }
    public double? HoldFor { get; init; }
    public double? BaseRps { get; init; }
    public double? PeakRps { get; init; }
    public double? At { get; init; }
    public double? For { get; init; }
    public double? Duration { get; init; }
    public double? Warmup { get; init; }
}

public enum FaultType
{
    InstanceKill,
    ZoneOutage,
    RegionOutage,
    LatencyInjection,
    DependencyOutage,
    Throttling,
    DuplicateDelivery,
    ClientRetry,
    ConsumerCrash,
    PublishCrash,
    Reorder,
    ReplicaLag,
}

public sealed record FaultSpec
{
    public FaultType Type { get; init; }
    public string? Target { get; init; }
    public string? Zone { get; init; }
    public double At { get; init; }
    public double? For { get; init; }
    public LatencyDist? AddMs { get; init; }
    public double Probability { get; init; }
    public int? Count { get; init; }
    public double? OnTimeoutMs { get; init; }
    public bool SameIdempotencyKey { get; init; } = true;
    public double? Factor { get; init; }
}

public sealed record ScenarioSpec
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public ScenarioKind Kind { get; init; }
    public Visibility Visibility { get; init; } = Visibility.Public;
    public LoadProfile Load { get; init; } = new();
    public IReadOnlyList<FaultSpec> Faults { get; init; } = [];
}

/// <summary>
/// SLO verificado em um ou mais cenários. Métricas: <c>latency.p50|p95|p99</c>, <c>success-rate</c>,
/// <c>async-completion.p95|p99</c>, <c>duplicate-effects</c>, <c>lost-events</c>, <c>ordering-violations</c>.
/// Latências em ms; taxa de sucesso como fração (0,9995).
/// </summary>
public sealed record SloSpec
{
    public required string Id { get; init; }
    public string? UseCase { get; init; }
    public required string Metric { get; init; }
    public double? Max { get; init; }
    public double? Min { get; init; }
    public IReadOnlyList<string> Scenarios { get; init; } = [];
    public string Description { get; init; } = "";
}

public sealed record TestPlan
{
    public IReadOnlyList<ScenarioSpec> Scenarios { get; init; } = [];
    public IReadOnlyList<SloSpec> Slos { get; init; } = [];

    public ScenarioSpec? Find(string id) => Scenarios.FirstOrDefault(s => s.Id == id);
}

public sealed record ConstraintsSpec
{
    public double BudgetUsdMonth { get; init; } = 5000;
    public bool DataResidencyBrazil { get; init; }
    public IReadOnlyList<string> Compliance { get; init; } = [];
    public bool RequireEncryptionAtRest { get; init; }
    public bool RequireAuditLog { get; init; }
    public double? RtoSeconds { get; init; }
    public double? RpoSeconds { get; init; }
}

public sealed record RubricWeights
{
    public double Functional { get; init; } = 20;
    public double Performance { get; init; } = 20;
    public double Resilience { get; init; } = 15;
    public double Consistency { get; init; } = 20;
    public double Security { get; init; } = 10;
    public double Cost { get; init; } = 10;
    public double Operability { get; init; } = 5;

    public double Total => Functional + Performance + Resilience + Consistency + Security + Cost + Operability;
}

public sealed record HintSpec(int Level, string Text);

public sealed record ClarificationSpec
{
    public required string Question { get; init; }
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public required string Answer { get; init; }
}

/// <summary>Desafio instanciado: semente + variação + provedor. É o que o motor e a interface usam.</summary>
public sealed record ChallengeSpec
{
    public required string Id { get; init; }
    public required string SeedId { get; init; }
    public int SeedVersion { get; init; } = 1;
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public string Narrative { get; init; } = "";
    public string Track { get; init; } = "";
    public string Domain { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Difficulty Difficulty { get; init; }
    public CloudProvider Provider { get; init; }
    public GameMode Mode { get; init; }
    public IReadOnlyList<UseCaseSpec> UseCases { get; init; } = [];
    public IReadOnlyList<ExternalSystemSpec> ExternalSystems { get; init; } = [];
    public WorkloadSpec Workload { get; init; } = new();
    public TestPlan TestPlan { get; init; } = new();
    public ConstraintsSpec Constraints { get; init; } = new();
    public IReadOnlyList<string> Competencies { get; init; } = [];
    public RubricWeights Weights { get; init; } = new();
    public IReadOnlyList<HintSpec> Hints { get; init; } = [];
    public IReadOnlyList<ClarificationSpec> Clarifications { get; init; } = [];
    public IReadOnlyList<string> Twists { get; init; } = [];
    public IReadOnlyDictionary<string, double> Variables { get; init; } = new Dictionary<string, double>();
    public ArchitectureGraph? Reference { get; init; }
    public string ReferenceRationale { get; init; } = "";
    public long MasterSeed { get; init; }

    public string Region => Provider.BrazilRegion();

    public UseCaseSpec? FindUseCase(string id) => UseCases.FirstOrDefault(u => u.Id == id);

    public ExternalSystemSpec? FindExternal(string id) => ExternalSystems.FirstOrDefault(e => e.Id == id);
}
