using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;
using Lab.Simulation;

namespace Lab.Cost;

public sealed record CostLine(string NodeId, string Label, string Archetype, string Category, double MonthlyUsd, string Basis);

public sealed record CostReport(
    double MonthlyUsd,
    IReadOnlyList<CostLine> Lines,
    double BudgetUsd,
    IReadOnlyList<string> Assumptions)
{
    public double BudgetUsage => BudgetUsd <= 0 ? 0 : MonthlyUsd / BudgetUsd;
}

/// <summary>Custo mensal estimado na carga do desafio. Preços ilustrativos do catálogo, região do desafio.</summary>
public static class CostModel
{
    private const double HoursPerMonth = 730;
    private const double DaysPerMonth = 30;

    public static CostReport Estimate(ArchitectureGraph graph, ChallengeSpec challenge, ComponentCatalog catalog,
        ScenarioResult? baseline = null, ScenarioResult? peak = null) =>
        EngineCulture.Run(() => EstimateCore(graph, challenge, catalog, baseline, peak));

    private static CostReport EstimateCore(ArchitectureGraph graph, ChallengeSpec challenge, ComponentCatalog catalog,
        ScenarioResult? baseline, ScenarioResult? peak)
    {
        var plans = FlowPlanner.PlanAll(graph, challenge);
        var workload = challenge.Workload;
        var rates = RateEstimator.EdgeRates(challenge, plans, workload.BaselineRps);
        var pattern = workload.DailyPattern;
        var peakHours = Math.Clamp(pattern.PeakHoursPerDay, 0, 24);
        var monthlyFactor = 3600 * ((24 - peakHours) + (peakHours * pattern.PeakMultiplier)) * DaysPerMonth;
        var durableStores = graph.Nodes.Count(n => Archetypes.IsDurableData(n.Archetype));
        var lines = new List<CostLine>();

        foreach (var node in graph.Nodes.OrderBy(n => n.Id, StringComparer.Ordinal))
        {
            if (Archetypes.IsClient(node.Archetype) || Archetypes.IsExternal(node.Archetype)) continue;
            var definition = catalog.Find(node.CatalogId) ?? catalog.DefaultFor(graph.Provider, node.Archetype);
            if (definition is null) continue;
            var price = definition.Pricing;
            var cfg = node.Cfg;
            var incoming = graph.Incoming(node.Id).ToList();
            var callsPerSecond = incoming.Sum(e => rates.GetValueOrDefault(e.Id));
            var monthlyCalls = callsPerSecond * monthlyFactor;
            double monthly;
            string basis;

            switch (node.Archetype)
            {
                case Archetypes.Service or Archetypes.StreamProcessor:
                {
                    var instances = AverageInstances(node, baseline, peak, peakHours);
                    var vcpu = cfg.Double("vcpu", 1);
                    var memory = cfg.Double("memoryGb", 2);
                    var perInstanceHour = (vcpu * (price.PerVcpuHour ?? 0.0656)) + (memory * (price.PerGbHour ?? 0.0072));
                    monthly = (instances * perInstanceHour * HoursPerMonth) + (price.FixedMonthly ?? 0);
                    basis = $"{instances:0.#} instância(s) em média × {vcpu:0.##} vCPU / {memory:0.#} GB × 730 h";
                    break;
                }

                case Archetypes.Function:
                {
                    var memoryGb = cfg.Int("memoryMb", 1024) / 1024.0;
                    var durationSeconds = (baseline?.Nodes.GetValueOrDefault(node.Id)?.MeanServiceMs ?? 50) / 1000;
                    var gbSeconds = monthlyCalls * durationSeconds * memoryGb;
                    var provisioned = cfg.Int("provisionedConcurrency", 0) * memoryGb * HoursPerMonth * 3600 * (price.PerGbSecond ?? 0.0000166667) * 0.25;

                    // Consumindo de fila/stream em lotes, cada invocação processa até batchSize mensagens.
                    var monthlyInvocations = incoming.Sum(e => rates.GetValueOrDefault(e.Id)
                        / (e.Interaction == Interaction.AsyncConsume ? Math.Max(1, e.Config.BatchSize ?? 1) : 1)) * monthlyFactor;
                    monthly = (monthlyInvocations / 1e6 * (price.PerMillionRequests ?? 0.2)) + (gbSeconds * (price.PerGbSecond ?? 0.0000166667)) + provisioned;
                    basis = $"{Millions(monthlyInvocations)} invocações/mês; {Millions(monthlyCalls)} itens × {durationSeconds * 1000:0} ms × {memoryGb:0.##} GB";
                    break;
                }

                case Archetypes.Relational:
                {
                    var className = cfg.String("instanceClass") ?? "medium";
                    var instanceClass = definition.Performance.InstanceClasses?.GetValueOrDefault(className);
                    var hourly = instanceClass?.PricePerHour ?? 0.82;
                    var copies = (cfg.Bool("multiAz") ? price.MultiAzFactor ?? 2 : 1) + cfg.Int("readReplicas", 0) + (cfg.Bool("crossRegionReplica") ? 1 : 0);
                    var storage = cfg.Double("storageGb", workload.StoredGb);
                    monthly = (hourly * copies * HoursPerMonth) + (storage * (price.PerGbStoredMonth ?? 0.23) * copies) + (cfg.Bool("connectionProxy") ? hourly * 0.15 * HoursPerMonth : 0);
                    basis = $"classe {className} × {copies:0.#} cópia(s) × 730 h + {storage:0} GB";
                    break;
                }

                case Archetypes.RelationalDistributed:
                {
                    var nodes = cfg.Int("nodes", 3) * (cfg.Bool("multiRegion") ? 3 : 1);
                    monthly = (nodes * (price.PerInstanceHour ?? 1.26) * HoursPerMonth) + (workload.StoredGb * (price.PerGbStoredMonth ?? 0.39));
                    basis = $"{nodes} nó(s) × 730 h";
                    break;
                }

                case Archetypes.KeyValue or Archetypes.Document or Archetypes.ObjectStorage:
                {
                    var (reads, writes) = Operations(incoming, rates);
                    var monthlyReads = reads * monthlyFactor;
                    var monthlyWrites = writes * monthlyFactor;
                    var storage = durableStores == 0 ? 0 : workload.StoredGb / durableStores;
                    var replicas = cfg.Bool("globalTables") ? 2 : 1;
                    monthly = ((monthlyReads / 1e6 * (price.PerMillionReads ?? 0.19)) + (monthlyWrites / 1e6 * (price.PerMillionWrites ?? 0.94)) + (storage * (price.PerGbStoredMonth ?? 0.25))) * replicas;
                    basis = $"{Millions(monthlyReads)} leituras + {Millions(monthlyWrites)} escritas/mês + {storage:0} GB";
                    break;
                }

                case Archetypes.Cache:
                {
                    var nodes = cfg.Int("nodes", 1) * (1 + cfg.Int("replicas", 0));
                    monthly = nodes * (price.PerInstanceHour ?? 0.25) * HoursPerMonth;
                    basis = $"{nodes} nó(s) × 730 h";
                    break;
                }

                case Archetypes.Queue:
                {
                    // Envio por mensagem; leitura e remoção por lote.
                    var batch = graph.Outgoing(node.Id).Where(e => e.Interaction == Interaction.AsyncConsume)
                        .Select(e => Math.Max(1, e.Config.BatchSize ?? 1)).DefaultIfEmpty(1).Max();
                    var callsPerMessage = 1 + (2.0 / batch);
                    monthly = monthlyCalls * callsPerMessage / 1e6 * (price.PerMillionRequests ?? 0.5);
                    basis = $"{Millions(monthlyCalls)} mensagens/mês × {callsPerMessage:0.##} chamadas (envio + leitura e remoção em lotes de {batch})";
                    break;
                }

                case Archetypes.Topic or Archetypes.EventBus:
                {
                    var subscriptions = Math.Max(1, graph.Outgoing(node.Id).Count());
                    monthly = monthlyCalls * subscriptions / 1e6 * (price.PerMillionRequests ?? 0.5);
                    basis = $"{Millions(monthlyCalls)} publicações/mês × {subscriptions} assinatura(s)";
                    break;
                }

                case Archetypes.Stream:
                {
                    var partitions = cfg.Int("partitions", 4);
                    monthly = (partitions * (price.PerPartitionHour ?? 0.021) * HoursPerMonth) + (monthlyCalls / 1e6 * (price.PerMillionRequests ?? 0.02));
                    basis = $"{partitions} partição(ões) × 730 h + {Millions(monthlyCalls)} registros/mês";
                    break;
                }

                case Archetypes.LoadBalancer:
                    monthly = ((price.PerInstanceHour ?? 0.0378) * HoursPerMonth) + (monthlyCalls / 1e6 * (price.PerMillionRequests ?? 0.8));
                    basis = $"730 h + {Millions(monthlyCalls)} requisições/mês";
                    break;
                default:
                    monthly = (price.FixedMonthly ?? 0) + (monthlyCalls / 1e6 * (price.PerMillionRequests ?? 0));
                    basis = monthlyCalls > 0 ? $"{Millions(monthlyCalls)} requisições/mês" : "valor fixo mensal";
                    break;
            }

            if (monthly <= 0.005) continue;
            lines.Add(new CostLine(node.Id, node.Label, node.Archetype, Category(node.Archetype), Round(monthly), basis));
        }

        var egress = Egress(graph, challenge, catalog, plans, monthlyFactor);
        if (egress is not null) lines.Add(egress);

        var assumptions = new List<string>
        {
            "Preços ilustrativos do catálogo (USD, região do desafio), sem impostos nem descontos de reserva.",
            $"Volume mensal: {workload.BaselineRps:N0} req/s de base, com {peakHours:0.#} h/dia em pico de {pattern.PeakMultiplier:0.#}×.",
            "Instâncias médias de contêineres vêm da simulação quando disponível; sem ela, usa o mínimo configurado.",
        };
        return new CostReport(Round(lines.Sum(l => l.MonthlyUsd)), lines, challenge.Constraints.BudgetUsdMonth, assumptions);
    }

