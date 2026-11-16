using Lab.Ir;
using Lab.Ir.Flows;

namespace Lab.Rules.Rules;

/// <summary>DAT-001 — dual-write: grava no banco e publica evento sem outbox nem CDC.</summary>
public sealed class DualWriteRule : Rule
{
    public override string Id => "DAT-001";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.Critical;
    public override IReadOnlyList<string> ConfirmedBy => ["integrity.lostEvents"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.Challenge.UseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            foreach (var node in plan.HappyPath.Select(c => c.Target).Append(plan.Root?.Target).OfType<Node>().DistinctBy(n => n.Id))
            {
                var calls = plan.HappyPath.Where(c => c.Parent?.Target.Id == node.Id).ToList();
                var write = calls.FirstOrDefault(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasWrite && Archetypes.IsDurableData(c.Target.Archetype));
                var publish = calls.FirstOrDefault(c => c.Edge!.Interaction == Interaction.AsyncPublish && c.Edge.Config.Delivery == DeliveryMode.Direct);
                if (write is null || publish is null || !reported.Add(node.Id)) continue;
                yield return context.Create(this,
                    $"\"{node.Label}\" grava em \"{write.Target.Label}\" e publica em \"{publish.Target.Label}\" como duas operações separadas: se o processo cair entre as duas, o evento se perde (ou sai sem o registro). Use outbox transacional ou CDC.",
                    [node.Id], [write.Edge!.Id, publish.Edge!.Id], useCase.Id,
                    severity: useCase.Monetary ? Severity.Critical : Severity.High);
            }
        }
    }
}

/// <summary>DAT-002 — consumidor com efeito colateral sem idempotência.</summary>
public sealed class IdempotentConsumerRule : Rule
{
    public override string Id => "DAT-002";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.Critical;
    public override IReadOnlyList<string> ConfirmedBy => ["integrity.duplicateEffects"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.Challenge.UseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            foreach (var consume in plan.HappyPath.Where(c => c.Edge!.Interaction == Interaction.AsyncConsume))
            {
                var consumer = consume.Target;
                var effects = consume.Children.Where(c => IsSideEffect(context, c)).ToList();
                if (effects.Count == 0 || IsProtected(context, consumer, effects) || !reported.Add(consumer.Id)) continue;
                var touchesMoney = useCase.Monetary && effects.Any(e => e.Edge!.Interaction == Interaction.DataAccess);
                yield return context.Create(this,
                    $"\"{consumer.Label}\" consome mensagens entregues pelo menos uma vez e aplica efeitos ({string.Join(", ", effects.Select(e => e.Target.Label))}) sem deduplicar: reentregas viram efeitos duplicados.",
                    [consumer.Id], effects.Select(e => e.Edge!.Id), useCase.Id,
                    severity: touchesMoney ? Severity.Critical : Severity.Medium);
            }
        }
    }

    internal static bool IsSideEffect(RuleContext context, CallNode call) =>
        (call.Edge!.Interaction == Interaction.DataAccess && call.Edge.Config.HasWrite && Archetypes.IsDurableData(call.Target.Archetype))
        || (Archetypes.IsExternal(call.Target.Archetype) && context.Challenge.FindExternal(call.Target.Id) is { SideEffect: true, Idempotent: false });

    private static bool IsProtected(RuleContext context, Node consumer, List<CallNode> effects)
    {
        if (consumer.Cfg.Bool("idempotency.enabled"))
        {
            var store = consumer.Cfg.String("idempotency.storeNodeId");
            var reachable = store is not null && context.Graph.Outgoing(consumer.Id).Any(e => e.To == store && e.Interaction == Interaction.DataAccess);
            if (reachable || store is null) return true;
        }

        return effects.All(e => e.Edge!.Interaction == Interaction.DataAccess && e.Edge.Config.HasAtomicWrite);
    }
}

/// <summary>DAT-003 — API de comando monetário sem chave de idempotência.</summary>
public sealed class IdempotencyKeyRule : Rule
{
    public override string Id => "DAT-003";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.Critical;
    public override IReadOnlyList<string> ConfirmedBy => ["integrity.duplicateEffects"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var useCase in context.MonetaryUseCases)
        {
            var entry = context.PlanFor(useCase)?.Root?.Children.FirstOrDefault();
            if (entry is null || entry.Edge!.Config.IdempotencyKey) continue;
            yield return context.Create(this,
                $"{useCase.Id} é um comando monetário e a chamada de entrada não carrega chave de idempotência: um retry do cliente (timeout, rede) executa a operação duas vezes.",
                edges: [entry.Edge.Id], useCase: useCase.Id);
        }
    }
}

