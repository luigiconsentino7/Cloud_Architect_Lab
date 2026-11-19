using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Lab.Ir;

public enum Interaction
{
    SyncRequest,
    AsyncPublish,
    AsyncConsume,
    DataAccess,
    Replication,
    Cdc,
}

public enum BackoffKind
{
    None,
    Fixed,
    Exponential,
    ExponentialJitter,
}

public enum DataOpKind
{
    Read,
    Write,
    ConditionalWrite,
    Transaction,
}

public enum TriggerKind
{
    Always,
    OnFailure,
    OnCacheMiss,
}

public enum DeliveryMode
{
    Direct,
    Outbox,
}

public enum ReplicationMode
{
    Sync,
    Async,
}

public sealed record RetryPolicy(int Max = 0, BackoffKind Backoff = BackoffKind.None, double BaseMs = 100);

public sealed record CircuitBreakerPolicy(bool Enabled = false, double ErrorRateThreshold = 0.5, double OpenMs = 30_000);

public sealed record DataOp(DataOpKind Op, int Count = 1)
{
    public bool IsWrite => Op is not DataOpKind.Read;
}

/// <summary>Configuração de uma conexão (seta) — espelha o <c>EdgeConfig</c> do frontend.</summary>
public sealed record EdgeConfig
{
    public string? Protocol { get; init; }
    public double? TimeoutMs { get; init; }
    public RetryPolicy? Retries { get; init; }
    public CircuitBreakerPolicy? CircuitBreaker { get; init; }
    public bool IdempotencyKey { get; init; }
    public double CallRatio { get; init; } = 1;
    public string? ParallelGroup { get; init; }
    public TriggerKind Trigger { get; init; } = TriggerKind.Always;

    /// <summary>Conexão cuja falha (ou cache miss) dispara este passo.</summary>
    public string? TriggerOf { get; init; }

    public double? PayloadKb { get; init; }
    public IReadOnlyList<DataOp>? Ops { get; init; }
    public DeliveryMode Delivery { get; init; } = DeliveryMode.Direct;
    public int? BatchSize { get; init; }
    public ReplicationMode? Mode { get; init; }

    public static EdgeConfig Default { get; } = new();

    public bool HasWrite => Ops?.Any(o => o.IsWrite) == true;
    public bool HasAtomicWrite => Ops?.Any(o => o.Op is DataOpKind.ConditionalWrite or DataOpKind.Transaction) == true;
}

public sealed record Placement(string? Region, IReadOnlyList<string> Zones);

public sealed record Node(string Id, string CatalogId, string Archetype, string Label, JsonObject Config, Placement Placement)
{
    [JsonIgnore]
    public NodeConfig Cfg => new(Config);

    public IReadOnlyList<string> Responsibilities => Cfg.Strings("responsibilities");
}

public sealed record Edge(string Id, string From, string To, Interaction Interaction, EdgeConfig Config);

public enum BoundaryType
{
    Region,
    Zone,
    Network,
    SubnetPublic,
    SubnetPrivate,
}

public sealed record Boundary(string Id, BoundaryType Type, string? Region, string? Zone, IReadOnlyList<string> Members);

public sealed record Flow(string UseCaseId, IReadOnlyList<string> Steps);

public sealed record Annotation(string Id, string Text, string? Near);

/// <summary>IR — Architecture Graph: o grafo tipado e agnóstico de provedor que o motor testa.</summary>
public sealed record ArchitectureGraph
{
    public string IrVersion { get; init; } = "1.0";
    public CloudProvider Provider { get; init; }
    public string CatalogVersion { get; init; } = "";
    public IReadOnlyList<Node> Nodes { get; init; } = [];
    public IReadOnlyList<Edge> Edges { get; init; } = [];
    public IReadOnlyList<Boundary> Boundaries { get; init; } = [];
    public IReadOnlyList<Flow> Flows { get; init; } = [];
    public IReadOnlyList<Annotation> Annotations { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<string>> SourceMap { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    public Node? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public Node GetNode(string id) => FindNode(id) ?? throw new KeyNotFoundException($"Nó '{id}' não existe na IR.");

    public Edge? FindEdge(string id) => Edges.FirstOrDefault(e => e.Id == id);

    public IEnumerable<Edge> Outgoing(string nodeId) => Edges.Where(e => e.From == nodeId);

    public IEnumerable<Edge> Incoming(string nodeId) => Edges.Where(e => e.To == nodeId);

    public Flow? FlowFor(string useCaseId) => Flows.FirstOrDefault(f => f.UseCaseId == useCaseId);
}
