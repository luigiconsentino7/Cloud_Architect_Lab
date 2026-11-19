using Lab.Ir;
using Lab.Ir.Flows;

namespace Lab.Rules.Rules;

/// <summary>ARQ-006 — cadeia síncrona longa demais.</summary>
public sealed class DeepSyncChainRule : Rule
{
    private const int MaxSyncHops = 4;

    public override string Id => "ARQ-006";
    public override Dimension Dimension => Dimension.Performance;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var useCase in context.CriticalUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            var deepest = plan.SyncHappyPath
                .Where(c => c.Edge!.Interaction == Interaction.SyncRequest)
                .Select(c => (Call: c, Hops: SyncHops(c)))
                .OrderByDescending(x => x.Hops)
                .FirstOrDefault();
            if (deepest.Call is null || deepest.Hops <= MaxSyncHops) continue;
            var chain = deepest.Call.Ancestors().Reverse().Skip(1).Append(deepest.Call).Select(c => c.Target.Label);
            yield return context.Create(this,
                $"{useCase.Id} tem {deepest.Hops} saltos síncronos em sequência ({string.Join(" → ", chain)}): a latência e as falhas se somam a cada salto.",
                deepest.Call.Ancestors().Select(a => a.Target.Id).Append(deepest.Call.Target.Id), useCase: useCase.Id);
        }
    }

    private static int SyncHops(CallNode call) =>
        1 + call.Ancestors().Count(a => a.Edge?.Interaction == Interaction.SyncRequest);
}

/// <summary>ARQ-007 — chamada síncrona entre regiões no caminho crítico.</summary>
public sealed class CrossRegionSyncRule : Rule
{
    public override string Id => "ARQ-007";
    public override Dimension Dimension => Dimension.Performance;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.CriticalUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            foreach (var call in plan.SyncHappyPath.Where(c => c.Edge!.Interaction is Interaction.SyncRequest or Interaction.DataAccess))
            {
                var from = call.Parent!.Target;
                var to = call.Target;
                if (Archetypes.IsClient(from.Archetype) || Archetypes.IsExternal(to.Archetype)) continue;
                if (from.Placement.Region is { } a && to.Placement.Region is { } b && a != b && reported.Add(call.Edge!.Id))
                {
                    yield return context.Create(this, $"\"{from.Label}\" ({a}) chama \"{to.Label}\" ({b}) de forma síncrona: cada chamada entre regiões soma dezenas de milissegundos.",
                        edges: [call.Edge!.Id], useCase: useCase.Id);
                }
            }
        }
    }
}

/// <summary>ARQ-008 — partições do stream × consumidores × vazão.</summary>
public sealed class StreamPartitionsRule : Rule
{
    public override string Id => "ARQ-008";
    public override Dimension Dimension => Dimension.Performance;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["load.throttled"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var stream in context.Graph.Nodes.Where(n => n.Archetype == Archetypes.Stream))
        {
            var partitions = Math.Max(1, stream.Cfg.Int("partitions", 4));
            var perPartition = context.Definition(stream).Performance.RecordsPerSecondPerPartition ?? 1000;
            var inbound = context.Graph.Incoming(stream.Id)
                .Where(e => e.Interaction is Interaction.AsyncPublish or Interaction.Cdc)
                .Sum(e => context.PeakRates.GetValueOrDefault(e.Id));
            if (inbound > partitions * perPartition)
            {
                yield return context.Create(this,
                    $"\"{stream.Label}\" recebe ~{inbound:N0} registros/s no pico, mas {partitions} partição(ões) suportam ~{partitions * perPartition:N0}/s: as escritas vão ser estranguladas.",
                    [stream.Id]);
            }

            foreach (var consumerEdge in context.Graph.Outgoing(stream.Id).Where(e => e.Interaction == Interaction.AsyncConsume))
            {
                var consumer = context.Graph.FindNode(consumerEdge.To);
                if (consumer?.Archetype != Archetypes.Service) continue;
                var instances = consumer.Cfg.Int("instances.max", 1);
                if (instances > partitions)
                {
                    yield return context.Create(this,
                        $"\"{consumer.Label}\" pode ter {instances} instâncias consumindo \"{stream.Label}\", mas só {partitions} partições: as instâncias extras ficam ociosas.",
                        [consumer.Id, stream.Id], [consumerEdge.Id], severity: Severity.Low);
                }
            }
        }
    }
}

/// <summary>ARQ-009 — função serverless acessando banco relacional sem pool/proxy de conexões.</summary>
public sealed class FunctionConnectionPoolRule : Rule
{
    public override string Id => "ARQ-009";
    public override Dimension Dimension => Dimension.Performance;
    public override Severity DefaultSeverity => Severity.Medium;
    public override IReadOnlyList<string> ConfirmedBy => ["load.connection-errors"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var edge in context.Graph.Edges.Where(e => e.Interaction == Interaction.DataAccess))
        {
            var from = context.Graph.FindNode(edge.From);
            var to = context.Graph.FindNode(edge.To);
            if (from?.Archetype != Archetypes.Function || to?.Archetype != Archetypes.Relational) continue;
            if (!to.Cfg.Bool("connectionProxy"))
            {
                yield return context.Create(this,
                    $"\"{from.Label}\" abre conexões direto em \"{to.Label}\": cada execução concorrente ocupa uma conexão, e um pico esgota o limite do banco. Use um proxy de conexões.",
                    [from.Id, to.Id], [edge.Id]);
            }
        }
    }
}

/// <summary>ARQ-010 — chave de partição de baixa cardinalidade num fluxo com escrita (partição quente).</summary>
public sealed class HotPartitionRule : Rule
{
    public override string Id => "ARQ-010";
    public override Dimension Dimension => Dimension.Performance;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["load.throttled"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var node in context.Graph.Nodes.Where(n => n.Archetype is Archetypes.KeyValue or Archetypes.Document))
        {
            if (node.Cfg.String("partitionKeyCardinality") != "baixa") continue;
            var writes = context.Graph.Incoming(node.Id).Any(e => e.Interaction == Interaction.DataAccess && e.Config.HasWrite);
            if (writes)
            {
                yield return context.Create(this,
                    $"\"{node.Label}\" usa uma chave de partição de baixa cardinalidade ({node.Cfg.String("partitionKey") ?? "?"}) e recebe escritas: tudo cai em poucas partições, que estrangulam bem antes do limite da tabela.",
                    [node.Id]);
            }
        }
    }
}

/// <summary>ARQ-011 — banco compartilhado por três ou mais serviços que escrevem nele.</summary>
public sealed class SharedDatabaseRule : Rule
{
    public override string Id => "ARQ-011";
    public override Dimension Dimension => Dimension.Operability;
    public override Severity DefaultSeverity => Severity.Low;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var store in context.Graph.Nodes.Where(n => Archetypes.IsDurableData(n.Archetype)))
        {
            var writers = context.Graph.Incoming(store.Id)
                .Where(e => e.Interaction == Interaction.DataAccess && e.Config.HasWrite)
                .Select(e => e.From)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (writers.Count >= 3)
            {
                yield return context.Create(this,
                    $"\"{store.Label}\" recebe escritas de {writers.Count} serviços ({string.Join(", ", writers.Select(context.Label))}): mudanças de schema e picos de um afetam todos.",
                    writers.Append(store.Id));
            }
        }
    }
}
