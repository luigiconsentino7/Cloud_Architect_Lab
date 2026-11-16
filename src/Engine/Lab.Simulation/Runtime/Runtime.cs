using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;
using Lab.Simulation.Core;
using Lab.Simulation.Runtime.Nodes;

namespace Lab.Simulation.Runtime;

internal enum ErrorKind
{
    None,
    Timeout,
    Throttled,
    Unavailable,
    Rejected,
    ConnectionLimit,
    Dependency,
    CircuitOpen,
    NoPath,
}

internal readonly record struct Outcome(bool Ok, ErrorKind Error = ErrorKind.None, bool CacheHit = false)
{
    public static readonly Outcome Success = new(true);

    public static Outcome Fail(ErrorKind error) => new(false, error);

    public static string Name(ErrorKind error) => error switch
    {
        ErrorKind.Timeout => "timeout",
        ErrorKind.Throttled => "throttled",
        ErrorKind.Unavailable => "unavailable",
        ErrorKind.Rejected => "rejected",
        ErrorKind.ConnectionLimit => "connection-limit",
        ErrorKind.Dependency => "dependency",
        ErrorKind.CircuitOpen => "circuit-open",
        ErrorKind.NoPath => "no-path",
        _ => "none",
    };
}

internal sealed class UseCaseRuntime(UseCaseSpec spec, FlowPlan plan, UseCaseLoad load, double cacheability, int index)
{
    public UseCaseSpec Spec { get; } = spec;
    public FlowPlan Plan { get; } = plan;
    public UseCaseLoad Load { get; } = load;
    public double Cacheability { get; } = cacheability;
    public int Index { get; } = index;
    public bool EntryHasKey { get; } = plan.Root?.Children.FirstOrDefault()?.Edge?.Config.IdempotencyKey == true;
    public bool Runnable { get; } = plan.IsValid;
}

internal sealed class Request
{
    public long Id { get; init; }
    public long TxId { get; init; }
    public required UseCaseRuntime UseCase { get; init; }
    public long FirstStartUs { get; init; }
    public long StartUs { get; init; }
    public string Key { get; init; } = "";
    public bool HotKey { get; init; }
    public long KeySequence { get; init; }
    public bool HasIdempotencyKey { get; init; }
    public int Attempt { get; init; } = 1;
    public bool Measured { get; init; }
    public bool Finished { get; set; }
    public int PendingAsync { get; set; }
    public bool AsyncFailed { get; set; }
    public bool AsyncRecorded { get; set; }
    public bool Published { get; set; }

    public Request RetryCopy(long id, long nowUs, bool keepKey) => new()
    {
        Id = id,
        TxId = TxId,
        UseCase = UseCase,
        FirstStartUs = FirstStartUs,
        StartUs = nowUs,
        Key = Key,
        HotKey = HotKey,
        KeySequence = KeySequence,
        HasIdempotencyKey = keepKey && HasIdempotencyKey,
        Attempt = Attempt + 1,
        Measured = Measured,
    };
}

internal sealed class Message
{
    public required Request Request { get; init; }
    public required CallNode Publish { get; init; }
    public required CallNode Consumer { get; init; }
    public long EnqueuedUs { get; set; }
    public int Deliveries { get; set; }
    public int Attempt { get; set; }
    public bool Acked { get; set; }
    public bool Duplicate { get; init; }
    public int Partition { get; init; }
}

/// <summary>Uma execução de uma chamada (requisição × nó do fluxo).</summary>
internal sealed class Exec(Request request, CallNode call, Message? message, SimNode? caller)
{
    private HashSet<string>? _missed;

    public Request Request { get; } = request;
    public CallNode Call { get; } = call;
    public Message? Message { get; } = message;
    public SimNode? Caller { get; } = caller;
    public bool Abandoned { get; set; }

    public void MarkMiss(string edgeId) => (_missed ??= new HashSet<string>(StringComparer.Ordinal)).Add(edgeId);

    public bool Missed(string? edgeId) =>
        _missed is not null && (edgeId is null ? _missed.Count > 0 : _missed.Contains(edgeId));
}

