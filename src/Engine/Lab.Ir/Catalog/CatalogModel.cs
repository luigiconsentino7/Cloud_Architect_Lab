using System.Text.Json.Nodes;

namespace Lab.Ir.Catalog;

/// <summary>Distribuição de latência definida por p50 e p99, em milissegundos (modelada como log-normal).</summary>
public sealed record LatencyDist(double P50, double P99)
{
    public LatencyDist Scale(double factor) => new(P50 * factor, P99 * factor);

    public LatencyDist Add(LatencyDist other) => new(P50 + other.P50, P99 + other.P99);
}

public sealed record InstanceClass
{
    public double Vcpu { get; init; } = 2;
    public double MemoryGb { get; init; } = 8;
    public int MaxConnections { get; init; } = 500;

    /// <summary>Multiplicador de capacidade de operações em relação à classe de referência (1,0).</summary>
    public double OpsFactor { get; init; } = 1;

    public double PricePerHour { get; init; }
}

/// <summary>Parâmetros de desempenho. Todos opcionais: o componente sobrescreve o arquétipo.</summary>
public sealed record PerformanceProfile
{
    public LatencyDist? Latency { get; init; }
    public LatencyDist? ReadLatency { get; init; }
    public LatencyDist? WriteLatency { get; init; }
    public LatencyDist? TransactionLatency { get; init; }
    public LatencyDist? DeliveryLatency { get; init; }
    public LatencyDist? ColdStart { get; init; }
    public double? MaxRps { get; init; }
    public double? Burst { get; init; }
    public double? ReadsPerSecondPerPartition { get; init; }
    public double? WritesPerSecondPerPartition { get; init; }
    public double? WritesPerSecondPerKey { get; init; }
    public double? RecordsPerSecondPerPartition { get; init; }
    public double? MessagesPerSecondPerGroup { get; init; }
    public double? MaxMessagesPerSecond { get; init; }
    public int? AccountConcurrencyLimit { get; init; }
    public double? ScaleAddConcurrency { get; init; }
    public double? ScaleEverySeconds { get; init; }
    public double? ProvisioningSeconds { get; init; }
    public double? FailoverSeconds { get; init; }
    public double? ReplicaLagMs { get; init; }
    public double? ConnectionsPerInstance { get; init; }
    public double? OpsPerSecondPerVcpu { get; init; }
    public double? Availability { get; init; }
    public IReadOnlyDictionary<string, double>? ServiceTimeFactorByMemory { get; init; }
    public IReadOnlyDictionary<string, InstanceClass>? InstanceClasses { get; init; }

    public PerformanceProfile Merge(PerformanceProfile? over)
    {
        if (over is null) return this;
        return new PerformanceProfile
        {
            Latency = over.Latency ?? Latency,
            ReadLatency = over.ReadLatency ?? ReadLatency,
            WriteLatency = over.WriteLatency ?? WriteLatency,
            TransactionLatency = over.TransactionLatency ?? TransactionLatency,
            DeliveryLatency = over.DeliveryLatency ?? DeliveryLatency,
            ColdStart = over.ColdStart ?? ColdStart,
            MaxRps = over.MaxRps ?? MaxRps,
            Burst = over.Burst ?? Burst,
            ReadsPerSecondPerPartition = over.ReadsPerSecondPerPartition ?? ReadsPerSecondPerPartition,
            WritesPerSecondPerPartition = over.WritesPerSecondPerPartition ?? WritesPerSecondPerPartition,
            WritesPerSecondPerKey = over.WritesPerSecondPerKey ?? WritesPerSecondPerKey,
            RecordsPerSecondPerPartition = over.RecordsPerSecondPerPartition ?? RecordsPerSecondPerPartition,
            MessagesPerSecondPerGroup = over.MessagesPerSecondPerGroup ?? MessagesPerSecondPerGroup,
            MaxMessagesPerSecond = over.MaxMessagesPerSecond ?? MaxMessagesPerSecond,
            AccountConcurrencyLimit = over.AccountConcurrencyLimit ?? AccountConcurrencyLimit,
            ScaleAddConcurrency = over.ScaleAddConcurrency ?? ScaleAddConcurrency,
            ScaleEverySeconds = over.ScaleEverySeconds ?? ScaleEverySeconds,
            ProvisioningSeconds = over.ProvisioningSeconds ?? ProvisioningSeconds,
            FailoverSeconds = over.FailoverSeconds ?? FailoverSeconds,
            ReplicaLagMs = over.ReplicaLagMs ?? ReplicaLagMs,
            ConnectionsPerInstance = over.ConnectionsPerInstance ?? ConnectionsPerInstance,
            OpsPerSecondPerVcpu = over.OpsPerSecondPerVcpu ?? OpsPerSecondPerVcpu,
            Availability = over.Availability ?? Availability,
            ServiceTimeFactorByMemory = over.ServiceTimeFactorByMemory ?? ServiceTimeFactorByMemory,
            InstanceClasses = over.InstanceClasses ?? InstanceClasses,
        };
    }
}

/// <summary>Preços ilustrativos em USD. Cada arquétipo usa os campos que fazem sentido para ele.</summary>
public sealed record PricingModel
{
    public double? PerVcpuHour { get; init; }
    public double? PerGbHour { get; init; }
    public double? PerInstanceHour { get; init; }
    public double? PerMillionRequests { get; init; }
    public double? PerGbSecond { get; init; }
    public double? PerMillionReads { get; init; }
    public double? PerMillionWrites { get; init; }
    public double? PerPartitionHour { get; init; }
    public double? PerGbStoredMonth { get; init; }
    public double? PerGbTransfer { get; init; }
    public double? FixedMonthly { get; init; }
    public double? MultiAzFactor { get; init; }

