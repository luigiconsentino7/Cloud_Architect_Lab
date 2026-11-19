using Lab.Ir.Challenges;
using Lab.Rules;
using Lab.Simulation;

namespace Lab.Scoring;

/// <summary>
/// Achado de regra cruzado com a simulação. <see cref="Observed"/>: o efeito previsto apareceu em algum cenário.
/// <see cref="Confirmed"/>: apareceu e derrubou uma verificação nesse cenário — aí a regra não é penalizada de novo.
/// </summary>
public sealed record AssessedFinding(
    Finding Finding,
    bool Observed,
    bool Confirmed,
    IReadOnlyList<string> ScenarioIds,
    double Points);

/// <summary>Liga as chaves <c>ConfirmedBy</c> das regras (ex.: <c>load.throttled</c>) aos resultados dos cenários.</summary>
public static class EvidenceMatcher
{
    public static (bool Observed, bool Confirmed, IReadOnlyList<string> ScenarioIds) Match(
        Finding finding, ChallengeSpec challenge, SimulationReport report, IReadOnlyList<CheckResult> checks)
    {
        var observedIn = new SortedSet<string>(StringComparer.Ordinal);
        var confirmedIn = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var key in finding.ConfirmedBy)
        {
            foreach (var scenario in report.Scenarios)
            {
                var spec = challenge.TestPlan.Find(scenario.ScenarioId);
                if (spec is null || !Applies(key, spec) || !Shows(key, finding, scenario)) continue;
                observedIn.Add(scenario.ScenarioId);
                if (checks.Any(c => c.ScenarioId == scenario.ScenarioId && !c.Passed && Relevant(key, c)))
                    confirmedIn.Add(scenario.ScenarioId);
            }
        }

        var scenarios = (confirmedIn.Count > 0 ? confirmedIn : observedIn).ToList();
        return (observedIn.Count > 0, confirmedIn.Count > 0, scenarios);
    }

    /// <summary>O cenário é do tipo que a chave descreve (ex.: <c>chaos.zone-outage.*</c> só em cenários com queda de zona).</summary>
    private static bool Applies(string key, ScenarioSpec spec) => key switch
    {
        _ when key.StartsWith("load.", StringComparison.Ordinal) => spec.Kind == ScenarioKind.Load,
        _ when key.StartsWith("chaos.zone-outage", StringComparison.Ordinal) =>
            spec.Faults.Any(f => f.Type is FaultType.ZoneOutage or FaultType.RegionOutage or FaultType.InstanceKill),
        _ when key.StartsWith("chaos.dependency", StringComparison.Ordinal) =>
            spec.Faults.Any(f => f.Type is FaultType.LatencyInjection or FaultType.DependencyOutage or FaultType.Throttling),
        _ when key.StartsWith("chaos.", StringComparison.Ordinal) => spec.Kind == ScenarioKind.Chaos,
        _ => true,
    };

    /// <summary>O efeito previsto pela regra aparece no cenário. Efeitos de nó exigem um dos nós do achado.</summary>
    private static bool Shows(string key, Finding finding, ScenarioResult scenario)
    {
        bool AnyNode(Func<NodeResult, bool> predicate) => finding.NodeIds.Count == 0
            ? scenario.Nodes.Values.Any(predicate)
            : finding.NodeIds.Any(id => scenario.Nodes.TryGetValue(id, out var node) && predicate(node));

        return key switch
        {
            "load.throttled" => AnyNode(n => n.Throttled > 0 || n.Rejected > 0),
            "load.connection-errors" => AnyNode(n => n.ConnectionErrors > 0),
            "integrity.lostEvents" => scenario.Integrity.LostEvents > 0,
            "integrity.duplicateEffects" => scenario.Integrity.DuplicateEffects > 0,
            "integrity.orderingViolations" => scenario.Integrity.OrderingViolations > 0,
            "chaos.data-loss" => scenario.Facts.Contains("data-loss"),
            _ when key.EndsWith(".failed", StringComparison.Ordinal) => true,
            _ => scenario.Facts.Contains(key[(key.IndexOf('.') + 1)..]),
        };
    }

    /// <summary>A regra fala do mesmo assunto da verificação (ex.: duplicidade com a verificação de efeitos duplicados).</summary>
    public static bool Explains(Finding finding, CheckResult check) => finding.ConfirmedBy.Any(k => Relevant(k, check));

    /// <summary>A verificação reprovada é do mesmo assunto da chave (integridade com integridade).</summary>
    private static bool Relevant(string key, CheckResult check) => key switch
    {
        "integrity.duplicateEffects" => check.Metric == "duplicate-effects",
        "integrity.lostEvents" => check.Metric == "lost-events",
        "integrity.orderingViolations" => check.Metric == "ordering-violations",
        _ => true,
    };
}
