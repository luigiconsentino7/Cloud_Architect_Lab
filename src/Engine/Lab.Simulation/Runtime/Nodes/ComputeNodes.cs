using System.Globalization;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Simulation.Core;

namespace Lab.Simulation.Runtime.Nodes;

/// <summary>
/// Serviço em contêineres: requisições simultâneas por instância (slots) + CPU (núcleos), autoscaling com atraso
/// de provisionamento, instâncias distribuídas pelas zonas.
/// </summary>
internal sealed class ServiceNode : SimNode
{
    private const double ScaleIntervalSeconds = 15;
    private readonly ServerPool _slots;
    private readonly ServerPool _cpu;
    private readonly int _concurrency;
    private readonly double _vcpu;
    private readonly double _cpuFactor;
    private readonly int _min;
    private readonly int _max;
    private readonly bool _autoscale;
    private readonly double _target;
    private readonly double _provisionSeconds;
    private readonly List<string> _zones;
    private readonly Dictionary<string, int> _byZone = new(StringComparer.Ordinal);
    private readonly HashSet<string> _downZones = new(StringComparer.Ordinal);
    private int _pending;
    private double _lastBusy;
    private double _lastCapacity;
    private double _lastScaleAt;
    private double _secondBusy;
    private double _secondCapacity;

    public ServiceNode(World world, Node node) : base(world, node)
    {
        if (node.Archetype == Archetypes.StreamProcessor)
        {
            var parallelism = Math.Max(1, Cfg.Int("parallelism", 4));
            _min = _max = parallelism;
            _autoscale = false;
            _concurrency = 50;
        }
        else
        {
            _min = Math.Max(1, Cfg.Int("instances.min", 2));
            _max = Math.Max(_min, Cfg.Int("instances.max", 10));
            _autoscale = Cfg.Bool("autoscaling.enabled", true);
            _concurrency = Math.Max(1, Cfg.Int("concurrencyPerInstance", 200));
        }

        _vcpu = Math.Max(0.25, Cfg.Double("vcpu", 1));
        _cpuFactor = 1 / Math.Min(1, _vcpu);
        _target = Math.Clamp(Cfg.Double("autoscaling.targetCpu", 0.6), 0.1, 0.95);
        _provisionSeconds = Perf.ProvisioningSeconds ?? 90;
        _zones = node.Placement.Zones.Count > 0 ? [.. node.Placement.Zones] : ["a"];
        _slots = new ServerPool(K, 0);
        _cpu = new ServerPool(K, 0);
        for (var i = 0; i < _min; i++) Place();
        UpdateCapacity();
    }

    public int Instances => _byZone.Values.Sum();

    public int InstancesMin { get; private set; } = int.MaxValue;

    public int InstancesMax { get; private set; }

    public double InstanceSecondsSum { get; private set; }

    public int InstanceSamples { get; private set; }

    public int MaxQueue => _slots.MaxWaitingObserved;

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        if (!base.CanAccept() || Instances == 0)
        {
            respond(Unavailable());
            return;
        }

        Arrive();
        var accepted = _slots.Request(() =>
        {
            var cpuMs = World.ComputeCostMs(exec) * _cpuFactor;
            _cpu.Request(() => K.AfterMs(cpuMs, () =>
            {
                _cpu.Release();
                RecordService(cpuMs);
                World.RunChildren(exec, outcome =>
                {
                    _slots.Release();
                    NotifyCapacity();
                    respond(outcome);
                });
            }));
        });

