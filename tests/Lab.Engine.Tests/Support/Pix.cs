using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Scene;

namespace Lab.Engine.Tests.Support;

public sealed record Design(JsonObject Scene, IReadOnlyList<FlowDefinition> Flows);

/// <summary>Desafio "Envio de PIX" (o exemplo ponta a ponta da spec) e dois desenhos: um sólido e um com defeitos clássicos.</summary>
public static class Pix
{
    public static ChallengeSpec Challenge(CloudProvider provider = CloudProvider.Aws, double baselineRps = 1500) => new()
    {
        Id = "chl_test_pix",
        SeedId = "pix/envio-pix",
        Title = "Envio de PIX da FinBank",
        Difficulty = Difficulty.Senior,
        Provider = provider,
        MasterSeed = 913337,
        UseCases =
        [
            new UseCaseSpec
            {
                Id = "UC-1",
                Name = "Enviar PIX",
                EntryClient = "client.mobile",
                Monetary = true,
                RequiredCapabilities =
                [
                    new CapabilityRequirement { Capability = "authenticate" },
                    new CapabilityRequirement { Capability = "idempotency-check" },
                    new CapabilityRequirement { Capability = "atomic-balance-update", Before = "respond" },
                    new CapabilityRequirement { Capability = "external-call", Target = "external:spi" },
                    new CapabilityRequirement { Capability = "publish-event" },
                    new CapabilityRequirement { Capability = "notify" },
                ],
                NegativePaths = [new NegativePathRequirement { Dependency = "external:spi", Description = "a transação fica pendente e é reconciliada" }],
            },
            new UseCaseSpec { Id = "UC-2", Name = "Consultar status", EntryClient = "client.mobile" },
        ],
        ExternalSystems =
        [
            new ExternalSystemSpec { Id = "external:spi", Name = "SPI (BACEN)", Latency = new LatencyDist(120, 400), Availability = 0.9995, Idempotent = true, SideEffect = true },
            new ExternalSystemSpec { Id = "external:push", Name = "Provedor de push", Latency = new LatencyDist(80, 300), Availability = 0.999, Idempotent = false, SideEffect = true },
        ],
        Workload = new WorkloadSpec
        {
            BaselineRps = baselineRps,
            Mix = new Dictionary<string, UseCaseLoad>
            {
                ["UC-1"] = new() { Share = 0.6, ComputeCostMs = new LatencyDist(4, 15) },
                ["UC-2"] = new() { Share = 0.4, ComputeCostMs = new LatencyDist(1, 4) },
            },
            Cacheability = new Dictionary<string, double> { ["UC-2"] = 0.7 },
        },
        TestPlan = new TestPlan
        {
            Scenarios =
            [
                new ScenarioSpec { Id = "baseline", Kind = ScenarioKind.Load, Load = new LoadProfile { Rps = baselineRps, Duration = 60, Warmup = 10 } },
                new ScenarioSpec
                {
                    Id = "payday-peak", Kind = ScenarioKind.Load,
                    Load = new LoadProfile { Profile = LoadShape.Ramp, FromRps = baselineRps, ToRps = baselineRps * 5, RampFor = 30, HoldFor = 60, Warmup = 10 },
                },
                new ScenarioSpec
                {
                    Id = "spi-slow", Kind = ScenarioKind.Chaos, Visibility = Visibility.Hidden, Load = new LoadProfile { Ref = "baseline" },
                    Faults = [new FaultSpec { Type = FaultType.LatencyInjection, Target = "external:spi", AddMs = new LatencyDist(800, 3000), At = 15, For = 30 }],
                },
                new ScenarioSpec
                {
                    Id = "duplicates", Kind = ScenarioKind.Integrity, Visibility = Visibility.Hidden, Load = new LoadProfile { Ref = "baseline" },
                    Faults = [new FaultSpec { Type = FaultType.DuplicateDelivery, Probability = 0.02 }, new FaultSpec { Type = FaultType.ClientRetry, OnTimeoutMs = 1500 }],
                },
            ],
            Slos =
            [
                new SloSpec { Id = "SLO-1", UseCase = "UC-1", Metric = "latency.p99", Max = 1000, Scenarios = ["baseline", "payday-peak"] },
                new SloSpec { Id = "SLO-2", UseCase = "UC-1", Metric = "success-rate", Min = 0.9995, Scenarios = ["baseline", "payday-peak", "spi-slow"] },
                new SloSpec { Id = "SLO-3", UseCase = "UC-1", Metric = "async-completion.p95", Max = 5000, Scenarios = ["baseline"] },
                new SloSpec { Id = "SLO-4", Metric = "duplicate-effects", Max = 0, Scenarios = ["duplicates"] },
                new SloSpec { Id = "SLO-5", UseCase = "UC-2", Metric = "latency.p99", Max = 200, Scenarios = ["baseline"] },
            ],
        },
        Constraints = new ConstraintsSpec { BudgetUsdMonth = 12000, DataResidencyBrazil = true, Compliance = ["lgpd", "bacen-4893"], RequireEncryptionAtRest = true },
    };

