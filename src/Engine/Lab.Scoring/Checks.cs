using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Rules;
using Lab.Simulation;

namespace Lab.Scoring;

public enum CheckStatus
{
    Passed,
    Failed,
}

/// <summary>Um par (SLO, cenário) avaliado: o que foi observado, o alvo e a pontuação parcial (0..1).</summary>
public sealed record CheckResult
{
    public required string Id { get; init; }
    public required string SloId { get; init; }
    public required string ScenarioId { get; init; }
    public string ScenarioName { get; init; } = "";
    public ScenarioKind ScenarioKind { get; init; }
    public Visibility Visibility { get; init; }
    public string? UseCaseId { get; init; }
    public required string Metric { get; init; }

    /// <summary>Nulo quando não há dado (ex.: latência de um caso de uso sem nenhuma requisição bem-sucedida).</summary>
    public double? Observed { get; init; }

    public double Target { get; init; }

    /// <summary>Alvo é um mínimo (taxa de sucesso); senão, um máximo.</summary>
    public bool IsMinimum { get; init; }

    public CheckStatus Status { get; init; }
    public double Score { get; init; }
    public Dimension Dimension { get; init; }
    public double Weight { get; init; } = 1;

    /// <summary>Violação de integridade que envolve dinheiro (dispara a trava de 60).</summary>
    public bool MonetaryViolation { get; init; }

    public string Description { get; init; } = "";

    public bool Passed => Status == CheckStatus.Passed;

    public bool IsIntegrity => SloEvaluator.IsIntegrityMetric(Metric);
}

/// <summary>Transforma SLOs × cenários simulados em verificações, com as fórmulas da spec 05 (Etapa 7).</summary>
public static class SloEvaluator
{
    /// <summary>Utilização média acima disto no baseline deixa pouca folga para variações normais de carga.</summary>
    public const double HeadroomTarget = 0.75;

    public static bool IsIntegrityMetric(string metric) => metric is "duplicate-effects" or "lost-events" or "ordering-violations";

    public static IReadOnlyList<CheckResult> Evaluate(ChallengeSpec challenge, SimulationReport report, ArchitectureGraph? graph = null) =>
        EngineCulture.Run(() => EvaluateCore(challenge, report, graph));

    private static IReadOnlyList<CheckResult> EvaluateCore(ChallengeSpec challenge, SimulationReport report, ArchitectureGraph? graph)
    {
        var checks = new List<CheckResult>();
        foreach (var slo in challenge.TestPlan.Slos.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            foreach (var scenarioId in slo.Scenarios)
            {
                var spec = challenge.TestPlan.Find(scenarioId);
                var result = report.Find(scenarioId);
                if (spec is null || result is null) continue;
                checks.Add(Evaluate(challenge, slo, spec, result));
            }
        }

        if (graph is not null && Headroom(challenge, report, graph) is { } headroom) checks.Add(headroom);
        return checks;
    }

    /// <summary>Pontuação de um SLO de máximo (latência, tempo assíncrono): 2× o limite → 0.</summary>
    public static double MaxScore(double observed, double limit) =>
        observed <= limit ? 1 : limit <= 0 ? 0 : Math.Max(0, 1 - ((observed - limit) / limit));

    /// <summary>Pontuação de taxa de sucesso: 10× o orçamento de erro → 0,5; 100× → 0.</summary>
    public static double SuccessScore(double observed, double target)
    {
        var budget = Math.Max(1e-9, 1 - target);
        var error = Math.Max(0, 1 - observed);
        return error <= budget ? 1 : Math.Max(0, 1 - (Math.Log10(error / budget) / 2));
    }

    /// <summary>Integridade: UC monetário é tudo ou nada; nos demais, 0,1% das requisições com violação → 0.</summary>
    public static double IntegrityScore(double violations, double allowed, bool monetary, long requests)
    {
        var excess = Math.Max(0, violations - allowed);
        if (excess <= 0) return 1;
        if (monetary || requests <= 0) return 0;
        return Math.Max(0, 1 - (excess / (0.001 * requests)));
    }

    private static CheckResult Evaluate(ChallengeSpec challenge, SloSpec slo, ScenarioSpec spec, ScenarioResult scenario)
    {
        var useCase = slo.UseCase is null ? null : challenge.FindUseCase(slo.UseCase);
        var ucResult = slo.UseCase is null ? null : scenario.UseCases.GetValueOrDefault(slo.UseCase);
        var metric = slo.Metric;
        double? observed;
        double score;
        var isMinimum = false;
        var monetaryViolation = false;
        var target = slo.Max ?? slo.Min ?? 0;

        if (metric.StartsWith("latency.", StringComparison.Ordinal))
        {
            observed = ucResult is { Succeeded: > 0 } ? Percentile(ucResult.Latency, metric) : null;
            score = observed is { } value ? MaxScore(value, target) : 0;
        }
        else if (metric.StartsWith("async-completion.", StringComparison.Ordinal))
        {
            // Sem parte assíncrona no fluxo, a conclusão acontece dentro da própria resposta.
            observed = ucResult switch
            {
                { AsyncCompletion: { } async } when ucResult.AsyncCompleted > 0 => Percentile(async, metric),
                { AsyncExpected: 0, Succeeded: > 0 } => Percentile(ucResult.Latency, metric),
                _ => null,
            };
            score = observed is { } value ? MaxScore(value, target) : 0;
        }
        else if (metric == "success-rate")
        {
            isMinimum = true;
            observed = ucResult is { Requests: > 0 } ? ucResult.SuccessRate : null;
            score = observed is { } value ? SuccessScore(value, target) : 0;
        }
        else if (IsIntegrityMetric(metric))
        {
            var integrity = scenario.Integrity;
            observed = metric switch
            {
                "duplicate-effects" => integrity.DuplicateEffects,
                "lost-events" => integrity.LostEvents,
                _ => integrity.OrderingViolations,
            };

            // Duplicidade monetária é medida no efeito (débito, transferência), não no UC do SLO.
            var monetary = metric == "duplicate-effects" ? integrity.MonetaryDuplicates > 0 : useCase?.Monetary == true;
            var requests = ucResult?.Requests ?? scenario.UseCases.Values.Sum(u => u.Requests);
            score = IntegrityScore(observed.Value, target, monetary, requests);
            monetaryViolation = monetary && observed > target;
        }
        else
        {
            observed = null;
            score = 0;
        }

        var passed = observed is { } o && (isMinimum ? o >= target : o <= target);
        var dimension = IsIntegrityMetric(metric) ? Dimension.Consistency : DimensionOf(spec.Kind);
        return new CheckResult
        {
            Id = $"{slo.Id}@{spec.Id}",
            SloId = slo.Id,
            ScenarioId = spec.Id,
            ScenarioName = string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name,
            ScenarioKind = spec.Kind,
            Visibility = spec.Visibility,
            UseCaseId = slo.UseCase,
            Metric = metric,
            Observed = observed is { } r ? Math.Round(r, 6) : null,
            Target = target,
            IsMinimum = isMinimum,
            Status = passed ? CheckStatus.Passed : CheckStatus.Failed,
            Score = passed ? 1 : Math.Round(score, 4),
            Dimension = dimension,
            Weight = useCase is { Critical: false } ? 1 : 2,
            MonetaryViolation = monetaryViolation,
            Description = Describe(slo, observed, target, isMinimum),
        };
    }

