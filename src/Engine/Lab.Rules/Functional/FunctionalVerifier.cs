using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;
using Lab.Rules.Rules;

namespace Lab.Rules.Functional;

public enum FunctionalStatus
{
    Passed,
    Partial,
    Failed,
}

public sealed record CapabilityCheck(
    string Capability,
    string? Target,
    bool Satisfied,
    string Detail,
    IReadOnlyList<string> NodeIds,
    IReadOnlyList<string> EdgeIds);

public sealed record UseCaseFunctionalResult
{
    public required string UseCaseId { get; init; }
    public required string Name { get; init; }
    public FunctionalStatus Status { get; init; }
    public bool PathOk { get; init; }
    public IReadOnlyList<string> PathProblems { get; init; } = [];
    public IReadOnlyList<CapabilityCheck> Capabilities { get; init; } = [];
    public IReadOnlyList<CapabilityCheck> NegativePaths { get; init; } = [];

    /// <summary>0..1 — caminho 40%, capacidades 40%, caminhos de falha 20% (redistribuído quando não há).</summary>
    public double Score { get; init; }
}

/// <summary>Testes funcionais: cada caso de uso tem caminho, cumpre as capacidades exigidas e trata as falhas pedidas.</summary>
public static class FunctionalVerifier
{
    public static IReadOnlyList<UseCaseFunctionalResult> Verify(RuleContext context) =>
        context.Challenge.UseCases.Select(u => Verify(context, u)).ToList();

    public static UseCaseFunctionalResult Verify(RuleContext context, UseCaseSpec useCase)
    {
        var plan = context.PlanFor(useCase)!;
        var problems = plan.Problems.ToList();
        if (plan.Root is not null && plan.Calls.Count > 0 && !plan.Calls.Any(c => c.InSyncSegment && c.Edge!.Interaction == Interaction.SyncRequest))
            problems.Add($"{useCase.Id}: o cliente não faz nenhuma chamada síncrona.");
        var pathOk = plan.IsValid && problems.Count == 0;

        var capabilities = useCase.RequiredCapabilities.Select(r => Check(context, plan, r)).ToList();
        var negatives = useCase.NegativePaths.Select(n => CheckNegative(context, plan, n)).ToList();

        var capabilityScore = capabilities.Count == 0 ? 1 : capabilities.Count(c => c.Satisfied) / (double)capabilities.Count;
        var negativeScore = negatives.Count == 0 ? 1 : negatives.Count(c => c.Satisfied) / (double)negatives.Count;
        var score = !pathOk ? 0
            : negatives.Count == 0 ? (0.5 + (0.5 * capabilityScore))
            : 0.4 + (0.4 * capabilityScore) + (0.2 * negativeScore);

        var status = !pathOk ? FunctionalStatus.Failed
            : capabilities.All(c => c.Satisfied) && negatives.All(c => c.Satisfied) ? FunctionalStatus.Passed
            : FunctionalStatus.Partial;

        return new UseCaseFunctionalResult
        {
            UseCaseId = useCase.Id,
            Name = useCase.Name,
            Status = status,
            PathOk = pathOk,
            PathProblems = problems,
            Capabilities = capabilities,
            NegativePaths = negatives,
            Score = Math.Round(score, 4),
        };
    }

