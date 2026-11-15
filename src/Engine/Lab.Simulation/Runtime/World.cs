using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;
using Lab.Simulation.Core;
using Lab.Simulation.Runtime.Nodes;

namespace Lab.Simulation.Runtime;

internal sealed record WorldInput(
    ArchitectureGraph Graph,
    ChallengeSpec Challenge,
    ComponentCatalog Catalog,
    IReadOnlyDictionary<string, FlowPlan> Plans,
    ScenarioSpec Scenario,
    LoadCurve Curve,
    long Seed,
    long EventBudget);

/// <summary>Um mundo simulado para um cenário: nós, clientes, falhas e métricas.</summary>
internal sealed class World
{
    private const double DrainSeconds = 30;
    private readonly Rng _arrivals;
    private readonly Rng _mix;
    private readonly Rng _keys;
    private readonly Rng _network;
    private readonly Rng _compute;
    private readonly Rng _faults;
    private readonly Rng _backoff;
    private readonly Rng _relay;
    private readonly Dictionary<string, Breaker> _breakers = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, string), long> _networkCache = [];
    private readonly List<UseCaseRuntime> _runnable;
    private readonly double[] _cumulativeShares;
    private readonly double[] _offered;
    private long _nextRequestId;
    private long _nextTxId;

    public World(WorldInput input)
    {
        Input = input;
        Seed = input.Seed;
        Kernel = new SimKernel { EventBudget = input.EventBudget };
        Faults = new FaultState(input.Scenario.Faults);
        SeriesSeconds = (int)Math.Ceiling(input.Curve.Total + DrainSeconds) + 1;
        _offered = new double[SeriesSeconds];

        _arrivals = Rng.For(Seed, "arrivals");
        _mix = Rng.For(Seed, "mix");
        _keys = Rng.For(Seed, "keys");
        _network = Rng.For(Seed, "network");
        _compute = Rng.For(Seed, "compute");
        _faults = Rng.For(Seed, "faults");
        _backoff = Rng.For(Seed, "backoff");
        _relay = Rng.For(Seed, "relay");

        var index = 0;
        foreach (var useCase in input.Challenge.UseCases)
        {
            var load = input.Challenge.Workload.Mix.GetValueOrDefault(useCase.Id) ?? new UseCaseLoad { Share = 0 };
            var runtime = new UseCaseRuntime(useCase, input.Plans[useCase.Id], load,
                input.Challenge.Workload.Cacheability.GetValueOrDefault(useCase.Id), index++);
            UseCases[useCase.Id] = runtime;
            Metrics[useCase.Id] = new UseCaseMetrics(SeriesSeconds);
        }

        _runnable = UseCases.Values.Where(u => u.Load.Share > 0).ToList();
        var totalShare = _runnable.Sum(u => u.Load.Share);
        _cumulativeShares = new double[_runnable.Count];
        var cumulative = 0.0;
        for (var i = 0; i < _runnable.Count; i++)
        {
            cumulative += _runnable[i].Load.Share / totalShare;
            _cumulativeShares[i] = cumulative;
        }

        PeakRates = RateEstimator.EdgeRates(input.Challenge, input.Plans, input.Curve.Peak);
        foreach (var node in input.Graph.Nodes)
            Nodes[node.Id] = NodeFactory.Create(this, node);
        foreach (var node in Nodes.Values) node.Initialize();
    }

    public WorldInput Input { get; }

    public long Seed { get; }

    public SimKernel Kernel { get; }

    public FaultState Faults { get; }

    public IntegrityLedger Integrity { get; } = new();

    public Dictionary<string, SimNode> Nodes { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, UseCaseRuntime> UseCases { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, UseCaseMetrics> Metrics { get; } = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> PeakRates { get; }

    public int SeriesSeconds { get; }

    public IReadOnlyList<double> Offered => _offered;

    public double Seconds => Kernel.NowSeconds;

    public int SecondIndex => Math.Clamp((int)Kernel.NowSeconds, 0, SeriesSeconds - 1);

    public ComponentDefinition DefinitionOf(Node node) =>
        Input.Catalog.Find(node.CatalogId) ?? Input.Catalog.DefaultFor(Input.Graph.Provider, node.Archetype)
        ?? new ComponentDefinition { Id = node.CatalogId, Archetype = node.Archetype };

    public void Run(CancellationToken cancellationToken)
    {
        ScheduleFaults();
        ScheduleTicks();
        ScheduleNextArrival();
        Kernel.RunUntil((long)((Input.Curve.Total + DrainSeconds) * 1_000_000), cancellationToken);
    }

    // ---------------------------------------------------------------- clientes

    private void ScheduleNextArrival()
    {
        if (_runnable.Count == 0) return;
        var now = Seconds;
        if (now >= Input.Curve.Total) return;
        var rate = Input.Curve.Rate(now);
        if (rate <= 0)
        {
            Kernel.AfterMs(100, ScheduleNextArrival);
            return;
        }

        Kernel.AfterUs(Math.Max(1, (long)(_arrivals.Exponential(rate) * 1_000_000)), Arrive);
    }

    private void Arrive()
    {
        if (Seconds >= Input.Curve.Total) return;
        _offered[SecondIndex] += 1;
        var pick = _mix.NextDouble();
        var useCase = _runnable[^1];
        for (var i = 0; i < _cumulativeShares.Length; i++)
        {
            if (pick <= _cumulativeShares[i])
            {
                useCase = _runnable[i];
                break;
            }
        }

        var (key, hot) = NextKey();
        var id = ++_nextRequestId;
        var request = new Request
        {
            Id = id,
            TxId = ++_nextTxId,
            UseCase = useCase,
            FirstStartUs = Kernel.Now,
            StartUs = Kernel.Now,
            Key = key,
            HotKey = hot,
            KeySequence = id,
            HasIdempotencyKey = useCase.EntryHasKey,
            Measured = Seconds >= Input.Curve.Warmup,
        };
        StartRequest(request);
        ScheduleNextArrival();
    }

    private (string Key, bool Hot) NextKey()
    {
        var roll = _keys.NextDouble();
        var cumulative = 0.0;
        foreach (var hot in Input.Challenge.Workload.HotKeys)
        {
            cumulative += hot.ShareOfWrites;
            if (roll < cumulative) return (hot.Key, true);
        }

        return ($"k{_keys.NextInt(Math.Max(1, Input.Challenge.Workload.Keys))}", false);
    }

    private void StartRequest(Request request)
    {
        var useCase = request.UseCase;
        if (!useCase.Runnable || useCase.Plan.Root is null)
        {
            Complete(request, Outcome.Fail(ErrorKind.NoPath));
            return;
        }

        var client = Nodes[useCase.Plan.Root.Target.Id];
        var exec = new Exec(request, useCase.Plan.Root, null, client);
        var retry = Faults.ClientRetry(Seconds);
        var timeoutMs = retry?.OnTimeoutMs ?? Input.Challenge.Workload.ClientTimeoutMs;
        var finished = false;
        Kernel.AfterMs(timeoutMs, () =>
        {
            if (finished) return;
            finished = true;
            exec.Abandoned = true;
            ClientOutcome(request, Outcome.Fail(ErrorKind.Timeout));
        });
        RunChildren(exec, outcome =>
        {
            if (finished) return;
            finished = true;
            ClientOutcome(request, outcome);
        });
    }

    private void ClientOutcome(Request request, Outcome outcome)
    {
        var retry = Faults.ClientRetry(Seconds);
        if (!outcome.Ok && retry is not null && request.Attempt <= (retry.Count ?? 1)
            && outcome.Error is ErrorKind.Timeout or ErrorKind.Unavailable or ErrorKind.Throttled)
        {
            var copy = request.RetryCopy(++_nextRequestId, Kernel.Now, retry.SameIdempotencyKey);
            Kernel.AfterMs(100, () => StartRequest(copy));
            return;
        }

        Complete(request, outcome);
    }

    private void Complete(Request request, Outcome outcome)
    {
        if (request.Finished) return;
        request.Finished = true;
        if (!request.Measured) return;
        var metrics = Metrics[request.UseCase.Spec.Id];
        var second = SecondIndex;
        metrics.Requests++;
        metrics.SeriesRequests[second]++;
        if (outcome.Ok)
        {
            metrics.Succeeded++;
            var latency = Kernel.Now - request.FirstStartUs;
            metrics.Latency.Record(latency);
            metrics.SeriesLatency[second].Record(latency);
        }
        else
        {
            metrics.Errors[outcome.Error] = metrics.Errors.GetValueOrDefault(outcome.Error) + 1;
            metrics.SeriesErrors[second]++;
        }
    }

    // ---------------------------------------------------------------- assíncrono

    public void AsyncStarted(Request request, int paths)
    {
        if (paths <= 0) return;
        if (!request.Published)
        {
            request.Published = true;
            if (request.Measured) Metrics[request.UseCase.Spec.Id].AsyncExpected++;
        }

        request.PendingAsync += paths;
    }

    public void AsyncFinished(Request request, bool ok)
    {
        if (!ok) request.AsyncFailed = true;
        request.PendingAsync--;
        if (request.PendingAsync > 0 || request.AsyncRecorded) return;
        request.AsyncRecorded = true;
        if (!request.Measured) return;
        var metrics = Metrics[request.UseCase.Spec.Id];
        if (request.AsyncFailed)
        {
            metrics.AsyncFailed++;
            return;
        }

        metrics.AsyncCompleted++;
        metrics.AsyncLatency.Record(Kernel.Now - request.FirstStartUs);
    }

    public void LostEvent(Request request)
    {
        request.AsyncFailed = true;
        if (request.Measured) Integrity.LostEvents++;
    }

    // ---------------------------------------------------------------- execução de chamadas

    public double ComputeCostMs(Exec exec) => _compute.LogNormal(exec.Request.UseCase.Load.ComputeCostMs);

    public double Probability(FaultType type) => Faults.Probability(type, Seconds);

    public bool Roll(double probability) => _faults.Chance(probability);

    public void RunChildren(Exec exec, Action<Outcome> done) => RunFrom(exec, 0, done);

    private void RunFrom(Exec exec, int index, Action<Outcome> done)
    {
        var children = exec.Call.Children;
        while (index < children.Count && !ShouldRun(exec, children[index])) index++;
        if (index >= children.Count)
        {
            done(Outcome.Success);
            return;
        }

        var child = children[index];
        if (child.Edge!.Config.ParallelGroup is { } group)
        {
            var members = new List<CallNode>();
            var next = index;
            while (next < children.Count && children[next].Edge!.Config.ParallelGroup == group)
            {
                if (ShouldRun(exec, children[next])) members.Add(children[next]);
                next++;
            }

            var remaining = members.Count;
            Outcome? failure = null;
            foreach (var member in members)
            {
                InvokeHandled(exec, member, outcome =>
                {
                    if (!outcome.Ok && failure is null) failure = outcome;
                    if (--remaining > 0) return;
                    if (failure is { } f) done(f);
                    else RunFrom(exec, next, done);
                });
            }

            return;
        }

        InvokeHandled(exec, child, outcome =>
        {
            if (outcome.Ok) RunFrom(exec, index + 1, done);
            else done(outcome);
        });
    }

    private static bool ShouldRun(Exec exec, CallNode child) => child.Trigger switch
    {
        TriggerKind.OnFailure => false,
        TriggerKind.OnCacheMiss => exec.Missed(child.Edge!.Config.TriggerOf),
        _ => true,
    };

    private void InvokeHandled(Exec exec, CallNode child, Action<Outcome> done)
    {
        Invoke(exec, child, outcome =>
        {
            var edge = child.Edge!;
            if (child.Target.Archetype == Archetypes.Cache)
            {
                var isRead = edge.Config.Ops?.Any(o => o.Op == DataOpKind.Read) ?? true;
                if (isRead && (!outcome.Ok || !outcome.CacheHit)) exec.MarkMiss(edge.Id);
                done(isRead ? Outcome.Success : outcome);
                return;
            }

            if (outcome.Ok)
            {
                done(outcome);
                return;
            }

            var fallback = exec.Call.Children.FirstOrDefault(c => c.Trigger == TriggerKind.OnFailure && c.Edge!.Config.TriggerOf == edge.Id);
            if (fallback is null) done(outcome);
            else Invoke(exec, fallback, done);
        });
    }

    public void Invoke(Exec parent, CallNode child, Action<Outcome> done)
    {
        var target = Nodes[child.Target.Id];
        switch (child.Edge!.Interaction)
        {
            case Interaction.AsyncPublish or Interaction.Cdc:
                Publish(parent, child, target, done);
                return;
            case Interaction.SyncRequest or Interaction.DataAccess:
                InvokeSync(parent, child, target, done);
                return;
            default:
                done(Outcome.Success);
                return;
        }
    }

    private void Publish(Exec parent, CallNode child, SimNode target, Action<Outcome> done)
    {
        if (target is not IBroker broker)
        {
            done(Outcome.Success);
            return;
        }

        var request = parent.Request;
        var edge = child.Edge!;
        if (edge.Interaction == Interaction.Cdc || edge.Config.Delivery == DeliveryMode.Outbox)
        {
            // Outbox/CDC: atômico com o commit; o relay publica depois e nunca perde o evento.
            Kernel.AfterMs(_relay.LogNormal(30, 150), () => PublishWithRetry(broker, request, child, 1));
            done(Outcome.Success);
            return;
        }

        var crash = Probability(FaultType.PublishCrash);
        if (crash > 0 && Roll(crash))
        {
            LostEvent(request);
            done(Outcome.Fail(ErrorKind.Unavailable));
            return;
        }

        var network = NetworkUs(Nodes[parent.Call.Target.Id], target);
        Kernel.AfterUs(network, () => broker.Publish(request, child, true, ack => Kernel.AfterUs(network, () => done(ack))));
    }

    private void PublishWithRetry(IBroker broker, Request request, CallNode child, int attempt) =>
        broker.Publish(request, child, true, ack =>
        {
            if (ack.Ok) return;
            if (attempt >= 20) LostEvent(request);
            else Kernel.AfterMs(Math.Min(5000, 100 * Math.Pow(2, attempt)), () => PublishWithRetry(broker, request, child, attempt + 1));
        });

    private void InvokeSync(Exec parent, CallNode child, SimNode target, Action<Outcome> done)
    {
        var edge = child.Edge!;
        var config = edge.Config;
        var caller = Nodes[parent.Call.Target.Id];
        var breaker = config.CircuitBreaker is { Enabled: true } policy ? BreakerFor(edge.Id, policy) : null;
        var maxAttempts = 1 + Math.Max(0, config.Retries?.Max ?? 0);
        var network = NetworkUs(caller, target);

        void Attempt(int number)
        {
            if (breaker is not null && !breaker.Allow(Kernel.Now))
            {
                done(Outcome.Fail(ErrorKind.CircuitOpen));
                return;
            }

            var exec = new Exec(parent.Request, child, parent.Message, caller);
            var finished = false;
            if (config.TimeoutMs is { } timeout)
            {
                Kernel.AfterMs(timeout, () =>
                {
                    if (finished) return;
                    finished = true;
                    exec.Abandoned = true;
                    breaker?.Record(Kernel.Now, false);
                    Failed(number, ErrorKind.Timeout);
                });
            }

            Kernel.AfterUs(network, () => target.Handle(exec, outcome => Kernel.AfterUs(network, () =>
            {
                if (finished) return;
                finished = true;
                breaker?.Record(Kernel.Now, outcome.Ok);
                if (outcome.Ok) done(outcome);
                else Failed(number, outcome.Error);
            })));
        }

        void Failed(int number, ErrorKind error)
        {
            if (number < maxAttempts && error is not (ErrorKind.CircuitOpen or ErrorKind.NoPath))
                Kernel.AfterMs(Backoff(config.Retries!, number), () => Attempt(number + 1));
            else
                done(Outcome.Fail(error));
        }

        Attempt(1);
    }

    private double Backoff(RetryPolicy policy, int attempt) => policy.Backoff switch
    {
        BackoffKind.Fixed => policy.BaseMs,
        BackoffKind.Exponential => policy.BaseMs * Math.Pow(2, attempt - 1),
        BackoffKind.ExponentialJitter => _backoff.Uniform(0, policy.BaseMs * Math.Pow(2, attempt - 1)),
        _ => 0,
    };

    private Breaker BreakerFor(string edgeId, CircuitBreakerPolicy policy)
    {
        if (!_breakers.TryGetValue(edgeId, out var breaker))
        {
            breaker = new Breaker(policy);
            _breakers[edgeId] = breaker;
        }

        return breaker;
    }

    public long NetworkUs(SimNode from, SimNode to)
    {
        if (Archetypes.IsExternal(to.Node.Archetype)) return 0;
        if (Archetypes.IsClient(from.Node.Archetype))
            return (long)(_network.LogNormal(from.Definition.Performance.Latency ?? new LatencyDist(30, 90)) / 2 * 1000);

        var key = (from.Node.Id, to.Node.Id);
        if (_networkCache.TryGetValue(key, out var cached)) return cached;
        long value;
        var a = from.Node.Placement;
        var b = to.Node.Placement;
        if (a.Region is not null && b.Region is not null && a.Region != b.Region) value = 60_000;
        else if (a.Zones.Count > 0 && b.Zones.Count > 0 && !a.Zones.Intersect(b.Zones).Any()) value = 600;
        else value = 250;
        _networkCache[key] = value;
        return value;
    }

    // ---------------------------------------------------------------- falhas e ticks

    private void ScheduleFaults()
    {
        foreach (var fault in Input.Scenario.Faults)
        {
            switch (fault.Type)
            {
                case FaultType.ZoneOutage:
                {
                    var zone = fault.Zone ?? "a";
                    Kernel.At(Us(fault.At), () => { foreach (var node in Nodes.Values) node.OnZone(zone, true); });
                    if (fault.For is { } length)
                        Kernel.At(Us(fault.At + length), () => { foreach (var node in Nodes.Values) node.OnZone(zone, false); });
                    break;
                }

                case FaultType.RegionOutage:
                {
                    Kernel.At(Us(fault.At), () => { foreach (var node in Nodes.Values) node.OnRegion(true); });
                    if (fault.For is { } length)
                        Kernel.At(Us(fault.At + length), () => { foreach (var node in Nodes.Values) node.OnRegion(false); });
                    break;
                }

                case FaultType.InstanceKill:
                {
                    Kernel.At(Us(fault.At), () =>
                    {
                        foreach (var node in Nodes.Values.Where(n => Targets(fault.Target, n)))
                            node.OnInstanceKill(fault.Count ?? 1);
                    });
                    break;
                }
            }
        }
    }

    private static bool Targets(string? target, SimNode node) =>
        target is null or "*" || target == node.Node.Id || target == $"archetype:{node.Node.Archetype}";

    private void ScheduleTicks()
    {
        void Tick()
        {
            var second = SecondIndex;
            foreach (var node in Nodes.Values) node.Tick(second);
            if (Kernel.NowSeconds + 1 < SeriesSeconds) Kernel.AfterMs(1000, Tick);
        }

        Kernel.AfterMs(1000, Tick);
    }

    private static long Us(double seconds) => (long)(seconds * 1_000_000);
}

internal interface IBroker
{
    /// <summary>Publica uma mensagem. <paramref name="countsPath"/> = abre caminhos assíncronos novos (falso quando é continuação de um tópico).</summary>
    void Publish(Request request, CallNode publishCall, bool countsPath, Action<Outcome> ack);
}

/// <summary>Circuit breaker com janela deslizante de 10 s.</summary>
internal sealed class Breaker(CircuitBreakerPolicy policy)
{
    private readonly int[] _ok = new int[10];
    private readonly int[] _fail = new int[10];
    private long _second = -1;
    private long _openUntil;

    public bool Allow(long nowUs) => nowUs >= _openUntil;

    public void Record(long nowUs, bool ok)
    {
        Advance(nowUs / 1_000_000);
        var slot = (int)(_second % 10);
        if (ok) _ok[slot]++;
        else _fail[slot]++;
        var failures = _fail.Sum();
        var total = failures + _ok.Sum();
        if (total >= 20 && failures >= policy.ErrorRateThreshold * total && nowUs >= _openUntil)
        {
            _openUntil = nowUs + (long)(policy.OpenMs * 1000);
            Array.Clear(_ok);
            Array.Clear(_fail);
        }
    }

    private void Advance(long second)
    {
        if (_second < 0) _second = second;
        var steps = 0;
        while (_second < second && steps++ < 10)
        {
            _second++;
            var slot = (int)(_second % 10);
            _ok[slot] = 0;
            _fail[slot] = 0;
        }

        _second = Math.Max(_second, second);
    }
}