        if (!accepted)
        {
            Rejected++;
            respond(Outcome.Fail(ErrorKind.Rejected));
        }
    }

    public override bool CanAccept() => base.CanAccept() && Instances > 0 && _slots.HasFreeServer;

    public override void Tick(int second)
    {
        var (busy, _, capacity) = _cpu.Integrals();
        var deltaCapacity = capacity - _secondCapacity;
        var utilization = deltaCapacity > 0 ? (busy - _secondBusy) / deltaCapacity : (Instances == 0 ? 0 : 1);
        _secondBusy = busy;
        _secondCapacity = capacity;
        UtilizationSeries[second] = Math.Clamp(utilization, 0, 1);
        QueueSeries[second] = _slots.Waiting;
        InstanceSeries[second] = Instances;

        if (second >= World.Input.Curve.Warmup && second <= World.Input.Curve.Total)
        {
            InstancesMin = Math.Min(InstancesMin, Instances);
            InstancesMax = Math.Max(InstancesMax, Instances);
            InstanceSecondsSum += Instances;
            InstanceSamples++;
        }

        if (World.Seconds - _lastScaleAt >= ScaleIntervalSeconds) Scale();
    }

    private void Scale()
    {
        _lastScaleAt = World.Seconds;
        var (busy, _, capacity) = _cpu.Integrals();
        var deltaCapacity = capacity - _lastCapacity;
        var utilization = deltaCapacity > 0 ? (busy - _lastBusy) / deltaCapacity : 0;
        _lastBusy = busy;
        _lastCapacity = capacity;

        var deficit = _min - (Instances + _pending);
        if (deficit > 0)
        {
            Provision(deficit);
            return;
        }

        if (!_autoscale) return;
        if (utilization > _target && Instances + _pending < _max)
        {
            var desired = Math.Min(_max, (int)Math.Ceiling(Instances * utilization / _target));
            var add = desired - Instances - _pending;
            if (add > 0) Provision(add);
        }
        else if (utilization < _target * 0.4 && Instances > _min && _pending == 0)
        {
            Remove(1);
        }
    }

    private void Provision(int count)
    {
        _pending += count;
        K.AfterMs(_provisionSeconds * 1000, () =>
        {
            _pending -= count;
            for (var i = 0; i < count; i++) Place();
            UpdateCapacity();
        });
    }

    private void Place()
    {
        var healthy = _zones.Where(z => !_downZones.Contains(z)).ToList();
        if (healthy.Count == 0) return;
        var zone = healthy.OrderBy(z => _byZone.GetValueOrDefault(z)).ThenBy(z => z, StringComparer.Ordinal).First();
        _byZone[zone] = _byZone.GetValueOrDefault(zone) + 1;
    }

    private void Remove(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var zone = _byZone.Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key).FirstOrDefault();
            if (zone is null) break;
            _byZone[zone]--;
        }

        UpdateCapacity();
    }

    public override void OnZone(string zone, bool down)
    {
        if (down)
        {
            _downZones.Add(zone);
            if (_byZone.ContainsKey(zone)) _byZone[zone] = 0;
            UpdateCapacity();
        }
        else
        {
            _downZones.Remove(zone);
        }
    }

    public override void OnInstanceKill(int count) => Remove(count);

    private void UpdateCapacity()
    {
        var instances = Instances;
        _slots.SetCapacity(instances * _concurrency);
        _slots.MaxQueue = Math.Max(100, instances * _concurrency * 2);
        _cpu.SetCapacity((int)Math.Ceiling(instances * Math.Max(1, _vcpu)));
        ConcurrencyLimit = instances * _concurrency;
        NotifyCapacity();
    }
}

/// <summary>
/// Função serverless: cada execução ocupa uma unidade de concorrência durante TODA a duração (inclusive esperando
/// chamadas a jusante) — é daqui que vem o "abismo de concorrência". Cold start acima do pool quente.
/// </summary>
internal sealed class FunctionNode : SimNode
{
    private readonly int _limit;
    private readonly int _provisioned;
    private readonly double _memoryFactor;
    private readonly double _timeoutMs;
    private readonly LatencyDist _coldStart;
    private int _concurrency;
    private int _warm;
    private int _peakWindow;
    private double _lastWarmUpdate;

    public FunctionNode(World world, Node node) : base(world, node)
    {
        var reserved = Cfg.Int("reservedConcurrency", 0);
        var account = Perf.AccountConcurrencyLimit ?? 1000;
        _limit = reserved > 0 ? reserved : account;
        _provisioned = Math.Min(_limit, Math.Max(0, Cfg.Int("provisionedConcurrency", 0)));
        _warm = _provisioned;
        var memory = Cfg.Int("memoryMb", 1024).ToString(CultureInfo.InvariantCulture);
        _memoryFactor = Perf.ServiceTimeFactorByMemory?.GetValueOrDefault(memory, 1) ?? 1;
        _timeoutMs = Cfg.Double("timeoutMs", 10_000);
        _coldStart = Perf.ColdStart ?? new LatencyDist(300, 1200);
        ConcurrencyLimit = _limit;
    }

    public double DurationMsSum { get; private set; }

    public long Completed { get; private set; }

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        if (!base.CanAccept())
        {
            respond(Unavailable());
            return;
        }

        Arrive();
        if (_concurrency >= _limit)
        {
            respond(Throttle());
            return;
        }

        _concurrency++;
        ConcurrencyMax = Math.Max(ConcurrencyMax, _concurrency);
        _peakWindow = Math.Max(_peakWindow, _concurrency);
        var cold = _concurrency > _warm;
        if (cold)
        {
            _warm = _concurrency;
            ColdStarts++;
        }

        var startedAt = K.Now;
        var cpuMs = World.ComputeCostMs(exec) * _memoryFactor;
        var initMs = cold ? Rng.LogNormal(_coldStart) : 0;
        RecordService(cpuMs);
        var finished = false;

        void Finish(Outcome outcome)
        {
            if (finished) return;
            finished = true;
            _concurrency--;
            DurationMsSum += (K.Now - startedAt) / 1000.0;
            Completed++;
            NotifyCapacity();
            respond(outcome);
        }

        K.AfterMs(_timeoutMs, () => Finish(Outcome.Fail(ErrorKind.Timeout)));
        K.AfterMs(initMs + cpuMs, () => World.RunChildren(exec, Finish));
    }

    public override bool CanAccept() => base.CanAccept() && _concurrency < _limit;

    public override void Tick(int second)
    {
        UtilizationSeries[second] = _limit == 0 ? 0 : _concurrency / (double)_limit;
        InstanceSeries[second] = _concurrency;
        if (World.Seconds - _lastWarmUpdate >= 60)
        {
            _lastWarmUpdate = World.Seconds;
            _warm = Math.Max(_provisioned, _peakWindow);
            _peakWindow = _concurrency;
        }
    }
}