    private static CapabilityCheck Check(RuleContext context, FlowPlan plan, CapabilityRequirement requirement)
    {
        var mustBeSync = requirement.Before == "respond";
        var calls = (mustBeSync ? plan.SyncHappyPath : plan.HappyPath).ToList();
        var where = mustBeSync ? " antes da resposta ao cliente" : "";

        CapabilityCheck Result(bool ok, string detail, IEnumerable<CallNode>? evidence = null) => new(
            requirement.Capability, requirement.Target, ok, detail,
            evidence?.Select(c => c.Target.Id).Distinct().ToList() ?? [],
            evidence?.Select(c => c.Edge!.Id).Distinct().ToList() ?? []);

        switch (requirement.Capability)
        {
            case "authenticate":
            {
                var auth = calls.Where(c =>
                    (c.Target.Archetype == Archetypes.ApiGateway && c.Target.Cfg.String("auth") is { } mode && mode != "none")
                    || c.Target.Archetype == Archetypes.Identity
                    || c.Target.Responsibilities.Contains("authenticate")).ToList();
                return auth.Count > 0
                    ? Result(true, $"Autenticação em \"{auth[0].Target.Label}\".", auth)
                    : Result(false, $"Nenhum passo autentica o cliente{where} (gateway com autenticação, provedor de identidade ou serviço com a responsabilidade).");
            }

            case "idempotency-check":
            {
                var entry = plan.Root?.Children.FirstOrDefault();
                var atomic = calls.Where(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasAtomicWrite && Archetypes.IsDurableData(c.Target.Archetype)).ToList();
                if (entry?.Edge!.Config.IdempotencyKey != true)
                    return Result(false, "A chamada de entrada não carrega chave de idempotência.");
                return atomic.Count > 0
                    ? Result(true, $"Chave de idempotência verificada com escrita atômica em \"{atomic[0].Target.Label}\".", atomic.Prepend(entry))
                    : Result(false, $"A chave de idempotência chega, mas nenhum passo{where} faz escrita condicional ou transação para verificá-la.", [entry]);
            }

            case "durable-write":
            {
                var writes = calls.Where(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasWrite && Archetypes.IsDurableData(c.Target.Archetype)).ToList();
                return writes.Count > 0
                    ? Result(true, $"Gravação durável em \"{writes[0].Target.Label}\"{where}.", writes)
                    : Result(false, $"Nenhuma gravação em armazenamento durável{where} (cache não conta).");
            }

            case "atomic-balance-update":
            {
                var atomic = calls.Where(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasAtomicWrite
                    && (context.Definition(c.Target).Has("transactions") || context.Definition(c.Target).Has("conditionalWrites"))).ToList();
                return atomic.Count > 0
                    ? Result(true, $"Saldo atualizado com operação atômica em \"{atomic[0].Target.Label}\".", atomic)
                    : Result(false, $"Nenhuma transação ou escrita condicional{where} para atualizar o saldo.");
            }

            case "publish-event":
            {
                var publish = calls.Where(c => c.Edge!.Interaction is Interaction.AsyncPublish or Interaction.Cdc).ToList();
                return publish.Count > 0
                    ? Result(true, $"Evento publicado em \"{publish[0].Target.Label}\".", publish)
                    : Result(false, "Nenhum evento é publicado (fila, tópico, stream ou CDC).");
            }

            case "external-call":
            {
                var target = requirement.Target ?? "";
                var external = calls.Where(c => c.Target.Id == target && c.Edge!.Interaction == Interaction.SyncRequest).ToList();
                var name = context.Challenge.FindExternal(target)?.Name ?? target;
                return external.Count > 0
                    ? Result(true, $"Chamada a {name}.", external)
                    : Result(false, $"O fluxo não chama {name}{where}.");
            }

            case "notify":
            {
                var notify = calls.Where(c => (Archetypes.IsExternal(c.Target.Archetype) && context.Challenge.FindExternal(c.Target.Id)?.SideEffect == true)
                    || c.Target.Responsibilities.Contains("notify")).ToList();
                return notify.Count > 0
                    ? Result(true, $"Notificação enviada por \"{notify[0].Target.Label}\".", notify)
                    : Result(false, "Nenhum passo envia a notificação.");
            }

            case "audit-log":
            {
                var audit = calls.Where(c => c.Edge!.Interaction == Interaction.DataAccess && c.Edge.Config.HasWrite && AuditTrailRule.IsImmutable(context, c.Target)).ToList();
                return audit.Count > 0
                    ? Result(true, $"Trilha de auditoria imutável em \"{audit[0].Target.Label}\".", audit)
                    : Result(false, "Nenhuma gravação em armazenamento imutável (append-only/WORM) para auditoria.");
            }

            case "read-from-cache":
            {
                var cache = calls.Where(c => c.Target.Archetype == Archetypes.Cache).ToList();
                return cache.Count > 0
                    ? Result(true, $"Leitura servida por \"{cache[0].Target.Label}\".", cache)
                    : Result(false, "O fluxo não lê de um cache.");
            }

            default:
            {
                var responsible = calls.Where(c => c.Target.Responsibilities.Contains(requirement.Capability)).ToList();
                var description = requirement.Description ?? requirement.Capability;
                return responsible.Count > 0
                    ? Result(true, $"\"{responsible[0].Target.Label}\" cumpre: {description}.", responsible)
                    : Result(false, $"Nenhum componente do fluxo{where} tem a responsabilidade \"{description}\" marcada no inspector.");
            }
        }
    }

    private static CapabilityCheck CheckNegative(RuleContext context, FlowPlan plan, NegativePathRequirement requirement)
    {
        var dependencyCalls = plan.Calls.Where(c => c.Target.Id == requirement.Dependency && c.OnHappyPath).ToList();
        var name = context.Challenge.FindExternal(requirement.Dependency)?.Name ?? context.Label(requirement.Dependency);
        if (dependencyCalls.Count == 0)
            return new CapabilityCheck($"fallback:{requirement.Dependency}", requirement.Dependency, false, $"O fluxo não chama {name}.", [], []);

        var ids = dependencyCalls.Select(c => c.Edge!.Id).ToHashSet(StringComparer.Ordinal);
        var fallbacks = plan.Calls.Where(c => c.Trigger == TriggerKind.OnFailure && c.Edge!.Config.TriggerOf is { } of && ids.Contains(of)).ToList();
        var detail = fallbacks.Count > 0
            ? $"Se {name} falhar, o fluxo segue por \"{fallbacks[0].Target.Label}\"."
            : $"Não há caminho para quando {name} falhar: {requirement.Description}".TrimEnd(' ', ':');
        return new CapabilityCheck($"fallback:{requirement.Dependency}", requirement.Dependency, fallbacks.Count > 0, detail,
            fallbacks.Select(f => f.Target.Id).ToList(), fallbacks.Select(f => f.Edge!.Id).Concat(ids).ToList());
    }
}
