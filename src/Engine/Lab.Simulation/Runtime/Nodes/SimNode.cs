using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Simulation.Core;

namespace Lab.Simulation.Runtime.Nodes;

/// <summary>Modelo de simulação de um componente. Cada arquétipo tem o seu.</summary>
internal abstract class SimNode
{
    private List<Action>? _capacityListeners;

    protected SimNode(World world, Node node)
    {
        World = world;
        Node = node;
        Definition = world.DefinitionOf(node);
        Rng = Rng.For(world.Seed, $"node:{node.Id}");
        Arrivals = new int[world.SeriesSeconds];
        ThrottledSeries = new int[world.SeriesSeconds];
        UtilizationSeries = new double[world.SeriesSeconds];
        QueueSeries = new int[world.SeriesSeconds];
        InstanceSeries = new int[world.SeriesSeconds];
    }

    public World World { get; }

    public Node Node { get; }

    public ComponentDefinition Definition { get; }

    protected Rng Rng { get; }

    protected SimKernel K => World.Kernel;

    protected PerformanceProfile Perf => Definition.Performance;

    protected NodeConfig Cfg => Node.Cfg;

    public long Requests { get; protected set; }

    public long Throttled { get; protected set; }

    public long Errors { get; protected set; }

    public long Rejected { get; protected set; }

    public long ConnectionErrors { get; protected set; }

    public long ColdStarts { get; protected set; }

    public int ConcurrencyMax { get; protected set; }

    public int ConcurrencyLimit { get; protected set; }

    public long BacklogMax { get; protected set; }

    public double OldestAgeMaxSeconds { get; protected set; }

    public long DlqMessages { get; protected set; }

    public bool DataLoss { get; protected set; }

    public double ServiceMsSum { get; private set; }

    public long ServiceCount { get; private set; }

    public int[] Arrivals { get; }

    public int[] ThrottledSeries { get; }

    public double[] UtilizationSeries { get; }

    public int[] QueueSeries { get; }

    public int[] InstanceSeries { get; }

    protected bool DownByRegion { get; private set; }

    public virtual void Initialize()
    {
    }

    public abstract void Handle(Exec exec, Action<Outcome> respond);

    public virtual void OnZone(string zone, bool down)
    {
    }

    public virtual void OnRegion(bool down) => DownByRegion = down;

    public virtual void OnInstanceKill(int count)
    {
    }

    /// <summary>Consumidor pode receber mais uma mensagem agora (usado pelas filas e streams).</summary>
    public virtual bool CanAccept() => !DownByRegion;

    public virtual void Tick(int second)
    {
        var (utilization, queue, instances) = Sample();
        UtilizationSeries[second] = utilization;
        QueueSeries[second] = queue;
        InstanceSeries[second] = instances;
    }

    protected virtual (double Utilization, int Queue, int Instances) Sample() => (0, 0, 0);

    public void OnCapacity(Action listener) => (_capacityListeners ??= []).Add(listener);

    protected void NotifyCapacity()
    {
        if (_capacityListeners is null) return;
        foreach (var listener in _capacityListeners) K.At(K.Now, listener);
    }

    protected void Arrive()
    {
        Requests++;
        Arrivals[World.SecondIndex]++;
    }

    protected Outcome Throttle()
    {
        Throttled++;
        ThrottledSeries[World.SecondIndex]++;
        return Outcome.Fail(ErrorKind.Throttled);
    }

    protected Outcome Unavailable()
    {
        Errors++;
        return Outcome.Fail(ErrorKind.Unavailable);
    }

    protected double Sample(LatencyDist? distribution, double fallbackMs) =>
        distribution is null ? fallbackMs : Rng.LogNormal(distribution);

    protected double InjectedLatencyMs() =>
        World.Faults.InjectedLatency(Node.Id, World.Seconds) is { } extra ? Rng.LogNormal(extra) : 0;

    protected double ThrottleFactor => World.Faults.ThrottleFactor(Node.Id, Node.Archetype, World.Seconds);

    protected void RecordService(double milliseconds)
    {
        ServiceMsSum += milliseconds;
        ServiceCount++;
    }

    protected static IReadOnlyList<DataOpKind> ExpandOps(IReadOnlyList<DataOp>? ops)
    {
        if (ops is null || ops.Count == 0) return [DataOpKind.Read];
        var list = new List<DataOpKind>();
        foreach (var op in ops)
        {
            for (var i = 0; i < Math.Max(1, op.Count); i++) list.Add(op.Op);
        }

        return list;
    }

    /// <summary>Registra o efeito de uma escrita: deduplicado por escrita atômica + chave, ou pelo consumidor idempotente.</summary>
    protected void RecordWrites(Exec exec)
    {
        var ops = exec.Call.Edge?.Config.Ops;
        if (ops is null || !ops.Any(o => o.IsWrite) || !Archetypes.IsDurableData(Node.Archetype)) return;
        var atomic = ops.Any(o => o.Op is DataOpKind.ConditionalWrite or DataOpKind.Transaction);
        var deduplicated = exec.Message is null
            ? atomic && exec.Request.HasIdempotencyKey
            : atomic || ConsumerIsIdempotent(exec);
        World.Integrity.RecordEffect(exec.Request, exec.Call.Edge!.Id, deduplicated, exec.Request.UseCase.Spec.Monetary);
    }