    public PricingModel Merge(PricingModel? over)
    {
        if (over is null) return this;
        return new PricingModel
        {
            PerVcpuHour = over.PerVcpuHour ?? PerVcpuHour,
            PerGbHour = over.PerGbHour ?? PerGbHour,
            PerInstanceHour = over.PerInstanceHour ?? PerInstanceHour,
            PerMillionRequests = over.PerMillionRequests ?? PerMillionRequests,
            PerGbSecond = over.PerGbSecond ?? PerGbSecond,
            PerMillionReads = over.PerMillionReads ?? PerMillionReads,
            PerMillionWrites = over.PerMillionWrites ?? PerMillionWrites,
            PerPartitionHour = over.PerPartitionHour ?? PerPartitionHour,
            PerGbStoredMonth = over.PerGbStoredMonth ?? PerGbStoredMonth,
            PerGbTransfer = over.PerGbTransfer ?? PerGbTransfer,
            FixedMonthly = over.FixedMonthly ?? FixedMonthly,
            MultiAzFactor = over.MultiAzFactor ?? MultiAzFactor,
        };
    }
}

public enum ComponentScope
{
    Zonal,
    Regional,
    Global,
}

public sealed record SourceRef(string Url, string ReviewedAt);

public sealed record ArchetypeDefinition
{
    public required string Id { get; init; }
    public string DisplayName { get; init; } = "";
    public string Category { get; init; } = "";
    public ComponentScope Scope { get; init; } = ComponentScope.Zonal;
    public PerformanceProfile Performance { get; init; } = new();
    public PricingModel Pricing { get; init; } = new();
    public IReadOnlyDictionary<string, string> Capabilities { get; init; } = new Dictionary<string, string>();
    public JsonObject? ConfigSchema { get; init; }
}

/// <summary>Um serviço de nuvem concreto, já mesclado com os padrões do seu arquétipo.</summary>
public sealed record ComponentDefinition
{
    public required string Id { get; init; }
    public CloudProvider Provider { get; init; }
    public required string Archetype { get; init; }
    public string DisplayName { get; init; } = "";
    public string? ShortName { get; init; }
    public string Category { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public ComponentScope Scope { get; init; } = ComponentScope.Zonal;

    /// <summary>Componente padrão do provedor para o arquétipo (usado no mapeamento das soluções de referência).</summary>
    public bool IsDefault { get; init; }

    /// <summary>Disponível em todas as paletas (clientes e sistemas externos).</summary>
    public bool Shared { get; init; }

    public IReadOnlyDictionary<string, string> Capabilities { get; init; } = new Dictionary<string, string>();
    public JsonObject ConfigSchema { get; init; } = [];
    public PerformanceProfile Performance { get; init; } = new();
    public PricingModel Pricing { get; init; } = new();
    public string? Docs { get; init; }
    public SourceRef? Source { get; init; }

    public string Capability(string key) => Capabilities.TryGetValue(key, out var value) ? value : "";

    public bool Has(string key) =>
        Capabilities.TryGetValue(key, out var value) && value is not ("" or "false" or "none" or "no");
}

public sealed class ComponentCatalog
{
    private readonly Dictionary<string, ComponentDefinition> _components;
    private readonly Dictionary<string, ArchetypeDefinition> _archetypes;

    public ComponentCatalog(string version, IEnumerable<ArchetypeDefinition> archetypes, IEnumerable<ComponentDefinition> components)
    {
        Version = version;
        _archetypes = archetypes.ToDictionary(a => a.Id, StringComparer.Ordinal);
        _components = components.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    public string Version { get; }

    public IReadOnlyDictionary<string, ArchetypeDefinition> Archetypes => _archetypes;

    public IReadOnlyDictionary<string, ComponentDefinition> Components => _components;

    public ComponentDefinition? Find(string id) => _components.GetValueOrDefault(id);

    public ComponentDefinition Get(string id) =>
        Find(id) ?? throw new KeyNotFoundException($"Componente '{id}' não existe no catálogo {Version}.");

    public ArchetypeDefinition? FindArchetype(string id) => _archetypes.GetValueOrDefault(id);

    /// <summary>Componentes visíveis na paleta de um provedor: os dele + os compartilhados.</summary>
    public IEnumerable<ComponentDefinition> ForProvider(CloudProvider provider) =>
        _components.Values
            .Where(c => c.Provider == provider || c.Shared)
            .OrderBy(c => c.Category, StringComparer.Ordinal)
            .ThenBy(c => c.DisplayName, StringComparer.Ordinal);

    public ComponentDefinition? DefaultFor(CloudProvider provider, string archetype)
    {
        var candidates = _components.Values.Where(c => c.Archetype == archetype && (c.Provider == provider || c.Shared)).ToList();
        return candidates.FirstOrDefault(c => c.IsDefault && c.Provider == provider)
            ?? candidates.FirstOrDefault(c => c.Provider == provider)
            ?? candidates.FirstOrDefault(c => c.IsDefault)
            ?? candidates.FirstOrDefault();
    }
}
