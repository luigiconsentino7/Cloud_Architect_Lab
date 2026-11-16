using System.Collections.Concurrent;
using System.Diagnostics;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;
using Lab.Simulation.Core;
using Lab.Simulation.Runtime;
using Lab.Simulation.Runtime.Nodes;

namespace Lab.Simulation;

public sealed record SimulationRequest
{
    public required ArchitectureGraph Graph { get; init; }
    public required ChallengeSpec Challenge { get; init; }
    public required ComponentCatalog Catalog { get; init; }

    /// <summary>Cenários a rodar (nulo = todos do plano de testes).</summary>
    public IReadOnlyList<string>? ScenarioIds { get; init; }

    public int Replications { get; init; } = 1;
    public long EventBudget { get; init; } = 20_000_000;
    public int MaxParallelism { get; init; } = Math.Max(1, Environment.ProcessorCount - 1);
    public bool IncludeSeries { get; init; } = true;

    /// <summary>Avisado a cada cenário (e replicação) concluído — alimenta "Simulando payday-peak, 2 de 6".</summary>
    public IProgress<SimulationProgress>? Progress { get; init; }
}

public sealed record SimulationProgress(int Completed, int Total, string ScenarioId);

/// <summary>Simulador de eventos discretos. Mesma entrada + mesma semente = mesmo resultado.</summary>
public static class Simulator
{
    public const string EngineVersion = "0.1.0";

    public static SimulationReport Run(SimulationRequest request, CancellationToken cancellationToken = default) =>
        EngineCulture.Run(() => RunCore(request, cancellationToken));