    protected static bool ConsumerIsIdempotent(Exec exec) =>
        exec.Message is not null && exec.Caller?.Node.Cfg.Bool("idempotency.enabled") == true;

    protected double MeasuredUtilization(double[] series)
    {
        var from = (int)World.Input.Curve.Warmup;
        var to = Math.Min(series.Length, (int)World.Input.Curve.Total);
        if (to <= from) return 0;
        var sum = 0.0;
        for (var i = from; i < to; i++) sum += series[i];
        return sum / (to - from);
    }
}

internal static class NodeFactory
{
    public static SimNode Create(World world, Node node) => node.Archetype switch
    {
        Archetypes.Client => new ClientNode(world, node),
        Archetypes.External => new ExternalNode(world, node),
        Archetypes.ApiGateway => new GatewayNode(world, node),
        Archetypes.Service or Archetypes.StreamProcessor => new ServiceNode(world, node),
        Archetypes.Function => new FunctionNode(world, node),
        Archetypes.Relational or Archetypes.RelationalDistributed => new RelationalNode(world, node),
        Archetypes.KeyValue or Archetypes.Document => new KeyValueNode(world, node),
        Archetypes.Cache => new CacheNode(world, node),
        Archetypes.ObjectStorage => new ObjectStorageNode(world, node),
        Archetypes.Queue => new QueueNode(world, node),
        Archetypes.Topic or Archetypes.EventBus => new TopicNode(world, node),
        Archetypes.Stream => new StreamNode(world, node),
        _ => new PassThroughNode(world, node),
    };
}

internal sealed class ClientNode(World world, Node node) : SimNode(world, node)
{
    public override void Handle(Exec exec, Action<Outcome> respond) => respond(Outcome.Success);
}

/// <summary>Borda gerenciada, balanceador, WAF, CDN, orquestrador: latência e repasse.</summary>
internal sealed class PassThroughNode(World world, Node node) : SimNode(world, node)
{
    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        if (!base.CanAccept())
        {
            respond(Unavailable());
            return;
        }

        Arrive();
        var latency = Sample(Perf.Latency, 0.3) + InjectedLatencyMs();
        K.AfterMs(latency, () => World.RunChildren(exec, respond));
    }
}

/// <summary>Gateway de API: limite de requisições (token bucket) + latência.</summary>
internal sealed class GatewayNode : SimNode
{
    private readonly TokenBucket _bucket;

    public GatewayNode(World world, Node node) : base(world, node)
    {
        var rate = Math.Min(Cfg.Double("throttle.rps", double.PositiveInfinity), Perf.MaxRps ?? double.PositiveInfinity);
        var burst = Math.Min(Cfg.Double("throttle.burst", rate), Perf.Burst ?? rate);
        _bucket = new TokenBucket(rate, double.IsPositiveInfinity(burst) ? 1_000_000 : burst);
    }

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        if (!base.CanAccept())
        {
            respond(Unavailable());
            return;
        }

        Arrive();
        if (!_bucket.TryTake(K.Now, ThrottleFactor))
        {
            respond(Throttle());
            return;
        }

        var latency = Sample(Perf.Latency, 5) + InjectedLatencyMs();
        K.AfterMs(latency, () => World.RunChildren(exec, respond));
    }
}

/// <summary>Sistema externo do desafio (SPI, bandeira, push…): latência, disponibilidade, limite e falhas injetadas.</summary>
internal sealed class ExternalNode : SimNode
{
    private readonly ExternalSystemSpec _spec;
    private readonly TokenBucket? _bucket;

    public ExternalNode(World world, Node node) : base(world, node)
    {
        _spec = world.Input.Challenge.FindExternal(node.Id)
            ?? new ExternalSystemSpec { Id = node.Id, Name = node.Label };
        if (_spec.RateLimitRps is { } limit) _bucket = new TokenBucket(limit, limit);
    }

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        Arrive();
        if (World.Faults.IsDown(Node.Id, World.Seconds))
        {
            K.AfterMs(30, () => respond(Unavailable()));
            return;
        }

        if (_bucket is not null && !_bucket.TryTake(K.Now))
        {
            respond(Throttle());
            return;
        }

        var latency = Rng.LogNormal(_spec.Latency) + InjectedLatencyMs();
        var fails = Rng.Chance(1 - _spec.Availability);
        K.AfterMs(latency, () =>
        {
            if (fails)
            {
                Errors++;
                respond(Outcome.Fail(ErrorKind.Dependency));
                return;
            }

            if (_spec.SideEffect)
            {
                World.Integrity.RecordEffect(exec.Request, exec.Call.Edge!.Id,
                    _spec.Idempotent || ConsumerIsIdempotent(exec),
                    exec.Request.UseCase.Spec.Monetary && _spec.Monetary);
            }

            RecordService(latency);
            respond(Outcome.Success);
        });
    }
}
