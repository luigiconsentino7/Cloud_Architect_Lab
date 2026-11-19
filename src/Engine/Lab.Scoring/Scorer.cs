using Lab.Cost;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Rules;
using Lab.Rules.Functional;
using Lab.Simulation;

namespace Lab.Scoring;

/// <summary>Penalidade de uma regra na dimensão dela (soma dos achados não confirmados, no máximo 2× a severidade mais alta).</summary>
public sealed record RulePenalty(
    string RuleId,
    Dimension Dimension,
    Severity Severity,
    int Findings,
    int ConfirmedFindings,
    double Points,
    IReadOnlyList<string> ConfirmedIn);

/// <summary>Um cenário dentro de uma dimensão: vale a pior verificação dele (um cenário só passa se todos os SLOs passarem).</summary>
public sealed record ScenarioScore(
    string ScenarioId,
    string ScenarioName,
    Dimension Dimension,
    double Score,
    double Weight,
    IReadOnlyList<string> CheckIds)
{
    public bool Passed => Score >= 1;
}

public sealed record DimensionScore
{
    public required Dimension Dimension { get; init; }
    public double Weight { get; init; }

    /// <summary>0..100, já com as penalidades.</summary>
    public double Score { get; init; }

    /// <summary>100 × média ponderada dos cenários (ou do teste funcional, ou da fórmula de custo).</summary>
    public double BaseScore { get; init; }

    public double Penalty { get; init; }
    public IReadOnlyList<string> CheckIds { get; init; } = [];
    public IReadOnlyList<ScenarioScore> Scenarios { get; init; } = [];
    public IReadOnlyList<RulePenalty> Rules { get; init; } = [];
    public string Basis { get; init; } = "";

    /// <summary>Pontos que esta dimensão soma à nota base.</summary>
    public double Contribution { get; init; }
}

/// <summary>Trava da nota. <see cref="Applied"/> indica se ela baixou a nota (a nota base já pode estar abaixo).</summary>
public sealed record ScoreCap(int Value, string Reason, IReadOnlyList<string> UseCaseIds, IReadOnlyList<string> CheckIds, bool Applied = false);

public sealed record ScoreResult
{
    public IReadOnlyList<DimensionScore> Dimensions { get; init; } = [];
    public double BaseScore { get; init; }
    public ScoreCap? Cap { get; init; }
    public int FinalScore { get; init; }
    public string Rank { get; init; } = "D";
    public IReadOnlyList<CheckResult> Checks { get; init; } = [];
    public IReadOnlyList<AssessedFinding> Findings { get; init; } = [];
    public IReadOnlyList<RulePenalty> RulePenalties { get; init; } = [];
    public double CostScore { get; init; }

    public DimensionScore? For(Dimension dimension) => Dimensions.FirstOrDefault(d => d.Dimension == dimension);
}

public sealed record ScoreInput
{
    public required ChallengeSpec Challenge { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required IReadOnlyList<UseCaseFunctionalResult> Functional { get; init; }
    public required SimulationReport Simulation { get; init; }
    public required IReadOnlyList<CheckResult> Checks { get; init; }
    public required CostReport Cost { get; init; }
    public double? ReferenceCostUsd { get; init; }
}

/// <summary>Nota por dimensão, travas, nota final e rank — as fórmulas da spec 05, Etapa 7.</summary>
public static class Scorer
{
    public static double SeverityPoints(Severity severity) => severity switch
    {
        Severity.Critical => 25,
        Severity.High => 10,
        Severity.Medium => 4,
        _ => 1,
    };

    public static string RankFor(int score) => score switch
    {
        >= 90 => "S",
        >= 80 => "A",
        >= 70 => "B",
        >= 60 => "C",
        _ => "D",
    };

