using Lab.Ir.Challenges;

namespace Lab.Simulation;

/// <summary>Taxa de chegada λ(t) de um cenário, em req/s. Tempos em segundos desde o início (aquecimento incluso).</summary>
public sealed class LoadCurve
{
    private readonly Func<double, double> _rate;

    private LoadCurve(double warmup, double total, double peak, Func<double, double> rate)
    {
        Warmup = warmup;
        Total = total;
        Peak = peak;
        _rate = rate;
    }

    public double Warmup { get; }

    public double Total { get; }

    public double Peak { get; }

    public double Rate(double seconds) => seconds < 0 || seconds > Total ? 0 : _rate(seconds);

    /// <summary>Número médio de requisições do cenário (para o orçamento de eventos).</summary>
    public double ExpectedRequests()
    {
        var sum = 0.0;
        for (var t = 0.0; t < Total; t += 0.5) sum += Rate(t) * 0.5;
        return sum;
    }

    public static LoadCurve From(ScenarioSpec scenario, TestPlan plan, WorkloadSpec workload, double compression = 1)
    {
        var load = scenario.Load;
        if (load.Ref is { } reference && plan.Find(reference) is { } referenced && referenced.Id != scenario.Id)
            load = Merge(referenced.Load, load);

        var warmup = load.Warmup ?? 10;
        var baseline = workload.BaselineRps;
        double Scale(double seconds) => Math.Max(20, seconds * compression);

        switch (load.Profile)
        {
            case LoadShape.Ramp:
                {
                    var from = load.FromRps ?? baseline;
                    var to = load.ToRps ?? from;
                    var ramp = Scale(load.RampFor ?? 30);
                    var hold = Scale(load.HoldFor ?? 60);
                    return new LoadCurve(warmup, warmup + ramp + hold, Math.Max(from, to), t =>
                        t < warmup ? from : t < warmup + ramp ? from + ((to - from) * (t - warmup) / ramp) : to);
                }

            case LoadShape.Spike:
                {
                    var @base = load.BaseRps ?? baseline;
                    var peak = load.PeakRps ?? (@base * 10);
                    var at = load.At ?? (warmup + 30);
                    var length = load.For ?? 20;
                    var total = Math.Max(at + length + 20, warmup + Scale((load.Duration ?? 120) - warmup));
                    return new LoadCurve(warmup, total, Math.Max(@base, peak), t => t >= at && t < at + length ? peak : @base);
                }

            default:
                {
                    var rps = load.Rps ?? baseline;
                    var duration = Scale(load.Duration ?? 60);
                    return new LoadCurve(warmup, warmup + duration, rps, _ => rps);
                }
        }
    }

    private static LoadProfile Merge(LoadProfile basis, LoadProfile over) => basis with
    {
        Duration = over.Duration ?? basis.Duration,
        Warmup = over.Warmup ?? basis.Warmup,
        Rps = over.Rps ?? basis.Rps,
        Ref = null,
    };
}