internal sealed class UseCaseMetrics(int seconds)
{
    public long Requests;
    public long Succeeded;
    public readonly Dictionary<ErrorKind, long> Errors = [];
    public readonly LatencyHistogram Latency = new();
    public readonly LatencyHistogram AsyncLatency = new();
    public long AsyncExpected;
    public long AsyncCompleted;
    public long AsyncFailed;
    public readonly int[] SeriesRequests = new int[seconds];
    public readonly int[] SeriesErrors = new int[seconds];
    public readonly LatencyHistogram[] SeriesLatency = Enumerable.Range(0, seconds).Select(_ => new LatencyHistogram(subBuckets: 4)).ToArray();
}

/// <summary>Contagem de efeitos por transação lógica: é aqui que aparecem as cobranças duplicadas.</summary>
internal sealed class IntegrityLedger
{
    private readonly Dictionary<(long TxId, int Site), int> _effects = [];
    private readonly Dictionary<string, int> _sites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastSequence = new(StringComparer.Ordinal);

    public long DuplicateEffects;
    public long MonetaryDuplicates;
    public long LostEvents;
    public long OrderingViolations;
    public long StaleDiscarded;
    public long DlqMessages;

    public void RecordEffect(Request request, string siteId, bool deduplicated, bool monetary)
    {
        if (!_sites.TryGetValue(siteId, out var site))
        {
            site = _sites.Count;
            _sites[siteId] = site;
        }

        var key = (request.TxId, site);
        var count = _effects.GetValueOrDefault(key);
        if (count >= 1 && deduplicated) return;
        _effects[key] = count + 1;
        if (count >= 1 && request.Measured)
        {
            DuplicateEffects++;
            if (monetary) MonetaryDuplicates++;
        }
    }

    /// <param name="sequenceCheck">O consumidor compara a sequência e descarta a versão mais antiga: o estado fica certo.</param>
    public void RecordOrder(Request request, bool sequenceCheck = false)
    {
        if (_lastSequence.Count > 200_000 && !_lastSequence.ContainsKey(request.Key)) return;
        if (_lastSequence.TryGetValue(request.Key, out var last) && request.KeySequence < last)
        {
            if (sequenceCheck) StaleDiscarded++;
            else if (request.Measured) OrderingViolations++;
            return;
        }

        _lastSequence[request.Key] = request.KeySequence;
    }
}

/// <summary>Falhas do cenário e suas janelas de tempo.</summary>
internal sealed class FaultState(IReadOnlyList<FaultSpec> faults)
{
    public IReadOnlyList<FaultSpec> All { get; } = faults;

    private static bool Active(FaultSpec fault, double seconds) =>
        seconds >= fault.At && (fault.For is not { } length || seconds < fault.At + length);

    public double Probability(FaultType type, double seconds) =>
        All.Where(f => f.Type == type && Active(f, seconds)).Select(f => f.Probability).DefaultIfEmpty(0).Max();

    public FaultSpec? ClientRetry(double seconds) => All.FirstOrDefault(f => f.Type == FaultType.ClientRetry && Active(f, seconds));

    public bool IsDown(string nodeId, double seconds) =>
        All.Any(f => f.Type == FaultType.DependencyOutage && Matches(f.Target, nodeId) && Active(f, seconds));

    public LatencyDist? InjectedLatency(string nodeId, double seconds) =>
        All.FirstOrDefault(f => f.Type == FaultType.LatencyInjection && Matches(f.Target, nodeId) && Active(f, seconds))?.AddMs;

    public double ThrottleFactor(string nodeId, string archetype, double seconds) =>
        All.Where(f => f.Type == FaultType.Throttling && Active(f, seconds) && (Matches(f.Target, nodeId) || f.Target == $"archetype:{archetype}"))
            .Select(f => f.Factor ?? 0.5)
            .DefaultIfEmpty(1)
            .Min();

    private static bool Matches(string? target, string nodeId) => target == nodeId || target == "*";
}
