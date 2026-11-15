using Lab.Simulation.Core;

namespace Lab.Engine.Tests.Simulation;

/// <summary>Testes "golden": o núcleo de eventos + pool de servidores reproduz a teoria de filas.</summary>
public sealed class QueueingTheoryTests
{
    private sealed record Measured(double MeanWaitMs, double MeanQueueLength, double ArrivalRate, double Utilization);

    private static Measured RunMmc(double lambda, double mu, int servers, double seconds, long seed)
    {
        var kernel = new SimKernel();
        var pool = new ServerPool(kernel, servers);
        var arrivals = Rng.For(seed, "arrivals");
        var service = Rng.For(seed, "service");
        var endUs = (long)(seconds * 1_000_000);
        long arrived = 0;

        void Arrive()
        {
            arrived++;
            pool.Request(() => kernel.AfterUs((long)(service.Exponential(mu) * 1_000_000), pool.Release));
            var next = (long)(arrivals.Exponential(lambda) * 1_000_000);
            if (kernel.Now + next < endUs) kernel.AfterUs(next, Arrive);
        }

        kernel.AfterUs(0, Arrive);
        kernel.RunUntil(endUs);
        var (busy, queue, capacity) = pool.Integrals();
        return new Measured(
            pool.TotalWaitUs / pool.Granted / 1000,
            queue / endUs,
            arrived / seconds,
            busy / capacity);
    }

    /// <summary>Probabilidade de espera de Erlang C.</summary>
    private static double ErlangC(int c, double offered)
    {
        var sum = 0.0;
        var term = 1.0;
        for (var k = 0; k < c; k++)
        {
            if (k > 0) term *= offered / k;
            sum += term;
        }

        var last = term * offered / c;
        var rho = offered / c;
        var top = last / (1 - rho);
        return top / (sum + top);
    }

    private static double ExpectedWaitMs(double lambda, double mu, int c)
    {
        var offered = lambda / mu;
        return ErlangC(c, offered) / ((c * mu) - lambda) * 1000;
    }

    [Theory]
    [InlineData(50, 100, 1)]   // M/M/1 ρ = 0,5
    [InlineData(80, 100, 1)]   // M/M/1 ρ = 0,8
    [InlineData(320, 100, 4)]  // M/M/4 ρ = 0,8
    [InlineData(1500, 100, 16)] // M/M/16 ρ ≈ 0,94
    public void Mean_wait_matches_erlang_c(double lambda, double mu, int servers)
    {
        var measured = RunMmc(lambda, mu, servers, seconds: 4000, seed: 42);
        var expected = ExpectedWaitMs(lambda, mu, servers);

        measured.MeanWaitMs.ShouldBe(expected, expected * 0.06);
        measured.Utilization.ShouldBe(lambda / (mu * servers), 0.01);
    }

    [Fact]
    public void Littles_law_holds_for_the_queue()
    {
        var measured = RunMmc(lambda: 80, mu: 100, servers: 1, seconds: 4000, seed: 7);

        // L = λ·W (fila): comprimento médio da fila = taxa de chegada × espera média.
        measured.MeanQueueLength.ShouldBe(measured.ArrivalRate * measured.MeanWaitMs / 1000, 0.05);
    }

    [Fact]
    public void Same_seed_same_stream_different_names_different_streams()
    {
        var a = Rng.For(1, "x");
        var b = Rng.For(1, "x");
        var c = Rng.For(1, "y");

        var first = Enumerable.Range(0, 5).Select(_ => a.NextULong()).ToList();
        Enumerable.Range(0, 5).Select(_ => b.NextULong()).ShouldBe(first);
        Enumerable.Range(0, 5).Select(_ => c.NextULong()).ShouldNotBe(first);
    }

    [Fact]
    public void Lognormal_matches_its_p50_and_p99()
    {
        var rng = Rng.For(3, "lognormal");
        var samples = Enumerable.Range(0, 200_000).Select(_ => rng.LogNormal(100, 400)).Order().ToList();

        samples[samples.Count / 2].ShouldBe(100, 3);
        samples[(int)(samples.Count * 0.99)].ShouldBe(400, 25);
    }
}