    /// <summary>Desenho sólido: ECS multi-AZ, Aurora multi-AZ com transação, SPI com timeout curto + circuit breaker + fila de pendências, outbox e consumidor idempotente.</summary>
    public static Design Good()
    {
        var s = new SceneBuilder();
        var app = s.Component("client.mobile", "App", id: "app");
        var waf = s.Component("aws.waf", "WAF", id: "waf");
        var gw = s.Component("aws.apigateway", "API PIX", id: "gw", config: new { auth = "jwt", waf = true, throttle = new { rps = 20000, burst = 10000 } });
        var svc = s.Component("aws.ecs-fargate", "pix-service", id: "svc", config: new
        {
            instances = new { min = 6, max = 40 }, vcpu = 1, concurrencyPerInstance = 64, zones = new[] { "a", "b", "c" },
            observability = new { metrics = true, logs = true, tracing = true, alarms = true },
        });
        var db = s.Component("aws.aurora-postgresql", "ledger", id: "db", config: new { instanceClass = "xlarge", multiAz = true, readReplicas = 1 });
        var cache = s.Component("aws.elasticache", "cache-status", id: "cache", config: new { nodes = 2, replicas = 1, multiAz = true, zones = new[] { "a", "b" } });
        s.Component("external.system", "SPI", id: "spi_box", externalId: "external:spi");
        var events = s.Component("aws.sqs", "eventos-pix", id: "events", config: new { dlq = new { enabled = true, maxReceiveCount = 5 }, observability = new { alarms = true } });
        var pending = s.Component("aws.sqs", "pendencias", id: "pending", config: new { dlq = new { enabled = true, maxReceiveCount = 5 }, observability = new { alarms = true } });
        var notifier = s.Component("aws.lambda", "notificador", id: "notifier", config: new
        {
            reservedConcurrency = 200, idempotency = new { enabled = true, storeNodeId = "db" },
            observability = new { metrics = true, logs = true, tracing = true },
        });
        s.Component("external.system", "Push", id: "push_box", externalId: "external:push");

        s.Arrow(app, waf, id: "e01", config: new { idempotencyKey = true });
        s.Arrow(waf, gw, id: "e02");
        s.Arrow(gw, svc, id: "e03", config: new { timeoutMs = 900 });
        s.Arrow(svc, db, id: "e04", config: new { ops = new[] { new { op = "transaction", count = 1 } } });
        s.Arrow(svc, "external:spi", id: "e05", config: new
        {
            timeoutMs = 450, retries = new { max = 1, backoff = "exponential-jitter", baseMs = 50 },
            circuitBreaker = new { enabled = true, errorRateThreshold = 0.5, openMs = 20000 },
        });
        s.Arrow(svc, pending, id: "e06", config: new { trigger = "on-failure", triggerOf = "e05", delivery = "outbox" });
        s.Arrow(svc, events, id: "e07", config: new { delivery = "outbox" });
        s.Arrow(events, notifier, id: "e08", config: new { batchSize = 10 });
        s.Arrow(notifier, db, id: "e09", config: new { ops = new[] { new { op = "conditional-write", count = 1 } } });
        s.Arrow(notifier, "external:push", id: "e10", config: new { timeoutMs = 1000 });
        s.Arrow(svc, cache, id: "e11", config: new { ops = new[] { new { op = "read", count = 1 } } });
        s.Arrow(svc, db, id: "e12", config: new { trigger = "on-cache-miss", triggerOf = "e11", ops = new[] { new { op = "read", count = 1 } } });

        return new Design(s.Build(),
        [
            new FlowDefinition("UC-1", ["e01", "e02", "e03", "e04", "e05", "e06", "e07", "e08", "e09", "e10"]),
            new FlowDefinition("UC-2", ["e01", "e02", "e03", "e11", "e12"]),
        ]);
    }

    /// <summary>Tentativa 1 do exemplo da spec: Lambda no caminho síncrono, SPI sem breaker, publicação direta e notificador sem idempotência.</summary>
    public static Design Bad()
    {
        var s = new SceneBuilder();
        var app = s.Component("client.mobile", "App", id: "app");
        var gw = s.Component("aws.apigateway", "API PIX", id: "gw", config: new { auth = "jwt" });
        var sender = s.Component("aws.lambda", "pix-sender", id: "sender", config: new { memoryMb = 1024 });
        var table = s.Component("aws.dynamodb", "saldos", id: "table");
        s.Component("external.system", "SPI", id: "spi_box", externalId: "external:spi");
        var topic = s.Component("aws.sns", "eventos", id: "topic");
        var notifier = s.Component("aws.lambda", "notificador", id: "notifier");
        s.Component("external.system", "Push", id: "push_box", externalId: "external:push");

        s.Arrow(app, gw, id: "b01", config: new { idempotencyKey = true });
        s.Arrow(gw, sender, id: "b02", config: new { timeoutMs = 3000 });
        s.Arrow(sender, table, id: "b03", config: new { ops = new[] { new { op = "conditional-write", count = 1 } } });
        s.Arrow(sender, "external:spi", id: "b04", config: new { timeoutMs = 2000, retries = new { max = 2, backoff = "fixed", baseMs = 100 } });
        s.Arrow(sender, topic, id: "b05");
        s.Arrow(topic, notifier, id: "b06");
        s.Arrow(notifier, "external:push", id: "b07");
        s.Arrow(sender, table, id: "b08", config: new { ops = new[] { new { op = "read", count = 1 } } });

        return new Design(s.Build(),
        [
            new FlowDefinition("UC-1", ["b01", "b02", "b03", "b04", "b05", "b06", "b07"]),
            new FlowDefinition("UC-2", ["b01", "b02", "b08"]),
        ]);
    }

    public static CompilationResult Compile(Design design, ChallengeSpec challenge, ComponentCatalog? catalog = null) =>
        SceneCompiler.Compile(new SceneCompileRequest
        {
            Scene = design.Scene,
            Flows = design.Flows,
            Provider = challenge.Provider,
            Catalog = catalog ?? TestContent.Get().Catalog,
            Challenge = challenge,
        });
}
