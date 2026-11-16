using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Simulation.Core;

namespace Lab.Simulation.Runtime.Nodes;

/// <summary>
/// Banco relacional: primário com escritor único (c servidores), réplicas para leitura, limite de conexões
/// (funções sem proxy), failover multi-AZ e exclusão mútua por linha quente (conta quente).
/// </summary>
internal sealed class RelationalNode : SimNode
{
    private readonly ServerPool _primary;
    private readonly List<ServerPool> _replicas = [];
    private readonly int _maxConnections;
    private readonly bool _proxy;
    private readonly bool _multiAz;
    private readonly bool _distributed;
    private readonly double _failoverSeconds;
    private readonly LatencyDist _read;
    private readonly LatencyDist _write;
    private readonly LatencyDist _transaction;
    private readonly Dictionary<string, Queue<Action>> _lockQueues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    private readonly HashSet<string> _downZones = new(StringComparer.Ordinal);
    private string _primaryZone;
    private string? _standbyZone;
    private bool _down;
    private long _downSinceUs;
    private long _unavailableUs;
    private int _open;
    private int _roundRobin;

    public RelationalNode(World world, Node node) : base(world, node)
    {
        _distributed = node.Archetype == Archetypes.RelationalDistributed;
        _read = Perf.ReadLatency ?? new LatencyDist(1.5, 8);
        _write = Perf.WriteLatency ?? new LatencyDist(3, 15);
        _transaction = Perf.TransactionLatency ?? new LatencyDist(6, 30);

        double vcpu;
        if (_distributed)
        {
            var nodes = Math.Max(1, Cfg.Int("nodes", 3));
            vcpu = nodes * 4.0;
            _maxConnections = nodes * 1000;
            _multiAz = true;
        }
        else
        {
            var instanceClass = Perf.InstanceClasses?.GetValueOrDefault(Cfg.String("instanceClass") ?? "medium")
                ?? new InstanceClass { Vcpu = 4, MaxConnections = 800 };
            vcpu = instanceClass.Vcpu * instanceClass.OpsFactor;
            _maxConnections = instanceClass.MaxConnections;
            _multiAz = Cfg.Bool("multiAz");
        }

        var servers = Math.Max(2, (int)Math.Round(vcpu * (Perf.OpsPerSecondPerVcpu ?? 1500) * Rng.Mean(_write) / 1000));
        _primary = new ServerPool(K, servers);
        for (var i = 0; i < Math.Max(0, Cfg.Int("readReplicas", 0)); i++) _replicas.Add(new ServerPool(K, servers));
        _proxy = Cfg.Bool("connectionProxy");
        _failoverSeconds = Perf.FailoverSeconds ?? 60;
        _primaryZone = node.Placement.Zones.FirstOrDefault() ?? "a";
        _standbyZone = _multiAz ? node.Placement.Zones.Skip(1).FirstOrDefault() ?? "b" : null;
        ConcurrencyLimit = _maxConnections;
    }

    public int Servers => _primary.Capacity;

    public int Failovers { get; private set; }

    /// <summary>Tempo sem aceitar operações (primário fora, à espera do failover ou da volta da zona).</summary>
    public double UnavailableSecondsAt(long nowUs) => (_unavailableUs + (_down ? nowUs - _downSinceUs : 0)) / 1_000_000.0;

    private void Recover(bool failover)
    {
        if (!_down) return;
        _unavailableUs += K.Now - _downSinceUs;
        if (failover) Failovers++;
        _down = false;
    }

    public int MaxQueue => _primary.MaxWaitingObserved;

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        if (!base.CanAccept() || _down)
        {
            Errors++;
            K.AfterMs(1, () => respond(Outcome.Fail(ErrorKind.Unavailable)));
            return;
        }

        Arrive();
        var needsConnection = exec.Caller?.Node.Archetype == Archetypes.Function && !_proxy;
        if (needsConnection)
        {
            if (_open >= _maxConnections)
            {
                ConnectionErrors++;
                respond(Outcome.Fail(ErrorKind.ConnectionLimit));
                return;
            }

            _open++;
            ConcurrencyMax = Math.Max(ConcurrencyMax, _open);
        }