    private static double AverageInstances(Node node, ScenarioResult? baseline, ScenarioResult? peak, double peakHours)
    {
        var minimum = node.Archetype == Archetypes.StreamProcessor ? node.Cfg.Int("parallelism", 4) : node.Cfg.Int("instances.min", 2);
        var normal = baseline?.Nodes.GetValueOrDefault(node.Id)?.InstancesAvg is > 0 and var b ? b : minimum;
        var busy = peak?.Nodes.GetValueOrDefault(node.Id)?.InstancesAvg is > 0 and var p ? p : normal;
        return ((normal * (24 - peakHours)) + (busy * peakHours)) / 24;
    }

    private static (double Reads, double Writes) Operations(IEnumerable<Edge> incoming, IReadOnlyDictionary<string, double> rates)
    {
        double reads = 0, writes = 0;
        foreach (var edge in incoming.Where(e => e.Interaction == Interaction.DataAccess))
        {
            var rate = rates.GetValueOrDefault(edge.Id);
            foreach (var op in edge.Config.Ops ?? [])
            {
                if (op.IsWrite) writes += rate * op.Count;
                else reads += rate * op.Count;
            }
        }

        return (reads, writes);
    }

    private static CostLine? Egress(ArchitectureGraph graph, ChallengeSpec challenge, ComponentCatalog catalog,
        IReadOnlyDictionary<string, FlowPlan> plans, double monthlyFactor)
    {
        var gigabytes = 0.0;
        var pricePerGb = 0.15;
        foreach (var useCase in challenge.UseCases)
        {
            var entry = plans.GetValueOrDefault(useCase.Id)?.Root?.Children.FirstOrDefault();
            if (entry is null) continue;
            var load = challenge.Workload.Mix.GetValueOrDefault(useCase.Id);
            if (load is null) continue;
            var monthlyRequests = challenge.Workload.BaselineRps * load.Share * monthlyFactor;
            gigabytes += monthlyRequests * load.PayloadKb / (1024 * 1024);
            var definition = catalog.Find(entry.Target.CatalogId);
            if (definition?.Pricing.PerGbTransfer is { } p) pricePerGb = p;
        }

        if (gigabytes <= 0) return null;
        return new CostLine("internet-egress", "Transferência para a internet", "network.egress", "transferência",
            Round(gigabytes * pricePerGb), $"{gigabytes:N0} GB/mês de respostas × US$ {pricePerGb:0.00}/GB");
    }

    private static string Category(string archetype) => Archetypes.Family(archetype) switch
    {
        "compute" or "workflow" => "computação",
        "data" => "dados",
        "messaging" => "mensageria",
        "edge" or "network" => "borda",
        _ => "segurança e operação",
    };

    private static string Millions(double value) =>
        value >= 1e6 ? $"{value / 1e6:N1} mi" : $"{value:N0}";

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
