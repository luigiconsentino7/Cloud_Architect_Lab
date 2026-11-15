using Lab.Engine.Tests.Support;
using Lab.Ir;
using Lab.Ir.Scene;

namespace Lab.Engine.Tests.Ir;

public sealed class SceneCompilerTests
{
    private static CompilationResult Compile(SceneBuilder scene, CloudProvider provider = CloudProvider.Aws,
        IReadOnlyList<FlowDefinition>? flows = null, bool reverse = false) =>
        SceneCompiler.Compile(new SceneCompileRequest
        {
            Scene = scene.Build(reverse),
            Flows = flows ?? [],
            Provider = provider,
            Catalog = TestContent.Get().Catalog,
        });

    private static SceneBuilder PaymentScene(out string[] connections)
    {
        var scene = new SceneBuilder();
        var app = scene.Component("client.mobile", "App", 0, 0);
        var gateway = scene.Component("aws.apigateway", "API PIX", 200, 0);
        var service = scene.Component("aws.ecs-fargate", "pix-service", 400, 0);
        var db = scene.Component("aws.aurora-postgresql", "ledger", 600, 0);
        var queue = scene.Component("aws.sqs", "eventos", 600, 200);
        var notifier = scene.Component("aws.lambda", "notificador", 800, 200);
        connections =
        [
            scene.Arrow(app, gateway, id: "c1"),
            scene.Arrow(gateway, service, id: "c2"),
            scene.Arrow(service, db, id: "c3"),
            scene.Arrow(service, queue, id: "c4", dashed: true),
            scene.Arrow(queue, notifier, id: "c5", dashed: true),
        ];
        return scene;
    }

    [Fact]
    public void Compiles_components_and_infers_interactions()
    {
        var result = Compile(PaymentScene(out _));

        result.Graph.Nodes.Count.ShouldBe(6);
        result.Graph.Edges.Count.ShouldBe(5);
        result.Graph.FindEdge("c1")!.Interaction.ShouldBe(Interaction.SyncRequest);
        result.Graph.FindEdge("c3")!.Interaction.ShouldBe(Interaction.DataAccess);
        result.Graph.FindEdge("c4")!.Interaction.ShouldBe(Interaction.AsyncPublish);
        result.Graph.FindEdge("c5")!.Interaction.ShouldBe(Interaction.AsyncConsume);
        result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ShouldBeEmpty();
    }

    [Fact]
    public void Fills_configuration_defaults_from_the_catalog_schema()
    {
        var result = Compile(PaymentScene(out _));
        var service = result.Graph.Nodes.Single(n => n.Label == "pix-service");

        service.Cfg.Int("instances.min", -1).ShouldBe(2);
        service.Cfg.Double("autoscaling.targetCpu", -1).ShouldBe(0.6);
        service.Placement.Region.ShouldBe("sa-east-1");
        service.Placement.Zones.ShouldBe(["a"]);
    }

    [Fact]
    public void Data_access_without_ops_defaults_to_read_and_write()
    {
        var edge = Compile(PaymentScene(out _)).Graph.FindEdge("c3")!;

        edge.Config.Ops!.Select(o => o.Op).ShouldBe([DataOpKind.Read, DataOpKind.Write]);
    }

    [Fact]
    public void Untyped_shape_with_known_label_gets_a_mapping_suggestion()
    {
        var scene = PaymentScene(out _);
        var shape = scene.Shape("Redis");

        var diagnostic = Compile(scene).Diagnostics.Single(d => d.Code == DiagnosticCodes.UntypedShape);

        diagnostic.ElementIds.ShouldBe([shape]);
        diagnostic.Suggestions!.First().CatalogId.ShouldBe("aws.elasticache");
    }

    [Fact]
    public void Untyped_shape_suggestions_follow_the_attempt_provider()
    {
        var scene = new SceneBuilder();
        scene.Shape("Kafka");

        var diagnostic = Compile(scene, CloudProvider.Azure).Diagnostics.Single(d => d.Code == DiagnosticCodes.UntypedShape);

        diagnostic.Suggestions!.First().CatalogId.ShouldBe("azure.eventhubs");
    }

    [Fact]
    public void Free_text_becomes_an_annotation_near_the_closest_component()
    {
        var scene = PaymentScene(out _);
        scene.Note("chave de idempotência = endToEndId", 420, 90);

        var annotation = Compile(scene).Graph.Annotations.Single();

        annotation.Text.ShouldBe("chave de idempotência = endToEndId");
        annotation.Near.ShouldNotBeNull();
    }