    private static SimulationReport RunCore(SimulationRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var plans = FlowPlanner.PlanAll(request.Graph, request.Challenge);
        var scenarios = request.Challenge.TestPlan.Scenarios
            .Where(s => request.ScenarioIds is null || request.ScenarioIds.Contains(s.Id))
            .ToList();
        var replications = Math.Max(1, request.Replications);
        var jobs = scenarios.SelectMany(s => Enumerable.Range(0, replications).Select(r => (Scenario: s, Replication: r))).ToList();
        var outputs = new ConcurrentDictionary<(string, int), ScenarioResult>();

        var completed = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = request.MaxParallelism, CancellationToken = cancellationToken },
            job =>
            {
                outputs[(job.Scenario.Id, job.Replication)] = RunOne(request, plans, job.Scenario, job.Replication, cancellationToken);
                request.Progress?.Report(new SimulationProgress(Interlocked.Increment(ref completed), jobs.Count, job.Scenario.Id));
            });

        var results = scenarios
            .Select(s => Aggregate(Enumerable.Range(0, replications).Select(r => outputs[(s.Id, r)]).ToList()))
            .ToList();
        return new SimulationReport
        {
            EngineVersion = EngineVersion,
            MasterSeed = request.Challenge.MasterSeed,
            Scenarios = results,
            WallTimeMs = stopwatch.Elapsed.TotalMilliseconds,
        };
    }

    public static long SeedFor(long masterSeed, string scenarioId, int replication) =>
        unchecked((long)Rng.Fnv1a($"{masterSeed}:{scenarioId}:{replication}"));

    private static ScenarioResult RunOne(SimulationRequest request, IReadOnlyDictionary<string, FlowPlan> plans,
        ScenarioSpec scenario, int replication, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var challenge = request.Challenge;
        var curve = LoadCurve.From(scenario, challenge.TestPlan, challenge.Workload);
        var compression = 1.0;
        var estimated = EstimateEvents(curve, plans);
        if (estimated > request.EventBudget)
        {
            compression = Math.Max(0.2, request.EventBudget / estimated);
            curve = LoadCurve.From(scenario, challenge.TestPlan, challenge.Workload, compression);
        }

        var seed = SeedFor(challenge.MasterSeed, scenario.Id, replication);
        var world = new World(new WorldInput(request.Graph, challenge, request.Catalog, plans, scenario, curve, seed, request.EventBudget * 3));
        world.Run(cancellationToken);
        return Collect(world, scenario, curve, seed, compression, stopwatch.Elapsed.TotalMilliseconds, request.IncludeSeries);
    }

    private static double EstimateEvents(LoadCurve curve, IReadOnlyDictionary<string, FlowPlan> plans)
    {
        var callsPerRequest = plans.Values.Where(p => p.Calls.Count > 0).Select(p => (double)p.Calls.Count).DefaultIfEmpty(1).Average();
        return curve.ExpectedRequests() * ((callsPerRequest * 5) + 4);
    }

    private static ScenarioResult Collect(World world, ScenarioSpec scenario, LoadCurve curve, long seed, double compression,
        double wallTimeMs, bool includeSeries)
    {
        var useCases = world.Metrics.ToDictionary(p => p.Key, p => UseCaseOf(p.Key, p.Value), StringComparer.Ordinal);
        var nodes = world.Nodes.Values
            .Where(n => !Archetypes.IsClient(n.Node.Archetype))
            .ToDictionary(n => n.Node.Id, n => NodeOf(world, n), StringComparer.Ordinal);
        var integrity = new IntegrityResult(world.Integrity.DuplicateEffects, world.Integrity.MonetaryDuplicates,
            world.Integrity.LostEvents, world.Integrity.OrderingViolations, world.Integrity.DlqMessages);

        var facts = new SortedSet<string>(StringComparer.Ordinal);
        if (nodes.Values.Any(n => n.Throttled > 0)) facts.Add("throttled");
        if (nodes.Values.Any(n => n.ConnectionErrors > 0)) facts.Add("connection-errors");
        if (nodes.Values.Any(n => n.Rejected > 0)) facts.Add("rejected");
        if (integrity.DuplicateEffects > 0) facts.Add("duplicate-effects");
        if (integrity.MonetaryDuplicates > 0) facts.Add("monetary-duplicates");
        if (integrity.LostEvents > 0) facts.Add("lost-events");
        if (integrity.OrderingViolations > 0) facts.Add("ordering-violations");
        if (integrity.DlqMessages > 0) facts.Add("dlq");
        if (world.Nodes.Values.Any(n => n.DataLoss)) facts.Add("data-loss");

        return new ScenarioResult
        {
            ScenarioId = scenario.Id,
            Name = scenario.Name,
            Kind = scenario.Kind,
            Visibility = scenario.Visibility,
            Seed = seed,
            DurationSeconds = curve.Total,
            WarmupSeconds = curve.Warmup,
            PeakOfferedRps = curve.Peak,
            UseCases = useCases,
            Nodes = nodes,
            Integrity = integrity,
            Bottlenecks = Bottlenecks(world, nodes),
            Facts = facts.ToList(),
            Series = includeSeries ? SeriesOf(world) : null,
            Events = world.Kernel.Events,
            WallTimeMs = wallTimeMs,
            TimeCompression = compression,
            BudgetExceeded = world.Kernel.BudgetExceeded,
        };
    }

    private static UseCaseResult UseCaseOf(string id, UseCaseMetrics metrics) => new()
    {
        UseCaseId = id,
        Requests = metrics.Requests,
        Succeeded = metrics.Succeeded,
        SuccessRate = metrics.Requests == 0 ? 1 : metrics.Succeeded / (double)metrics.Requests,
        Latency = Summary(metrics.Latency),
        AsyncCompletion = metrics.AsyncLatency.Count == 0 ? null : Summary(metrics.AsyncLatency),
        AsyncExpected = metrics.AsyncExpected,
        AsyncCompleted = metrics.AsyncCompleted,
        AsyncFailed = metrics.AsyncFailed,
        Errors = metrics.Errors.OrderBy(e => e.Key).ToDictionary(e => Outcome.Name(e.Key), e => e.Value, StringComparer.Ordinal),
        P99ByReplication = [Round(metrics.Latency.PercentileMs(0.99))],
    };

    private static LatencySummary Summary(LatencyHistogram histogram) => new(
        Round(histogram.PercentileMs(0.50)), Round(histogram.PercentileMs(0.95)), Round(histogram.PercentileMs(0.99)),
        Round(histogram.MeanMs), Round(histogram.MaxUs / 1000));

    private static NodeResult NodeOf(World world, SimNode node)
    {
        var from = (int)world.Input.Curve.Warmup;
        var to = Math.Min(node.UtilizationSeries.Length, (int)world.Input.Curve.Total);
        var window = node.UtilizationSeries.Skip(from).Take(Math.Max(0, to - from)).ToList();
        var service = node as ServiceNode;
        var meanServiceMs = node switch
        {
            FunctionNode f when f.Completed > 0 => f.DurationMsSum / f.Completed,
            _ when node.ServiceCount > 0 => node.ServiceMsSum / node.ServiceCount,
            _ => 0,
        };
        return new NodeResult
        {
            NodeId = node.Node.Id,
            Label = node.Node.Label,
            Archetype = node.Node.Archetype,
            Requests = node.Requests,
            Throttled = node.Throttled,
            Errors = node.Errors,
            Rejected = node.Rejected,
            ConnectionErrors = node.ConnectionErrors,
            ColdStarts = node.ColdStarts,
            UtilizationAvg = Round(window.Count == 0 ? 0 : window.Average(), 4),
            UtilizationMax = Round(window.Count == 0 ? 0 : window.Max(), 4),
            MaxQueue = node switch
            {
                ServiceNode s => s.MaxQueue,
                RelationalNode r => r.MaxQueue,
                _ => node.QueueSeries.DefaultIfEmpty(0).Max(),
            },
            InstancesMin = service is null ? 0 : (service.InstancesMin == int.MaxValue ? service.Instances : service.InstancesMin),
            InstancesMax = service?.InstancesMax ?? 0,
            InstancesAvg = service is null || service.InstanceSamples == 0 ? 0 : Round(service.InstanceSecondsSum / service.InstanceSamples, 2),
            ConcurrencyMax = node.ConcurrencyMax,
            ConcurrencyLimit = node.ConcurrencyLimit,
            BacklogMax = node.BacklogMax,
            OldestMessageAgeMaxSeconds = Round(node.OldestAgeMaxSeconds, 2),
            DlqMessages = node.DlqMessages,
            MeanServiceMs = Round(meanServiceMs, 3),
            PeakArrivalRps = node.Arrivals.Skip(from).Take(Math.Max(1, to - from)).DefaultIfEmpty(0).Max(),
            UnavailableSeconds = node is RelationalNode db ? Round(db.UnavailableSecondsAt(world.Kernel.Now), 1) : 0,
            Failovers = node is RelationalNode r2 ? r2.Failovers : 0,
        };
    }

    private static IReadOnlyList<Bottleneck> Bottlenecks(World world, IReadOnlyDictionary<string, NodeResult> nodes)
    {
        var found = new List<(double Weight, Bottleneck Item)>();
        foreach (var node in nodes.Values)
        {
            switch (node.Archetype)
            {
                case Archetypes.Function when node.Throttled > 0:
                {
                    var needed = Math.Ceiling(node.PeakArrivalRps * node.MeanServiceMs / 1000);
                    found.Add((node.Throttled, new Bottleneck(node.NodeId, "concurrency-limit",
                        $"\"{node.Label}\" precisou de ~{needed:N0} execuções simultâneas (lei de Little: {node.PeakArrivalRps:N0}/s × {node.MeanServiceMs:N0} ms), mas o limite é {node.ConcurrencyLimit:N0}; {node.Throttled:N0} chamadas foram rejeitadas.",
                        needed, node.ConcurrencyLimit)));
                    break;
                }

                case Archetypes.Service or Archetypes.StreamProcessor when node.UtilizationMax >= 0.9 || node.Rejected > 0:
                    found.Add((node.Rejected + (node.UtilizationMax * 1000), new Bottleneck(node.NodeId, "cpu",
                        $"\"{node.Label}\" chegou a {node.UtilizationMax:P0} de CPU com {node.InstancesMax} instância(s); {node.Rejected:N0} requisições rejeitadas por fila cheia.",
                        node.InstancesMax, null)));
                    break;
                case Archetypes.Relational when node.UnavailableSeconds > 0:
                    found.Add((node.Errors + (node.UnavailableSeconds * 1000), new Bottleneck(node.NodeId, "failover",
                        node.Failovers > 0
                            ? $"\"{node.Label}\" ficou ~{node.UnavailableSeconds:N0} s sem aceitar operações enquanto o primário fazia failover para outra zona; {node.Errors:N0} operações falharam nesse intervalo."
                            : $"\"{node.Label}\" ficou ~{node.UnavailableSeconds:N0} s fora com a zona do primário (sem standby em outra zona); {node.Errors:N0} operações falharam.",
                        node.UnavailableSeconds, null)));
                    break;
                case Archetypes.Relational or Archetypes.RelationalDistributed when node.ConnectionErrors > 0:
                    found.Add((node.ConnectionErrors, new Bottleneck(node.NodeId, "connections",
                        $"\"{node.Label}\" esgotou as conexões ({node.ConcurrencyLimit:N0}): {node.ConnectionErrors:N0} chamadas falharam ao conectar.",
                        node.ConcurrencyMax, node.ConcurrencyLimit)));
                    break;
                case Archetypes.Relational or Archetypes.RelationalDistributed when node.UtilizationMax >= 0.9:
                    found.Add((node.UtilizationMax * 1000, new Bottleneck(node.NodeId, "db-saturation",
                        $"\"{node.Label}\" ficou saturado ({node.UtilizationMax:P0} de ocupação); a fila de operações cresceu até {node.MaxQueue:N0}.")));
                    break;
                case Archetypes.Queue or Archetypes.Stream when node.OldestMessageAgeMaxSeconds > 30:
                    found.Add((node.OldestMessageAgeMaxSeconds, new Bottleneck(node.NodeId, "consumer-lag",
                        $"O consumidor de \"{node.Label}\" não acompanhou: backlog de {node.BacklogMax:N0} mensagens, a mais antiga com {node.OldestMessageAgeMaxSeconds:N0} s.")));
                    break;
                case Archetypes.External when node.Errors > Math.Max(5, node.Requests * 0.005):
                    found.Add((node.Errors, new Bottleneck(node.NodeId, "dependency",
                        $"\"{node.Label}\" falhou {node.Errors:N0} vezes durante o cenário.")));
                    break;
            }

            if (node.Throttled > 0 && node.Archetype is not Archetypes.Function)
            {
                found.Add((node.Throttled, new Bottleneck(node.NodeId, "throttled",
                    $"\"{node.Label}\" estrangulou {node.Throttled:N0} chamadas por limite de vazão ({node.PeakArrivalRps:N0}/s no pico).")));
            }
        }

        _ = world;
        return found.OrderByDescending(f => f.Weight).ThenBy(f => f.Item.NodeId, StringComparer.Ordinal).Select(f => f.Item).Take(5).ToList();
    }

    private static TimeSeries SeriesOf(World world)
    {
        var useCases = world.Metrics.ToDictionary(p => p.Key, p => new UseCaseSeries(
            p.Value.SeriesRequests,
            p.Value.SeriesErrors,
            p.Value.SeriesLatency.Select(h => Round(h.PercentileMs(0.99))).ToArray()), StringComparer.Ordinal);
        var nodes = world.Nodes.Values
            .Where(n => !Archetypes.IsClient(n.Node.Archetype))
            .ToDictionary(n => n.Node.Id, n => new NodeSeries(
                n.UtilizationSeries.Select(v => Round(v, 3)).ToArray(),
                n.QueueSeries,
                n.InstanceSeries,
                n.ThrottledSeries), StringComparer.Ordinal);
        return new TimeSeries(world.SeriesSeconds, world.Offered.ToArray(), useCases, nodes);
    }

    private static ScenarioResult Aggregate(IReadOnlyList<ScenarioResult> runs)
    {
        if (runs.Count == 1) return runs[0];
        var first = runs[0];
        var useCases = first.UseCases.Keys.ToDictionary(id => id, id =>
        {
            var all = runs.Select(r => r.UseCases[id]).ToList();
            var requests = all.Sum(u => u.Requests);
            var succeeded = all.Sum(u => u.Succeeded);
            return all[0] with
            {
                Requests = requests,
                Succeeded = succeeded,
                SuccessRate = requests == 0 ? 1 : succeeded / (double)requests,
                Latency = Average(all.Select(u => u.Latency)),
                AsyncCompletion = all.All(u => u.AsyncCompletion is null) ? null : Average(all.Select(u => u.AsyncCompletion).OfType<LatencySummary>()),
                AsyncExpected = all.Sum(u => u.AsyncExpected),
                AsyncCompleted = all.Sum(u => u.AsyncCompleted),
                AsyncFailed = all.Sum(u => u.AsyncFailed),
                Errors = all.SelectMany(u => u.Errors).GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Sum(e => e.Value), StringComparer.Ordinal),
                P99ByReplication = all.Select(u => u.Latency.P99).ToList(),
            };
        }, StringComparer.Ordinal);

        return first with
        {
            Replications = runs.Count,
            UseCases = useCases,
            Integrity = new IntegrityResult(
                runs.Sum(r => r.Integrity.DuplicateEffects), runs.Sum(r => r.Integrity.MonetaryDuplicates),
                runs.Sum(r => r.Integrity.LostEvents), runs.Sum(r => r.Integrity.OrderingViolations),
                runs.Sum(r => r.Integrity.DlqMessages)),
            Facts = runs.SelectMany(r => r.Facts).Distinct().Order(StringComparer.Ordinal).ToList(),
            Events = runs.Sum(r => r.Events),
            WallTimeMs = runs.Max(r => r.WallTimeMs),
        };
    }

    private static LatencySummary Average(IEnumerable<LatencySummary> items)
    {
        var list = items.ToList();
        return new LatencySummary(
            Round(list.Average(i => i.P50)), Round(list.Average(i => i.P95)), Round(list.Average(i => i.P99)),
            Round(list.Average(i => i.Mean)), Round(list.Max(i => i.Max)));
    }

    private static double Round(double value, int digits = 2) => Math.Round(value, digits, MidpointRounding.AwayFromZero);
}
