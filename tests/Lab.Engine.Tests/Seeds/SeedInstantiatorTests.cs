using System.Text.Json.Nodes;
using Lab.Engine.Tests.Support;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Scene;
using Lab.Ir.Seeds;
using Lab.Pipeline.Seeds;

namespace Lab.Engine.Tests.Seeds;

public sealed class SeedInstantiatorTests
{
    private static Seed PixSeed => TestContent.Get().Seeds.Single(s => s.Id == "pix/envio-pix");

    private static Instantiation Instantiate(CloudProvider provider = CloudProvider.Aws, Difficulty difficulty = Difficulty.Senior,
        IReadOnlyDictionary<string, double>? variables = null, IReadOnlyList<string>? twists = null, long? variation = null) =>
        SeedInstantiator.Instantiate(new InstantiationRequest
        {
            Seed = PixSeed,
            Provider = provider,
            Difficulty = difficulty,
            Variables = variables,
            Twists = twists ?? [],
            VariationSeed = variation,
        }, TestContent.Get().Catalog);

    [Fact]
    public void Renders_templates_units_and_variables()
    {
        var challenge = Instantiate(variables: new Dictionary<string, double> { ["baselineRps"] = 1200 }).Challenge;

        challenge.SeedId.ShouldBe("pix/envio-pix");
        challenge.Workload.BaselineRps.ShouldBe(1200);
        challenge.Narrative.ShouldContain("1.200 PIX por segundo");
        challenge.TestPlan.Find("payday-peak")!.Load.ToRps.ShouldBe(6000);
        challenge.TestPlan.Find("baseline")!.Load.Duration.ShouldBe(40);           // 40s → segundos
        challenge.TestPlan.Slos.Single(s => s.Id == "SLO-1").Max.ShouldBe(1000);   // 1000ms → ms
        challenge.TestPlan.Slos.Single(s => s.Id == "SLO-2").Min.ShouldBe(0.9995); // 99.95% → fração
        challenge.TestPlan.Slos.Single(s => s.Id == "SLO-3").Max.ShouldBe(5000);   // 5s → ms
        challenge.ExternalSystems[0].Latency.P99.ShouldBe(800);
        challenge.Constraints.BudgetUsdMonth.ShouldBe(48_200);
        challenge.Variables["baselineRps"].ShouldBe(1200);
    }

    [Theory]
    [InlineData(CloudProvider.Aws, "aws.ecs-fargate")]
    [InlineData(CloudProvider.Azure, null)]
    [InlineData(CloudProvider.Gcp, null)]
    public void Reference_is_compiled_in_the_chosen_provider(CloudProvider provider, string? serviceComponent)
    {
        var instance = Instantiate(provider);
        var reference = instance.Challenge.Reference!;

        instance.ReferenceDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        reference.Provider.ShouldBe(provider);
        var catalog = TestContent.Get().Catalog;
        reference.Nodes.Where(n => !Archetypes.IsClient(n.Archetype) && !Archetypes.IsExternal(n.Archetype))
            .ShouldAllBe(n => catalog.Get(n.CatalogId).Provider == provider);
        if (serviceComponent is not null) reference.GetNode("svc").CatalogId.ShouldBe(serviceComponent);
        reference.GetNode("svc").Cfg.Int("instances.min", 0).ShouldBe(16); // ceil(1000 / 65)
        reference.FlowFor("UC-1")!.Steps.Count.ShouldBe(10);
        reference.FindNode("external:spi").ShouldNotBeNull();
    }

    [Fact]
    public void Twists_change_the_challenge_and_the_reference()
    {
        var challenge = Instantiate(twists: ["spi-degradado", "auditoria-obrigatoria"]).Challenge;

        challenge.Twists.ShouldBe(["spi-degradado", "auditoria-obrigatoria"]);
        challenge.FindExternal("external:spi")!.Latency.P50.ShouldBe(400);
        challenge.TestPlan.Find("spi-outage")!.Faults.Single().Type.ShouldBe(FaultType.DependencyOutage);
        challenge.TestPlan.Slos.Single(s => s.Id == "SLO-2").Scenarios.ShouldContain("spi-outage");
        challenge.Constraints.RequireAuditLog.ShouldBeTrue();
        challenge.Reference!.FlowFor("UC-1")!.Steps.ShouldContain("e13");
    }

    [Fact]
    public void Rejects_values_outside_the_difficulty_range_and_unknown_twists()
    {
        Should.Throw<SeedException>(() => Instantiate(difficulty: Difficulty.Pleno, variables: new Dictionary<string, double> { ["baselineRps"] = 5000 }))
            .Message.ShouldContain("fora da faixa");
        Should.Throw<SeedException>(() => Instantiate(twists: ["nao-existe"])).Message.ShouldContain("desconhecida");
        Should.Throw<SeedException>(() => Instantiate(difficulty: Difficulty.Pleno, twists: ["spi-degradado"])).Message.ShouldContain("exige");
        Should.Throw<SeedException>(() => Instantiate(difficulty: Difficulty.Junior)).Message.ShouldContain("dificuldade");
    }

    [Fact]
    public void Variation_seed_samples_inside_the_range_deterministically()
    {
        var first = Instantiate(difficulty: Difficulty.Staff, variation: 42).Challenge;
        var again = Instantiate(difficulty: Difficulty.Staff, variation: 42).Challenge;
        var other = Instantiate(difficulty: Difficulty.Staff, variation: 7).Challenge;

        first.Workload.BaselineRps.ShouldBeInRange(1800, 2500);
        (first.Workload.BaselineRps % 50).ShouldBe(0);
        again.Workload.BaselineRps.ShouldBe(first.Workload.BaselineRps);
        again.MasterSeed.ShouldBe(first.MasterSeed);
        other.MasterSeed.ShouldNotBe(first.MasterSeed);
    }

    [Fact]
    public void Twist_paths_support_id_selectors_and_appends()
    {
        var body = JsonNode.Parse("""{ "items": [ { "id": "a", "v": 1 }, { "id": "b", "v": 2 } ], "list": [] }""")!.AsObject();

        SeedInstantiator.Apply(body, new TwistOp { Op = "merge", Path = "items[b]", Value = JsonNode.Parse("""{ "v": 3, "w": true }""") }, "t");
        SeedInstantiator.Apply(body, new TwistOp { Op = "append", Path = "list", Value = JsonValue.Create("x") }, "t");
        SeedInstantiator.Apply(body, new TwistOp { Op = "set", Path = "nested.value", Value = JsonValue.Create(5) }, "t");

        body["items"]![1]!["v"]!.GetValue<int>().ShouldBe(3);
        body["items"]![1]!["w"]!.GetValue<bool>().ShouldBeTrue();
        body["list"]!.AsArray().Count.ShouldBe(1);
        body["nested"]!["value"]!.GetValue<int>().ShouldBe(5);
        Should.Throw<SeedException>(() => SeedInstantiator.Apply(body, new TwistOp { Op = "merge", Path = "items[z]", Value = new JsonObject() }, "t"));
    }

    [Fact]
    public void Unit_normalizer_rejects_thousands_separators()
    {
        var errors = new List<string>();
        UnitNormalizer.Normalize(JsonNode.Parse("""{ "max": "1.500ms", "duration": "2min", "min": "99.9%" }"""), errors)!
            .AsObject()["duration"]!.GetValue<double>().ShouldBe(120);

        errors.Single().ShouldContain("separador de milhar");
    }
}