    /// <summary>Custo dentro do orçamento perde pontos só a partir de 1,5× a referência; acima do orçamento, 50% a mais zera.</summary>
    public static double CostScore(double cost, double budget, double? reference)
    {
        if (budget > 0 && cost > budget) return Math.Max(0, 100 * (1 - (2 * ((cost / budget) - 1))));
        if (reference is not > 0) return 100;
        return Math.Clamp(100 - (40 * Math.Max(0, (cost / reference.Value) - 1.5)), 0, 100);
    }

    public static ScoreResult Score(ScoreInput input) => EngineCulture.Run(() => ScoreCore(input));

    private static ScoreResult ScoreCore(ScoreInput input)
    {
        var challenge = input.Challenge;
        var assessed = input.Findings
            .Select(f =>
            {
                var (observed, confirmed, scenarios) = EvidenceMatcher.Match(f, challenge, input.Simulation, input.Checks);
                return new AssessedFinding(f, observed, confirmed, scenarios, confirmed ? 0 : SeverityPoints(f.Severity));
            })
            .ToList();
        var penalties = assessed
            .GroupBy(a => a.Finding.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var severity = g.Min(a => a.Finding.Severity);
                var raw = g.Sum(a => a.Points);
                return new RulePenalty(g.Key, g.First().Finding.Dimension, severity, g.Count(), g.Count(a => a.Confirmed),
                    Math.Min(raw, 2 * SeverityPoints(severity)),
                    g.SelectMany(a => a.Confirmed ? a.ScenarioIds : []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList());
            })
            .ToList();

        var weights = challenge.Weights;
        var totalWeight = weights.Total <= 0 ? 100 : weights.Total;
        var costScore = CostScore(input.Cost.MonthlyUsd, input.Cost.BudgetUsd, input.ReferenceCostUsd);
        var dimensions = new List<DimensionScore>();
        foreach (var dimension in Enum.GetValues<Dimension>())
        {
            var weight = WeightOf(weights, dimension);
            var checks = input.Checks.Where(c => c.Dimension == dimension).ToList();
            var scenarios = ScenarioScores(challenge, checks, dimension);
            var (baseScore, basis) = dimension switch
            {
                Dimension.Functional => FunctionalBase(challenge, input.Functional),
                Dimension.Cost => (costScore, CostBasis(input.Cost, input.ReferenceCostUsd)),
                _ when scenarios.Count > 0 => (100 * scenarios.Sum(s => s.Weight * s.Score) / scenarios.Sum(s => s.Weight),
                    $"{scenarios.Count(s => s.Passed)} de {scenarios.Count} cenário(s) aprovado(s); cada cenário vale a pior verificação dele"),
                _ => (100d, "sem verificações de simulação nesta dimensão: só as regras contam"),
            };
            var rules = penalties.Where(p => p.Dimension == dimension).ToList();
            var penalty = rules.Sum(p => p.Points);
            var score = Math.Round(Math.Clamp(baseScore - penalty, 0, 100), 1, MidpointRounding.AwayFromZero);
            dimensions.Add(new DimensionScore
            {
                Dimension = dimension,
                Weight = weight,
                Score = score,
                BaseScore = Math.Round(baseScore, 1, MidpointRounding.AwayFromZero),
                Penalty = penalty,
                CheckIds = checks.Select(c => c.Id).ToList(),
                Scenarios = scenarios,
                Rules = rules,
                Basis = basis,
                Contribution = Math.Round(weight * score / totalWeight, 2, MidpointRounding.AwayFromZero),
            });
        }

        var baseTotal = Math.Round(dimensions.Sum(d => d.Weight * d.Score) / totalWeight, 1, MidpointRounding.AwayFromZero);
        var cap = CapFor(challenge, input.Functional, input.Checks, input.Simulation);
        var final = (int)Math.Round(Math.Min(baseTotal, cap?.Value ?? 100), MidpointRounding.AwayFromZero);
        return new ScoreResult
        {
            Dimensions = dimensions,
            BaseScore = baseTotal,
            Cap = cap is null ? null : cap with { Applied = cap.Value < baseTotal },
            FinalScore = final,
            Rank = RankFor(final),
            Checks = input.Checks,
            Findings = assessed,
            RulePenalties = penalties,
            CostScore = Math.Round(costScore, 1, MidpointRounding.AwayFromZero),
        };
    }

