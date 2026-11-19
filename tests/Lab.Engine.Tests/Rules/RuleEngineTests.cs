using Lab.Engine.Tests.Support;
using Lab.Ir;
using Lab.Ir.Scene;
using Lab.Rules;

namespace Lab.Engine.Tests.Rules;

public sealed class RuleEngineTests
{
    private static IReadOnlyList<Finding> Run(Design design)
    {
        var challenge = Pix.Challenge();
        var compiled = Pix.Compile(design, challenge);
        compiled.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
        return RuleEngine.Run(new RuleContext(compiled.Graph, challenge, TestContent.Get().Catalog));
    }

    [Fact]
    public void Solid_design_has_no_critical_or_high_findings()
    {
        var findings = Run(Pix.Good());

        findings.Where(f => f.Severity is Severity.Critical or Severity.High)
            .Select(f => $"{f.RuleId}: {f.Message}")
            .ShouldBeEmpty();
    }

    [Fact]
    public void Classic_mistakes_are_all_caught()
    {
        var findings = Run(Pix.Bad());
        var ids = findings.Select(f => f.RuleId).ToHashSet();

        ids.ShouldContain("DAT-001"); // grava e publica sem outbox
        ids.ShouldContain("DAT-002"); // notificador sem idempotência
        ids.ShouldContain("ARQ-005"); // SPI sem circuit breaker nem fallback
        ids.ShouldContain("ARQ-003"); // retry sem jitter
        ids.ShouldContain("ARQ-002"); // timeout do SPI maior que o SLO
        ids.ShouldContain("SEC-003"); // sem WAF
        ids.ShouldContain("ARQ-004"); // SNS sem DLQ
        ids.ShouldNotContain("DAT-003"); // a chave de idempotência existe
    }

    [Fact]
    public void Dual_write_in_a_monetary_flow_is_critical()
    {
        var finding = Run(Pix.Bad()).Single(f => f.RuleId == "DAT-001");

        finding.Severity.ShouldBe(Severity.Critical);
        finding.NodeIds.ShouldBe(["sender"]);
        finding.EdgeIds.ShouldBe(["b03", "b05"]);
    }

    [Fact]
    public void Duplicate_notifications_are_medium_not_critical()
    {
        Run(Pix.Bad()).Single(f => f.RuleId == "DAT-002").Severity.ShouldBe(Severity.Medium);
    }

    [Fact]
    public void Missing_idempotency_key_on_monetary_command_is_critical()
    {
        var design = Pix.Bad();
        var scene = design.Scene.DeepClone().AsObject();
        foreach (var element in scene["elements"]!.AsArray())
        {
            if (element!["customData"]?["connectionId"]?.GetValue<string>() == "b01")
                element["customData"]!["config"] = new System.Text.Json.Nodes.JsonObject();
        }

        Run(design with { Scene = scene }).ShouldContain(f => f.RuleId == "DAT-003" && f.Severity == Severity.Critical);
    }

    [Fact]
    public void Single_az_service_is_a_single_point_of_failure()
    {
        var s = new SceneBuilder();
        var app = s.Component("client.mobile", "App");
        var svc = s.Component("aws.ecs-fargate", "api");
        var db = s.Component("aws.aurora-postgresql", "db", config: new { multiAz = true });
        var steps = new[] { s.Arrow(app, svc, id: "x1"), s.Arrow(svc, db, id: "x2") };
        var challenge = Pix.Challenge();
        var compiled = SceneCompiler.Compile(new SceneCompileRequest
        {
            Scene = s.Build(),
            Flows = [new FlowDefinition("UC-2", steps)],
            Provider = CloudProvider.Aws,
            Catalog = TestContent.Get().Catalog,
            Challenge = challenge,
        });

        var findings = RuleEngine.Run(new RuleContext(compiled.Graph, challenge, TestContent.Get().Catalog));

        findings.ShouldContain(f => f.RuleId == "ARQ-001" && f.NodeIds.Contains(svc));
        findings.ShouldNotContain(f => f.RuleId == "ARQ-001" && f.NodeIds.Contains(db));
    }

    [Fact]
    public void Low_cardinality_partition_key_with_writes_is_flagged()
    {
        var s = new SceneBuilder();
        var app = s.Component("client.mobile", "App");
        var svc = s.Component("aws.ecs-fargate", "api");
        var table = s.Component("aws.dynamodb", "transacoes", config: new { partitionKey = "data", partitionKeyCardinality = "baixa" });
        s.Arrow(app, svc);
        s.Arrow(svc, table);
        var challenge = Pix.Challenge();
        var compiled = SceneCompiler.Compile(new SceneCompileRequest
        {
            Scene = s.Build(),
            Provider = CloudProvider.Aws,
            Catalog = TestContent.Get().Catalog,
            Challenge = challenge,
        });

        RuleEngine.Run(new RuleContext(compiled.Graph, challenge, TestContent.Get().Catalog))
            .ShouldContain(f => f.RuleId == "ARQ-010" && f.NodeIds.Contains(table));
    }

    [Fact]
    public void Data_outside_brazil_breaks_residency()
    {
        var s = new SceneBuilder();
        var region = s.Frame("Região us-east-1", -100, -100, 3000, 3000);
        var app = s.Component("client.mobile", "App", 0, 0);
        var svc = s.Component("aws.ecs-fargate", "api", 200, 0, frameId: region);
        var table = s.Component("aws.dynamodb", "contas", 400, 0, frameId: region);
        s.Arrow(app, svc);
        s.Arrow(svc, table);
        var challenge = Pix.Challenge();
        var compiled = SceneCompiler.Compile(new SceneCompileRequest
        {
            Scene = s.Build(),
            Provider = CloudProvider.Aws,
            Catalog = TestContent.Get().Catalog,
            Challenge = challenge,
        });

        RuleEngine.Run(new RuleContext(compiled.Graph, challenge, TestContent.Get().Catalog))
            .Where(f => f.RuleId == "SEC-005")
            .SelectMany(f => f.NodeIds)
            .ShouldBe([svc, table], ignoreOrder: true);
    }

    [Fact]
    public void Rule_ids_are_unique_and_follow_the_spec_naming()
    {
        var ids = RuleEngine.All.Select(r => r.Id).ToList();

        ids.Distinct().Count().ShouldBe(ids.Count);
        ids.ShouldAllBe(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^(ARQ|DAT|SEC|OPS)-\\d{3}$"));
    }
}
