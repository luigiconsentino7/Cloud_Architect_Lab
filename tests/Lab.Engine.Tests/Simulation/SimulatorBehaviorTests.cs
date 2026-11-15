using Lab.Engine.Tests.Support;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Simulation;

namespace Lab.Engine.Tests.Simulation;

/// <summary>
/// Os comportamentos do exemplo ponta a ponta da spec: abismo de concorrência da Lambda no pico, notificações
/// duplicadas sem idempotência e circuit breaker + fallback protegendo do SPI lento.
/// </summary>
public sealed class SimulatorBehaviorTests(ITestOutputHelper output)
{
    private static readonly ChallengeSpec Challenge = Pix.Challenge();
    private static readonly Lazy<ArchitectureGraph> GoodGraph = new(() => Pix.Compile(Pix.Good(), Challenge).Graph);
    private static readonly Lazy<ArchitectureGraph> BadGraph = new(() => Pix.Compile(Pix.Bad(), Challenge).Graph);

    private ScenarioResult Run(ArchitectureGraph graph, string scenario)
    {
        var report = Simulator.Run(new SimulationRequest
        {
            Graph = graph,
            Challenge = Challenge,
            Catalog = TestContent.Get().Catalog,
            ScenarioIds = [scenario],
            IncludeSeries = false,
        });
        var result = report.Scenarios.Single();
        var uc1 = result.UseCases["UC-1"];
        output.WriteLine($"{scenario}: {result.Events:N0} eventos em {result.WallTimeMs:N0} ms ({result.Events / Math.Max(1, result.WallTimeMs) * 1000:N0}/s)");
        output.WriteLine($"  UC-1: {uc1.Requests:N0} req, sucesso {uc1.SuccessRate:P3}, p99 {uc1.Latency.P99} ms, erros {string.Join(", ", uc1.Errors.Select(e => $"{e.Key}={e.Value}"))}");
        output.WriteLine($"  integridade: {result.Integrity}");
        foreach (var bottleneck in result.Bottlenecks) output.WriteLine($"  gargalo: {bottleneck.Detail}");
        return result;
    }

    [Fact]
    public void Solid_design_meets_the_slos_at_baseline()
    {
        var result = Run(GoodGraph.Value, "baseline");

        result.UseCases["UC-1"].SuccessRate.ShouldBeGreaterThanOrEqualTo(0.9995);
        result.UseCases["UC-1"].Latency.P99.ShouldBeLessThanOrEqualTo(1000);
        result.UseCases["UC-1"].AsyncCompletion!.P95.ShouldBeLessThanOrEqualTo(5000);
        result.UseCases["UC-2"].Latency.P99.ShouldBeLessThanOrEqualTo(200);
    }

    [Fact]
    public void Lambda_hits_the_concurrency_cliff_at_the_payday_peak()
    {
        var bad = Run(BadGraph.Value, "payday-peak");

        bad.UseCases["UC-1"].SuccessRate.ShouldBeLessThan(0.97);
        bad.Nodes["sender"].Throttled.ShouldBeGreaterThan(0);
        bad.Bottlenecks.ShouldContain(b => b.NodeId == "sender" && b.Reason == "concurrency-limit");
        bad.Facts.ShouldContain("throttled");
    }

    [Fact]
    public void Solid_design_survives_the_payday_peak()
    {
        var good = Run(GoodGraph.Value, "payday-peak");

        good.UseCases["UC-1"].SuccessRate.ShouldBeGreaterThanOrEqualTo(0.999);
        good.UseCases["UC-1"].Latency.P99.ShouldBeLessThanOrEqualTo(1000);
    }

    [Fact]
    public void Duplicate_deliveries_become_duplicate_notifications_without_idempotency()
    {
        var bad = Run(BadGraph.Value, "duplicates");
        var good = Run(GoodGraph.Value, "duplicates");

        bad.Integrity.DuplicateEffects.ShouldBeGreaterThan(0);
        bad.Integrity.MonetaryDuplicates.ShouldBe(0); // o débito tem escrita condicional + chave
        good.Integrity.DuplicateEffects.ShouldBe(0);
    }

    [Fact]
    public void Circuit_breaker_and_fallback_protect_from_a_slow_dependency()
    {
        var bad = Run(BadGraph.Value, "spi-slow");
        var good = Run(GoodGraph.Value, "spi-slow");

        good.UseCases["UC-1"].SuccessRate.ShouldBeGreaterThanOrEqualTo(0.99);
        bad.UseCases["UC-1"].SuccessRate.ShouldBeLessThan(good.UseCases["UC-1"].SuccessRate);
    }

    [Fact]
    public void Simulation_is_deterministic()
    {
        var first = Run(BadGraph.Value, "baseline") with { WallTimeMs = 0 };
        var second = Run(BadGraph.Value, "baseline") with { WallTimeMs = 0 };

        Canonical.Serialize(second).ShouldBe(Canonical.Serialize(first));
    }

    [Fact]
    public void Missing_flow_fails_every_request_of_the_use_case()
    {
        var design = Pix.Good() with { Flows = [Pix.Good().Flows[0]] };
        var graph = Pix.Compile(design, Challenge).Graph;

        var result = Run(graph, "baseline");

        result.UseCases["UC-2"].SuccessRate.ShouldBe(0);
        result.UseCases["UC-2"].Errors.ShouldContainKey("no-path");
    }
}
