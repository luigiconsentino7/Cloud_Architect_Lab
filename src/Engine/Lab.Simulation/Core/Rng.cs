using Lab.Ir.Catalog;

namespace Lab.Simulation.Core;

/// <summary>
/// xoshiro256** com semente derivada por SplitMix64. Não usamos <see cref="System.Random"/> para que a sequência
/// não mude entre versões do .NET. Cada finalidade tem seu próprio fluxo (<see cref="For"/>), derivado da
/// semente mestra por um hash estável — nunca <c>string.GetHashCode()</c>, que é randomizado por processo.
/// </summary>
public sealed class Rng
{
    private const double Z99 = 2.3263478740408408;
    private ulong _s0, _s1, _s2, _s3;
    private double _spare;
    private bool _hasSpare;

    public Rng(ulong seed)
    {
        var x = seed;
        _s0 = SplitMix(ref x);
        _s1 = SplitMix(ref x);
        _s2 = SplitMix(ref x);
        _s3 = SplitMix(ref x);
    }

    public static Rng For(long masterSeed, string stream) => new(Mix(unchecked((ulong)masterSeed) ^ Fnv1a(stream)));

    public static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash ^= c;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    public ulong NextULong()
    {
        var result = RotL(_s1 * 5, 7) * 9;
        var t = _s1 << 17;
        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = RotL(_s3, 45);
        return result;
    }

    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0);

    public int NextInt(int maxExclusive) => maxExclusive <= 1 ? 0 : (int)(NextDouble() * maxExclusive);

    public bool Chance(double probability) => probability > 0 && (probability >= 1 || NextDouble() < probability);

    public double Uniform(double min, double max) => min + ((max - min) * NextDouble());

    public double Exponential(double rate) => -Math.Log(1.0 - NextDouble()) / rate;

    public double Normal()
    {
        if (_hasSpare)
        {
            _hasSpare = false;
            return _spare;
        }

        double u, v, s;
        do
        {
            u = (NextDouble() * 2) - 1;
            v = (NextDouble() * 2) - 1;
            s = (u * u) + (v * v);
        }
        while (s >= 1 || s == 0);

        var factor = Math.Sqrt(-2 * Math.Log(s) / s);
        _spare = v * factor;
        _hasSpare = true;
        return u * factor;
    }

    /// <summary>Amostra log-normal definida por p50 e p99.</summary>
    public double LogNormal(double p50, double p99) =>
        p99 <= p50 || p50 <= 0 ? Math.Max(0, p50) : p50 * Math.Exp(Sigma(p50, p99) * Normal());

    public double LogNormal(LatencyDist distribution) => LogNormal(distribution.P50, distribution.P99);

    public static double Sigma(double p50, double p99) => p99 <= p50 || p50 <= 0 ? 0 : Math.Log(p99 / p50) / Z99;

    public static double Mean(LatencyDist distribution)
    {
        var sigma = Sigma(distribution.P50, distribution.P99);
        return distribution.P50 * Math.Exp(sigma * sigma / 2);
    }

    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    private static ulong SplitMix(ref ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        return Mix(x);
    }

    private static ulong RotL(ulong x, int k) => (x << k) | (x >> (64 - k));
}