        var ops = ExpandOps(exec.Call.Edge?.Config.Ops);
        RunOps(exec, ops, 0, () =>
        {
            if (needsConnection) _open--;
            RecordWrites(exec);
            respond(Outcome.Success);
        });
    }

    private void RunOps(Exec exec, IReadOnlyList<DataOpKind> ops, int index, Action done)
    {
        if (index >= ops.Count)
        {
            done();
            return;
        }

        var op = ops[index];
        var pool = op == DataOpKind.Read && _replicas.Count > 0 && !exec.Request.UseCase.Spec.ReadYourWrites
            ? _replicas[_roundRobin++ % _replicas.Count]
            : _primary;
        var milliseconds = Rng.LogNormal(op switch
        {
            DataOpKind.Read => _read,
            DataOpKind.Transaction => _transaction,
            _ => _write,
        });
        var hot = op != DataOpKind.Read && exec.Request.HotKey;
        var key = exec.Request.Key;

        void Next()
        {
            if (hot) ReleaseLock(key);
            RunOps(exec, ops, index + 1, done);
        }

        void Execute() => pool.Request(() => K.AfterMs(milliseconds, () =>
        {
            pool.Release();
            RecordService(milliseconds);
            Next();
        }));

        if (hot) AcquireLock(key, Execute);
        else Execute();
    }

    private void AcquireLock(string key, Action action)
    {
        if (_held.Add(key))
        {
            action();
            return;
        }

        if (!_lockQueues.TryGetValue(key, out var queue))
        {
            queue = new Queue<Action>();
            _lockQueues[key] = queue;
        }

        queue.Enqueue(action);
    }

    private void ReleaseLock(string key)
    {
        if (_lockQueues.TryGetValue(key, out var queue) && queue.Count > 0) K.At(K.Now, queue.Dequeue());
        else _held.Remove(key);
    }

    public override void OnZone(string zone, bool down)
    {
        if (_distributed) return;
        if (down)
        {
            _downZones.Add(zone);
            if (zone != _primaryZone || _down) return;
            _down = true;
            _downSinceUs = K.Now;
            if (Requests > 0 && !_multiAz) DataLoss = true;
            if (_multiAz && _standbyZone is { } standby && !_downZones.Contains(standby))
            {
                K.AfterMs(_failoverSeconds * 1000, () =>
                {
                    if (!_down) return;
                    (_primaryZone, _standbyZone) = (standby, _primaryZone);
                    Recover(failover: true);
                });
            }
        }
        else
        {
            _downZones.Remove(zone);
            if (_down && zone == _primaryZone) K.AfterMs(_failoverSeconds * 1000, () => Recover(failover: false));
        }
    }

    protected override (double Utilization, int Queue, int Instances) Sample() =>
        (_primary.Capacity == 0 ? 0 : Math.Min(1, _primary.Busy / (double)_primary.Capacity), _primary.Waiting, 1 + _replicas.Count);
}

/// <summary>Chave-valor/documentos: limites por partição + limite da tabela; a chave define a partição.</summary>
internal sealed class KeyValueNode : SimNode
{
    private readonly TokenBucket[] _reads;
    private readonly TokenBucket[] _writes;
    private readonly TokenBucket _table;
    private readonly Dictionary<string, TokenBucket> _perKey = new(StringComparer.Ordinal);
    private readonly double? _perKeyLimit;
    private readonly LatencyDist _read;
    private readonly LatencyDist _write;
    private readonly LatencyDist _transaction;

    public KeyValueNode(World world, Node node) : base(world, node)
    {
        var readsPerPartition = Perf.ReadsPerSecondPerPartition ?? 3000;
        var writesPerPartition = Perf.WritesPerSecondPerPartition ?? 1000;
        double peakReads = 0, peakWrites = 0;
        foreach (var edge in world.Input.Graph.Incoming(node.Id).Where(e => e.Interaction == Interaction.DataAccess))
        {
            var rate = world.PeakRates.GetValueOrDefault(edge.Id);
            foreach (var op in edge.Config.Ops ?? [])
            {
                if (op.IsWrite) peakWrites += rate * op.Count;
                else peakReads += rate * op.Count;
            }
        }

        var automatic = Math.Max(4, Math.Max(
            (int)Math.Ceiling(peakWrites / (writesPerPartition * 0.5)),
            (int)Math.Ceiling(peakReads / (readsPerPartition * 0.5))));
        Partitions = Cfg.String("partitionKeyCardinality") switch
        {
            "baixa" => 1,
            "media" => Math.Min(automatic, 4),
            _ => automatic,
        };

        _reads = Enumerable.Range(0, Partitions).Select(_ => new TokenBucket(readsPerPartition, readsPerPartition)).ToArray();
        _writes = Enumerable.Range(0, Partitions).Select(_ => new TokenBucket(writesPerPartition, writesPerPartition)).ToArray();
        var tableLimit = Perf.MaxRps ?? 40000;
        _table = new TokenBucket(tableLimit, tableLimit);
        _perKeyLimit = Perf.WritesPerSecondPerKey;
        _read = Perf.ReadLatency ?? new LatencyDist(4, 15);
        _write = Perf.WriteLatency ?? new LatencyDist(6, 22);
        _transaction = Perf.TransactionLatency ?? new LatencyDist(14, 45);
    }

    public int Partitions { get; }

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        if (!base.CanAccept())
        {
            respond(Unavailable());
            return;
        }

        Arrive();
        var ops = ExpandOps(exec.Call.Edge?.Config.Ops);
        var request = exec.Request;
        var partition = request.HotKey
            ? (int)(Rng.Fnv1a(request.Key) % (ulong)Partitions)
            : Rng.NextInt(Partitions);
        var latency = 0.0;
        foreach (var op in ops)
        {
            var bucket = op == DataOpKind.Read ? _reads[partition] : _writes[partition];
            if (!_table.TryTake(K.Now, ThrottleFactor) || !bucket.TryTake(K.Now, ThrottleFactor))
            {
                respond(Throttle());
                return;
            }

            if (op != DataOpKind.Read && request.HotKey && _perKeyLimit is { } limit)
            {
                if (!_perKey.TryGetValue(request.Key, out var keyBucket))
                {
                    keyBucket = new TokenBucket(limit, Math.Max(1, limit));
                    _perKey[request.Key] = keyBucket;
                }

                if (!keyBucket.TryTake(K.Now))
                {
                    respond(Throttle());
                    return;
                }
            }

            latency += Rng.LogNormal(op switch
            {
                DataOpKind.Read => _read,
                DataOpKind.Transaction => _transaction,
                _ => _write,
            });
        }