    [Fact]
    public void Dangling_arrow_is_reported_and_ignored()
    {
        var scene = PaymentScene(out _);
        var loose = scene.LooseArrowFrom("cmp_03");

        var result = Compile(scene);

        result.Graph.Edges.Count.ShouldBe(5);
        result.Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.DanglingArrow && d.ElementIds.Contains(loose));
    }

    [Fact]
    public void Component_from_another_provider_is_flagged_with_the_equivalent()
    {
        var scene = new SceneBuilder();
        var app = scene.Component("client.web", "Web");
        var cosmos = scene.Component("azure.cosmosdb", "contas");
        var api = scene.Component("aws.lambda", "api");
        scene.Arrow(app, api);
        scene.Arrow(api, cosmos);

        var diagnostic = Compile(scene).Diagnostics.Single(d => d.Code == DiagnosticCodes.ForeignProvider);

        diagnostic.Suggestions!.Single().CatalogId.ShouldBe("aws.dynamodb");
    }

    [Fact]
    public void Synchronous_cycle_is_an_error()
    {
        var scene = new SceneBuilder();
        var a = scene.Component("aws.ecs-fargate", "a");
        var b = scene.Component("aws.ecs-fargate", "b");
        scene.Arrow(a, b);
        scene.Arrow(b, a);

        var result = Compile(scene);

        result.Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.SyncCycle);
        result.HasErrors.ShouldBeTrue();
    }

    [Fact]
    public void Flow_steps_that_no_longer_exist_are_reported()
    {
        var result = Compile(PaymentScene(out _), flows: [new FlowDefinition("UC-1", ["c1", "c2", "apagada", "c3"])]);

        result.Graph.FlowFor("UC-1")!.Steps.ShouldBe(["c1", "c2", "c3"]);
        result.Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.MissingFlowStep && d.IrIds.Contains("apagada"));
    }

    [Fact]
    public void Zone_and_region_frames_define_placement()
    {
        var scene = new SceneBuilder();
        var region = scene.Frame("Região sa-east-1", -50, -50, 2000, 2000);
        var zoneB = scene.Frame("AZ b", 0, 0, 500, 500);
        var service = scene.Component("aws.ecs-fargate", "svc", 10, 10, frameId: zoneB);
        var db = scene.Component("aws.aurora-postgresql", "db", 900, 10, frameId: region);
        scene.Arrow(service, db);

        var graph = Compile(scene).Graph;

        graph.GetNode(service).Placement.Zones.ShouldBe(["b"]);
        graph.GetNode(db).Placement.Region.ShouldBe("sa-east-1");
    }

    [Fact]
    public void Invalid_configuration_is_reported()
    {
        var scene = new SceneBuilder();
        scene.Component("aws.ecs-fargate", "svc", config: new { instances = new { min = 0 } });

        Compile(scene).Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.InvalidConfig);
    }

    [Fact]
    public void Arrowhead_on_the_start_reverses_the_direction()
    {
        var scene = new SceneBuilder();
        var service = scene.Component("aws.ecs-fargate", "svc");
        var db = scene.Component("aws.dynamodb", "tabela");
        scene.Arrow(service, db, id: "x", reversedHeads: true);

        var edge = Compile(scene).Graph.FindEdge("x")!;

        edge.From.ShouldBe(service);
        edge.To.ShouldBe(db);
    }

    [Fact]
    public void Same_external_system_drawn_twice_is_a_single_node()
    {
        var scene = new SceneBuilder();
        var svc = scene.Component("aws.ecs-fargate", "svc");
        scene.Component("external.system", "SPI", externalId: "external:spi");
        scene.Component("external.system", "SPI (cópia)", externalId: "external:spi");
        scene.Arrow(svc, "external:spi");

        var graph = Compile(scene).Graph;

        graph.Nodes.Count(n => n.Archetype == Archetypes.External).ShouldBe(1);
    }

    [Fact]
    public void Compilation_is_deterministic_regardless_of_element_order()
    {
        var first = Compile(PaymentScene(out _));
        var second = Compile(PaymentScene(out _), reverse: true);

        second.IrHash.ShouldBe(first.IrHash);
    }

    [Fact]
    public void Explicit_interaction_that_makes_no_sense_is_an_error()
    {
        var scene = new SceneBuilder();
        var service = scene.Component("aws.ecs-fargate", "svc");
        var db = scene.Component("aws.dynamodb", "tabela");
        scene.Arrow(service, db, interaction: "async-consume");

        Compile(scene).Diagnostics.ShouldContain(d => d.Code == DiagnosticCodes.InvalidInteraction);
    }
}