    /// <summary>Folga de capacidade no baseline (Performance): o componente com capacidade fixa mais ocupado.</summary>
    private static CheckResult? Headroom(ChallengeSpec challenge, SimulationReport report, ArchitectureGraph graph)
    {
        var spec = BaselineScenario(challenge);
        if (spec is null || report.Find(spec.Id) is not { } scenario) return null;
        var busiest = scenario.Nodes.Values
            .Where(n => graph.FindNode(n.NodeId) is { } node && HasProvisionedCapacity(node.Archetype))
            .OrderByDescending(n => n.UtilizationAvg)
            .ThenBy(n => n.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (busiest is null) return null;

        var observed = busiest.UtilizationAvg;
        var passed = observed <= HeadroomTarget;
        return new CheckResult
        {
            Id = $"HEADROOM@{spec.Id}",
            SloId = "HEADROOM",
            ScenarioId = spec.Id,
            ScenarioName = string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name,
            ScenarioKind = spec.Kind,
            Visibility = spec.Visibility,
            Metric = "capacity.headroom",
            Observed = Math.Round(observed, 4),
            Target = HeadroomTarget,
            Status = passed ? CheckStatus.Passed : CheckStatus.Failed,
            Score = passed ? 1 : Math.Round(Math.Max(0, 1 - ((observed - HeadroomTarget) / (1 - HeadroomTarget))), 4),
            Dimension = Dimension.Performance,
            Weight = 1,
            Description = $"\"{busiest.Label}\" com {Fmt.Pct(observed)} de utilização média (folga recomendada: até {Fmt.Pct(HeadroomTarget)})",
        };
    }

    /// <summary>O cenário de carga de referência: <c>baseline</c>, ou o primeiro público de carga constante.</summary>
    public static ScenarioSpec? BaselineScenario(ChallengeSpec challenge) =>
        challenge.TestPlan.Find("baseline")
        ?? challenge.TestPlan.Scenarios.FirstOrDefault(s => s is { Kind: ScenarioKind.Load, Visibility: Visibility.Public, Load.Profile: LoadShape.Constant })
        ?? challenge.TestPlan.Scenarios.FirstOrDefault(s => s.Kind == ScenarioKind.Load);

    /// <summary>O pico público usado no custo: o cenário de carga público de maior vazão além do baseline.</summary>
    public static ScenarioSpec? PeakScenario(ChallengeSpec challenge)
    {
        var baseline = BaselineScenario(challenge);
        return challenge.TestPlan.Scenarios
            .Where(s => s.Kind == ScenarioKind.Load && s.Visibility == Visibility.Public && s.Id != baseline?.Id)
            .OrderByDescending(s => new[] { s.Load.Rps, s.Load.ToRps, s.Load.PeakRps }.Max() ?? 0)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    public static Dimension DimensionOf(ScenarioKind kind) => kind switch
    {
        ScenarioKind.Load => Dimension.Performance,
        ScenarioKind.Chaos => Dimension.Resilience,
        _ => Dimension.Consistency,
    };

    private static bool HasProvisionedCapacity(string archetype) =>
        archetype is Archetypes.Service or Archetypes.Function or Archetypes.StreamProcessor or Archetypes.Relational
            or Archetypes.RelationalDistributed or Archetypes.Cache;

    private static double Percentile(LatencySummary summary, string metric) => metric[(metric.LastIndexOf('.') + 1)..] switch
    {
        "p50" => summary.P50,
        "p95" => summary.P95,
        _ => summary.P99,
    };

    private static string Describe(SloSpec slo, double? observed, double target, bool isMinimum)
    {
        var subject = slo.UseCase is null ? Fmt.MetricName(slo.Metric) : $"{slo.UseCase} · {Fmt.MetricName(slo.Metric)}";
        var limit = isMinimum ? $"mínimo {Fmt.MetricValue(slo.Metric, target)}" : $"máximo {Fmt.MetricValue(slo.Metric, target)}";
        var value = observed is { } o ? Fmt.MetricValue(slo.Metric, o) : "sem dados (nenhuma requisição concluída)";
        return $"{subject}: {value} ({limit})";
    }
}
