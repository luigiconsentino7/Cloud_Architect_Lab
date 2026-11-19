using Lab.Ir;

namespace Lab.Rules.Rules;

/// <summary>OPS-001 — componente do caminho crítico sem métricas/logs, e ausência de tracing distribuído.</summary>
public sealed class ObservabilityRule : Rule
{
    public override string Id => "OPS-001";
    public override Dimension Dimension => Dimension.Operability;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var critical = context.CriticalUseCases
            .Select(context.PlanFor)
            .Where(p => p is not null)
            .SelectMany(p => p!.SyncHappyPath.Select(c => c.Target))
            .Where(n => !Archetypes.IsClient(n.Archetype) && !Archetypes.IsExternal(n.Archetype))
            .DistinctBy(n => n.Id)
            .ToList();

        foreach (var node in critical.Where(n => !n.Cfg.Bool("observability.metrics", true) || !n.Cfg.Bool("observability.logs", true)))
            yield return context.Create(this, $"\"{node.Label}\" está no caminho crítico sem métricas ou logs: num incidente, ninguém vê o que ele está fazendo.", [node.Id]);

        var computes = context.Graph.Nodes.Where(n => Archetypes.IsCompute(n.Archetype)).ToList();
        if (computes.Count >= 2 && computes.All(n => !n.Cfg.Bool("observability.tracing")))
        {
            yield return context.Create(this,
                "Nenhum serviço tem tracing distribuído: com várias chamadas encadeadas, achar onde a latência nasce vira adivinhação.",
                computes.Select(n => n.Id), severity: Severity.Low);
        }
    }
}

/// <summary>OPS-002 — fila sem alarme de backlog ou de idade da mensagem mais antiga.</summary>
public sealed class QueueAlarmRule : Rule
{
    public override string Id => "OPS-002";
    public override Dimension Dimension => Dimension.Operability;
    public override Severity DefaultSeverity => Severity.Low;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var node in context.Graph.Nodes.Where(n => n.Archetype is Archetypes.Queue or Archetypes.Stream))
        {
            if (!node.Cfg.Bool("observability.alarms"))
                yield return context.Create(this, $"\"{node.Label}\" não tem alarme de backlog: um consumidor parado só é notado quando o cliente reclama.", [node.Id]);
        }
    }
}