    public static double WeightOf(RubricWeights weights, Dimension dimension) => dimension switch
    {
        Dimension.Functional => weights.Functional,
        Dimension.Performance => weights.Performance,
        Dimension.Resilience => weights.Resilience,
        Dimension.Consistency => weights.Consistency,
        Dimension.Security => weights.Security,
        Dimension.Cost => weights.Cost,
        _ => weights.Operability,
    };

    /// <summary>
    /// Agrupa as verificações da dimensão por cenário. O cenário vale a pior verificação (ex.: o pico com 84,7% de
    /// sucesso vale 0, mesmo com o p99 dentro do limite) e pesa 2 quando envolve um caso de uso crítico.
    /// </summary>
    public static IReadOnlyList<ScenarioScore> ScenarioScores(ChallengeSpec challenge, IEnumerable<CheckResult> checks, Dimension dimension)
    {
        var order = challenge.TestPlan.Scenarios.Select((s, i) => (s.Id, i)).ToDictionary(p => p.Id, p => p.i, StringComparer.Ordinal);
        return checks
            .Where(c => c.Dimension == dimension)
            .GroupBy(c => c.ScenarioId, StringComparer.Ordinal)
            .OrderBy(g => order.GetValueOrDefault(g.Key, int.MaxValue))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new ScenarioScore(g.Key, g.First().ScenarioName, dimension, g.Min(c => c.Score), g.Max(c => c.Weight),
                g.Select(c => c.Id).ToList()))
            .ToList();
    }

    /// <summary>Peso de cada caso de uso no teste funcional (crítico pesa 2).</summary>
    public static double UseCaseWeight(ChallengeSpec challenge, string useCaseId) =>
        challenge.FindUseCase(useCaseId) is { Critical: false } ? 1 : 2;

    private static (double Score, string Basis) FunctionalBase(ChallengeSpec challenge, IReadOnlyList<UseCaseFunctionalResult> functional)
    {
        if (functional.Count == 0) return (100, "sem casos de uso");
        var total = functional.Sum(f => UseCaseWeight(challenge, f.UseCaseId));
        var score = 100 * functional.Sum(f => UseCaseWeight(challenge, f.UseCaseId) * f.Score) / total;
        return (score, $"{functional.Count(f => f.Status == FunctionalStatus.Passed)} de {functional.Count} casos de uso aprovados");
    }

    private static string CostBasis(CostReport cost, double? reference)
    {
        var basis = $"{Fmt.Usd(cost.MonthlyUsd)}/mês para um orçamento de {Fmt.Usd(cost.BudgetUsd)}";
        return reference is > 0 ? $"{basis}; referência {Fmt.Usd(reference.Value)}/mês" : basis;
    }

    private static ScoreCap? CapFor(ChallengeSpec challenge, IReadOnlyList<UseCaseFunctionalResult> functional,
        IReadOnlyList<CheckResult> checks, SimulationReport simulation)
    {
        var noPath = functional.Where(f => !f.PathOk).Select(f => f.UseCaseId).ToList();
        if (noPath.Count > 0)
            return new ScoreCap(50, $"{string.Join(", ", noPath)} sem caminho válido: o caso de uso não funciona.", noPath, []);

        var monetaryChecks = checks.Where(c => c.MonetaryViolation).Select(c => c.Id).ToList();
        var monetaryDuplicates = simulation.Scenarios.Any(s => s.Integrity.MonetaryDuplicates > 0);
        if (monetaryChecks.Count > 0 || monetaryDuplicates)
        {
            var useCases = challenge.UseCases.Where(u => u.Monetary).Select(u => u.Id).ToList();
            return new ScoreCap(60, "Violação de integridade em caso de uso monetário: o desenho pode perder ou duplicar dinheiro.", useCases, monetaryChecks);
        }

        return null;
    }
}
