using Lab.Ir;
using Lab.Ir.Flows;

namespace Lab.Rules.Rules;

/// <summary>ARQ-001 — ponto único de falha no caminho síncrono de um caso de uso crítico.</summary>
public sealed class SinglePointOfFailureRule : Rule
{
    public override string Id => "ARQ-001";
    public override Dimension Dimension => Dimension.Resilience;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["chaos.zone-outage.failed"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.CriticalUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            foreach (var call in plan.SyncHappyPath)
            {
                var node = call.Target;
                if (!reported.Add(node.Id)) continue;
                var reason = Reason(node);
                if (reason is null) continue;
                yield return context.Create(this, $"\"{node.Label}\" é um ponto único de falha no caminho de {useCase.Id}: {reason}.",
                    [node.Id], useCase: useCase.Id,
                    severity: node.Archetype == Archetypes.Cache ? Severity.Medium : null);
            }
        }
    }

    private static string? Reason(Node node)
    {
        var cfg = node.Cfg;
        return node.Archetype switch
        {
            Archetypes.Service when cfg.Int("instances.min", 1) < 2 => "roda com uma instância só",
            Archetypes.Service or Archetypes.StreamProcessor when node.Placement.Zones.Count < 2 => "está em uma única zona de disponibilidade",
            Archetypes.Relational when !cfg.Bool("multiAz") => "o banco não tem standby em outra AZ (multi-AZ desligado)",
            Archetypes.Cache when cfg.Int("replicas", 0) == 0 || !cfg.Bool("multiAz") => "o cache não tem réplica em outra AZ",
            _ => null,
        };
    }
}

/// <summary>ARQ-002 — chamada síncrona sem timeout, ou com timeout maior que o SLO.</summary>
public sealed class MissingTimeoutRule : Rule
{
    public override string Id => "ARQ-002";
    public override Dimension Dimension => Dimension.Resilience;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["chaos.dependency.failed"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.CriticalUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            var budget = context.LatencyBudgetMs(useCase.Id);
            foreach (var call in plan.SyncHappyPath.Where(IsRemoteSyncCall))
            {
                var edge = call.Edge!;
                if (!reported.Add(edge.Id)) continue;
                var target = call.Target.Label;
                var source = call.Parent!.Target.Label;
                if (edge.Config.TimeoutMs is null)
                {
                    yield return context.Create(this, $"A chamada \"{source}\" → \"{target}\" não tem timeout: se \"{target}\" ficar lento, as requisições se acumulam.",
                        edges: [edge.Id], useCase: useCase.Id);
                }
                else if (budget is { } limit && edge.Config.TimeoutMs > limit)
                {
                    yield return context.Create(this, $"O timeout de \"{source}\" → \"{target}\" ({edge.Config.TimeoutMs:0} ms) é maior que o SLO de {useCase.Id} ({limit:0} ms).",
                        edges: [edge.Id], useCase: useCase.Id, severity: Severity.Medium);
                }
            }
        }
    }

    /// <summary>Chamada síncrona para computação ou sistema externo (proxies gerenciados de borda não contam).</summary>
    internal static bool IsRemoteSyncCall(CallNode call) =>
        call.Edge!.Interaction == Interaction.SyncRequest
        && !Archetypes.IsClient(call.Parent!.Target.Archetype)
        && (Archetypes.IsCompute(call.Target.Archetype) || Archetypes.IsExternal(call.Target.Archetype));
}

/// <summary>ARQ-003 — retry sem backoff com jitter, ou retries × timeout acima do SLO.</summary>
public sealed class RetryPolicyRule : Rule
{
    public override string Id => "ARQ-003";
    public override Dimension Dimension => Dimension.Resilience;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.CriticalUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            var budget = context.LatencyBudgetMs(useCase.Id);
            foreach (var call in plan.SyncHappyPath.Where(MissingTimeoutRule.IsRemoteSyncCall))
            {
                var edge = call.Edge!;
                var retries = edge.Config.Retries;
                if (retries is null || retries.Max == 0 || !reported.Add(edge.Id)) continue;
                var label = $"\"{call.Parent!.Target.Label}\" → \"{call.Target.Label}\"";
                if (retries.Backoff != BackoffKind.ExponentialJitter)
                {
                    yield return context.Create(this, $"Os retries de {label} não usam backoff exponencial com jitter: numa falha, todos tentam de novo ao mesmo tempo (retry storm).",
                        edges: [edge.Id], useCase: useCase.Id);
                }

                if (budget is { } limit && edge.Config.TimeoutMs is { } timeout && (retries.Max + 1) * timeout > limit)
                {
                    yield return context.Create(this, $"Em {label}, {retries.Max + 1} tentativas × {timeout:0} ms passam do SLO de {useCase.Id} ({limit:0} ms): o cliente desiste antes do último retry.",
                        edges: [edge.Id], useCase: useCase.Id);
                }
            }
        }
    }
}

/// <summary>ARQ-004 — fila ou tópico sem DLQ.</summary>
public sealed class MissingDlqRule : Rule
{
    public override string Id => "ARQ-004";
    public override Dimension Dimension => Dimension.Resilience;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var node in context.Graph.Nodes.Where(n => n.Archetype is Archetypes.Queue or Archetypes.Topic))
        {
            var hasConsumers = context.Graph.Outgoing(node.Id).Any(e => e.Interaction is Interaction.AsyncConsume or Interaction.AsyncPublish);
            if (hasConsumers && !node.Cfg.Bool("dlq.enabled"))
            {
                yield return context.Create(this, $"\"{node.Label}\" não tem DLQ: uma mensagem que sempre falha fica sendo reprocessada para sempre (ou se perde, conforme o serviço).",
                    [node.Id]);
            }
        }
    }
}

/// <summary>ARQ-005 — dependência externa no caminho crítico sem circuit breaker nem caminho de fallback.</summary>
public sealed class ExternalDependencyRule : Rule
{
    public override string Id => "ARQ-005";
    public override Dimension Dimension => Dimension.Resilience;
    public override Severity DefaultSeverity => Severity.High;
    public override IReadOnlyList<string> ConfirmedBy => ["chaos.dependency.failed"];

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.CriticalUseCases)
        {
            var plan = context.PlanFor(useCase);
            if (plan is null) continue;
            foreach (var call in plan.SyncHappyPath.Where(c => Archetypes.IsExternal(c.Target.Archetype)))
            {
                var edge = call.Edge!;
                if (!reported.Add(edge.Id)) continue;
                var hasBreaker = edge.Config.CircuitBreaker?.Enabled == true;
                var hasFallback = plan.Calls.Any(c => c.Trigger == TriggerKind.OnFailure && c.Edge!.Config.TriggerOf == edge.Id);
                if (!hasBreaker && !hasFallback)
                {
                    yield return context.Create(this, $"\"{call.Target.Label}\" está no caminho síncrono de {useCase.Id} sem circuit breaker nem fallback: se ele degradar, o seu sistema degrada junto.",
                        [call.Target.Id], [edge.Id], useCase.Id);
                }
            }
        }
    }
}