        latency += InjectedLatencyMs();
        RecordService(latency);
        K.AfterMs(latency, () =>
        {
            RecordWrites(exec);
            respond(Outcome.Success);
        });
    }
}

/// <summary>Cache: acerto limitado pela cacheabilidade do workload; quando cai, todas as leituras vão à origem.</summary>
internal sealed class CacheNode : SimNode
{
    private const double WarmupSeconds = 60;
    private const double FailoverSeconds = 10;
    private readonly TokenBucket _bucket;
    private readonly bool _resilient;
    private readonly LatencyDist _read;
    private readonly LatencyDist _write;
    private readonly HashSet<string> _downZones = new(StringComparer.Ordinal);
    private double _unavailableFraction;
    private double _coldFraction;
    private long _recoveredAt = -1;

    public CacheNode(World world, Node node) : base(world, node)
    {
        var nodes = Math.Max(1, Cfg.Int("nodes", 1));
        _resilient = Cfg.Int("replicas", 0) > 0 && Cfg.Bool("multiAz") && node.Placement.Zones.Count > 1;
        var rate = (Perf.MaxRps ?? 100_000) * nodes;
        _bucket = new TokenBucket(rate, rate);
        _read = Perf.ReadLatency ?? new LatencyDist(0.5, 2);
        _write = Perf.WriteLatency ?? new LatencyDist(0.6, 2.5);
    }

    public override void Handle(Exec exec, Action<Outcome> respond)
    {
        // Os shards com primário na zona que caiu ficam indisponíveis (o resto do cluster segue atendendo).
        if (!base.CanAccept() || (_unavailableFraction > 0 && Rng.Chance(_unavailableFraction)))
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

        var ops = exec.Call.Edge?.Config.Ops;
        var isRead = ops?.Any(o => o.Op == DataOpKind.Read) ?? true;
        var milliseconds = Rng.LogNormal(isRead ? _read : _write);
        var hit = isRead && Rng.Chance(HitRatio(exec));
        RecordService(milliseconds);
        K.AfterMs(milliseconds, () => respond(new Outcome(true, CacheHit: hit)));
    }

    private double HitRatio(Exec exec)
    {
        var ratio = exec.Request.UseCase.Cacheability;
        if (_recoveredAt < 0 || _coldFraction <= 0) return ratio;
        var warmth = Math.Min(1, (K.Now - _recoveredAt) / 1_000_000.0 / WarmupSeconds);
        return ratio * (1 - (_coldFraction * (1 - warmth)));
    }

    /// <summary>
    /// Cluster com shards espalhados pelas zonas: a queda de uma zona atinge 1/N dos dados. Com réplica em outra zona,
    /// esses shards fazem failover em ~10 s e voltam quentes; sem réplica, ficam fora até a zona voltar e voltam frios.
    /// </summary>
    public override void OnZone(string zone, bool down)
    {
        var zones = Node.Placement.Zones;
        if (!zones.Contains(zone)) return;
        var share = zones.Count <= 1 ? 1.0 : 1.0 / zones.Count;
        if (down)
        {
            if (!_downZones.Add(zone)) return;
            _unavailableFraction = Math.Min(1, _unavailableFraction + share);
            if (_resilient)
            {
                K.AfterMs(FailoverSeconds * 1000, () =>
                {
                    if (_downZones.Remove(zone)) _unavailableFraction = Math.Max(0, _unavailableFraction - share);
                });
            }
        }
        else if (!_resilient && _downZones.Remove(zone))
        {
            _unavailableFraction = Math.Max(0, _unavailableFraction - share);
            _coldFraction = Math.Min(1, _coldFraction + share);
            _recoveredAt = K.Now;
        }
    }
}

/// <summary>Armazenamento de objetos: latência alta, capacidade enorme.</summary>
internal sealed class ObjectStorageNode : SimNode
{
    private readonly TokenBucket _bucket;

    public ObjectStorageNode(World world, Node node) : base(world, node)
    {
        var rate = Perf.MaxRps ?? 50_000;
        _bucket = new TokenBucket(rate, rate);
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

        var ops = ExpandOps(exec.Call.Edge?.Config.Ops);
        var latency = ops.Sum(op => Rng.LogNormal(op == DataOpKind.Read
            ? Perf.ReadLatency ?? new LatencyDist(20, 80)
            : Perf.WriteLatency ?? new LatencyDist(30, 120)));
        RecordService(latency);
        K.AfterMs(latency, () =>
        {
            RecordWrites(exec);
            respond(Outcome.Success);
        });
    }
}