/// <summary>DAT-004 — caso de uso exige ordem por chave, mas o broker não garante.</summary>
public sealed class OrderingRule : Rule
{
    public override string Id => "DAT-004";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["integrity.orderingViolations"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var useCase in context.Challenge.UseCases.Where(u => u.RequiresOrdering))
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            foreach (var broker in plan.HappyPath.Select(c => c.Target).Where(n => Archetypes.IsMessaging(n.Archetype)).DistinctBy(n => n.Id))
            {
                // Consumidores que descartam sequência antiga tornam o processamento comutativo: a ordem do broker deixa de importar.
                var consumers = plan.Calls.Where(c => c.Parent?.Target.Id == broker.Id && c.Edge!.Interaction == Interaction.AsyncConsume).ToList();
                var sequenceChecked = consumers.Count > 0 && consumers.All(c => c.Target.Cfg.Bool("idempotency.sequenceCheck"));
                if (!GuaranteesOrder(context, broker) && !sequenceChecked)
                {
                    yield return context.Create(this,
                        $"{useCase.Id} exige ordem por chave, mas \"{broker.Label}\" não garante ordem: consumidores em paralelo podem processar eventos da mesma conta fora de ordem.",
                        [broker.Id], useCase: useCase.Id);
                }
            }
        }
    }

    internal static bool GuaranteesOrder(RuleContext context, Node broker)
    {
        var ordering = context.Definition(broker).Capability("ordering");
        return ordering is "per-key" or "total" || broker.Cfg.Bool("sessions");
    }
}

/// <summary>DAT-005 — atualização de saldo sem controle de concorrência.</summary>
public sealed class AtomicBalanceRule : Rule
{
    public override string Id => "DAT-005";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["integrity.duplicateEffects"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var useCase in context.MonetaryUseCases)
        {
            if (!useCase.RequiredCapabilities.Any(c => c.Capability == "atomic-balance-update")) continue;
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            var writes = plan.HappyPath
                .Where(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasWrite && Archetypes.IsDurableData(c.Target.Archetype))
                .ToList();
            if (writes.Count > 0 && !writes.Any(w => w.Edge!.Config.HasAtomicWrite))
            {
                yield return context.Create(this,
                    $"{useCase.Id} atualiza saldo com escrita simples em \"{writes[0].Target.Label}\": duas operações concorrentes na mesma conta podem gastar o mesmo saldo. Use transação ou escrita condicional.",
                    [writes[0].Target.Id], writes.Select(w => w.Edge!.Id), useCase.Id);
            }
        }
    }
}

/// <summary>DAT-006 — cache usado como fonte da verdade num fluxo monetário.</summary>
public sealed class CacheAsSourceOfTruthRule : Rule
{
    public override string Id => "DAT-006";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.High;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var useCase in context.MonetaryUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            var cacheWrite = plan.HappyPath.FirstOrDefault(c => c.Target.Archetype == Archetypes.Cache && c.Edge!.Config.HasWrite);
            var durableWrite = plan.HappyPath.Any(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasWrite && Archetypes.IsDurableData(c.Target.Archetype));
            if (cacheWrite is not null && !durableWrite)
            {
                yield return context.Create(this,
                    $"{useCase.Id} grava o estado monetário só em \"{cacheWrite.Target.Label}\" (cache): uma reinicialização ou expulsão de chave perde dinheiro.",
                    [cacheWrite.Target.Id], [cacheWrite.Edge!.Id], useCase.Id);
            }
        }
    }
}

/// <summary>DAT-008 — RPO zero com replicação assíncrona.</summary>
public sealed class RpoRule : Rule
{
    public override string Id => "DAT-008";
    public override Dimension Dimension => Dimension.Consistency;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["chaos.data-loss"];

    public override bool AppliesTo(Lab.Ir.Challenges.ChallengeSpec challenge) => challenge.Constraints.RpoSeconds is 0;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var stores = context.MonetaryUseCases
            .Select(context.PlanFor)
            .OfType<FlowPlan>()
            .SelectMany(p => p.HappyPath)
            .Where(c => c.Target.Archetype == Archetypes.Relational && c.Edge!.Config.HasWrite)
            .Select(c => c.Target)
            .DistinctBy(n => n.Id);
        foreach (var store in stores.Where(s => !s.Cfg.Bool("multiAz")))
        {
            yield return context.Create(this,
                $"O desafio exige RPO = 0, mas \"{store.Label}\" não tem standby síncrono (multi-AZ): uma falha de AZ perde as últimas transações.",
                [store.Id]);
        }
    }
}
