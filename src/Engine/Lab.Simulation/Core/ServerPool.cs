namespace Lab.Simulation.Core;

/// <summary>
/// c servidores com fila FIFO. Acumula as integrais de ocupação e de fila no tempo para medir utilização e
/// verificar a lei de Little (L = λW) nos testes.
/// </summary>
public sealed class ServerPool
{
    private readonly SimKernel _kernel;
    private readonly Queue<(Action Grant, long EnqueuedAt)> _waiting = new();
    private long _lastChange;
    private double _busyIntegral;
    private double _queueIntegral;
    private double _capacityIntegral;

    public ServerPool(SimKernel kernel, int capacity, int maxQueue = int.MaxValue)
    {
        _kernel = kernel;
        Capacity = Math.Max(0, capacity);
        MaxQueue = maxQueue;
        _lastChange = kernel.Now;
    }

    public int Capacity { get; private set; }

    public int Busy { get; private set; }

    public int Waiting => _waiting.Count;

    public int MaxQueue { get; set; }

    public int MaxWaitingObserved { get; private set; }

    public long Granted { get; private set; }

    public double TotalWaitUs { get; private set; }

    public bool HasFreeServer => Busy < Capacity && _waiting.Count == 0;

    /// <summary>Pede um servidor. Devolve falso se a fila estiver cheia (o chamador rejeita a requisição).</summary>
    public bool Request(Action grant)
    {
        Accumulate();
        if (Busy < Capacity && _waiting.Count == 0)
        {
            Busy++;
            Granted++;
            grant();
            return true;
        }

        if (_waiting.Count >= MaxQueue) return false;
        _waiting.Enqueue((grant, _kernel.Now));
        if (_waiting.Count > MaxWaitingObserved) MaxWaitingObserved = _waiting.Count;
        return true;
    }

    public void Release()
    {
        Accumulate();
        Busy = Math.Max(0, Busy - 1);
        Pump();
    }

    public void SetCapacity(int capacity)
    {
        Accumulate();
        Capacity = Math.Max(0, capacity);
        Pump();
    }

    /// <summary>Integrais acumuladas (µs × servidores) até agora: ocupação, fila e capacidade.</summary>
    public (double Busy, double Queue, double Capacity) Integrals()
    {
        Accumulate();
        return (_busyIntegral, _queueIntegral, _capacityIntegral);
    }

    private void Pump()
    {
        while (Busy < Capacity && _waiting.Count > 0)
        {
            var (grant, enqueuedAt) = _waiting.Dequeue();
            Busy++;
            Granted++;
            TotalWaitUs += _kernel.Now - enqueuedAt;
            _kernel.At(_kernel.Now, grant);
        }
    }

    private void Accumulate()
    {
        var elapsed = _kernel.Now - _lastChange;
        if (elapsed <= 0) return;
        _busyIntegral += Busy * (double)elapsed;
        _queueIntegral += _waiting.Count * (double)elapsed;
        _capacityIntegral += Capacity * (double)elapsed;
        _lastChange = _kernel.Now;
    }
}

/// <summary>Limitador de vazão (token bucket).</summary>
public sealed class TokenBucket
{
    private double _tokens;
    private long _last;

    public TokenBucket(double ratePerSecond, double burst)
    {
        Rate = ratePerSecond;
        Burst = Math.Max(1, burst);
        _tokens = Burst;
    }

    public double Rate { get; set; }

    public double Burst { get; }

    public bool TryTake(long nowUs, double factor = 1)
    {
        if (double.IsPositiveInfinity(Rate)) return true;
        _tokens = Math.Min(Burst, _tokens + ((nowUs - _last) / 1_000_000.0 * Rate * factor));
        _last = nowUs;
        if (_tokens < 1) return false;
        _tokens -= 1;
        return true;
    }
}

/// <summary>Histograma log-linear (8 sub-baldes por potência de 2, ~4% de erro), em microssegundos.</summary>
public sealed class LatencyHistogram
{
    private readonly int _subBuckets;
    private readonly int _buckets;
    private readonly long[] _counts;

    /// <param name="subBuckets">Sub-baldes por potência de 2: 16 ≈ 2% de erro; 4 ≈ 9% (séries por segundo).</param>
    public LatencyHistogram(int subBuckets = 16)
    {
        _subBuckets = subBuckets;
        _buckets = 36 * subBuckets;
        _counts = new long[_buckets + 1];
    }

    public long Count { get; private set; }

    public double SumUs { get; private set; }

    public double MaxUs { get; private set; }

    public double MeanMs => Count == 0 ? 0 : SumUs / Count / 1000;

    public void Record(double microseconds)
    {
        var value = Math.Max(0, microseconds);
        _counts[Index(value)]++;
        Count++;
        SumUs += value;
        if (value > MaxUs) MaxUs = value;
    }

    public double PercentileMs(double quantile)
    {
        if (Count == 0) return 0;
        var target = Math.Max(1, (long)Math.Ceiling(quantile * Count));
        long cumulative = 0;
        for (var i = 0; i <= _buckets; i++)
        {
            cumulative += _counts[i];
            if (cumulative >= target) return Math.Min(Value(i), MaxUs) / 1000;
        }

        return MaxUs / 1000;
    }

    public void Merge(LatencyHistogram other)
    {
        if (other._subBuckets != _subBuckets) throw new InvalidOperationException("Histogramas com resoluções diferentes.");
        for (var i = 0; i <= _buckets; i++) _counts[i] += other._counts[i];
        Count += other.Count;
        SumUs += other.SumUs;
        MaxUs = Math.Max(MaxUs, other.MaxUs);
    }

    private int Index(double microseconds) =>
        microseconds <= 1 ? 0 : Math.Min(_buckets, (int)(Math.Log2(microseconds) * _subBuckets));

    private double Value(int index) => Math.Pow(2, (index + 0.5) / _subBuckets);
}
