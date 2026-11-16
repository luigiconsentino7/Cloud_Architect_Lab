using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Content;
using Lab.Ir.Scene;
using Lab.Ir.Seeds;
using Lab.Scoring;

namespace Lab.Pipeline.Seeds;

public enum IssueSeverity
{
    Error,
    Warning,
}

public sealed record SeedIssue(IssueSeverity Severity, string Message, string? Variation = null);

public sealed record VariationResult(
    string Name,
    CloudProvider Provider,
    Difficulty Difficulty,
    IReadOnlyDictionary<string, double> Variables,
    IReadOnlyList<string> Twists,
    int? ReferenceScore,
    string? Rank,
    double? ReferenceCostUsd,
    double BudgetUsd,
    IReadOnlyList<string> FailedChecks,
    double WallTimeMs);

public sealed record SeedValidationReport(string SeedId, IReadOnlyList<SeedIssue> Issues, IReadOnlyList<VariationResult> Variations)
{
    public bool Ok => Issues.All(i => i.Severity != IssueSeverity.Error);
}

public sealed record SeedValidationOptions
{
    public IReadOnlyList<CloudProvider> Providers { get; init; } = [CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp];

    /// <summary>Dificuldades a validar (nulo = todas as da semente).</summary>
    public IReadOnlyList<Difficulty>? Difficulties { get; init; }

    /// <summary>Roda a referência nos cantos (todas as variáveis no mínimo e no máximo da faixa); senão, só no valor padrão.</summary>
    public bool Corners { get; init; } = true;

    /// <summary>Também valida cada pegadinha (aplicada sozinha, no canto máximo).</summary>
    public bool Twists { get; init; } = true;

    public bool RunReference { get; init; } = true;
    public double MinimumReferenceScore { get; init; } = 85;
    public int? MaxParallelism { get; init; }
    public long EventBudget { get; init; } = 20_000_000;
    public IProgress<string>? Progress { get; init; }
}

/// <summary>
/// <c>lab challenge validate</c>: schema e semântica de cada variação + a solução de referência com nota ≥ 85 nos cantos do
/// espaço de variação, em cada provedor. Se ela passa nos extremos, passa no meio.
/// </summary>
public static class SeedValidator
{
    public static SeedValidationReport Validate(Seed seed, ComponentCatalog catalog, RuleTextLibrary ruleTexts,
        SeedValidationOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new SeedValidationOptions();
        var issues = new List<SeedIssue>();
        var results = new List<VariationResult>();
        var difficulties = (seed.Difficulties.Count > 0 ? seed.Difficulties : Enum.GetValues<Difficulty>())
            .Where(d => options.Difficulties is null || options.Difficulties.Contains(d))
            .ToList();

        foreach (var (name, spec) in seed.Variables)
        {
            foreach (var difficulty in difficulties)
            {
                if (!spec.Ranges.TryGetValue(difficulty, out var range) || range.Length != 2)
                    issues.Add(new SeedIssue(IssueSeverity.Warning, $"Variável {name} sem faixa para {difficulty}: usa o padrão {spec.Default}."));
                else if (range[0] > range[1])
                    issues.Add(new SeedIssue(IssueSeverity.Error, $"Variável {name}: faixa invertida em {difficulty} [{range[0]}, {range[1]}]."));
            }
        }

        foreach (var variation in Variations(seed, difficulties, options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.Progress?.Report(variation.Name);
            Instantiation instance;
            try
            {
                instance = SeedInstantiator.Instantiate(variation.Request, catalog);
            }
            catch (SeedException ex)
            {
                issues.Add(new SeedIssue(IssueSeverity.Error, ex.Message, variation.Name));
                continue;
            }

            var challenge = instance.Challenge;
            issues.AddRange(ChallengeChecks.Check(challenge, catalog).Select(i => i with { Variation = variation.Name }));
            foreach (var diagnostic in instance.ReferenceDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
                issues.Add(new SeedIssue(IssueSeverity.Error, $"Referência: {diagnostic.Code} {diagnostic.Message}", variation.Name));
            foreach (var diagnostic in instance.ReferenceDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning))
                issues.Add(new SeedIssue(IssueSeverity.Warning, $"Referência: {diagnostic.Code} {diagnostic.Message}", variation.Name));

            if (!options.RunReference || challenge.Reference is null || instance.ReferenceDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                continue;

            var started = DateTime.UtcNow;
            var result = EvaluationPipeline.Evaluate(new EvaluationRequest
            {
                Challenge = challenge,
                Catalog = catalog,
                RuleTexts = ruleTexts,
                Graph = challenge.Reference,
                IncludeSeries = false,
                ComputeReferenceCost = false,
                EventBudget = options.EventBudget,
                MaxParallelism = options.MaxParallelism,
            }, cancellationToken);
            var failed = result.Score.Checks.Where(c => !c.Passed).Select(c => $"{c.Id}: {c.Description}").ToList();
            results.Add(new VariationResult(variation.Name, challenge.Provider, challenge.Difficulty, challenge.Variables, challenge.Twists,
                result.Score.FinalScore, result.Score.Rank, result.Cost.MonthlyUsd, challenge.Constraints.BudgetUsdMonth, failed,
                (DateTime.UtcNow - started).TotalMilliseconds));

            if (result.Score.FinalScore < options.MinimumReferenceScore)
            {
                var top = string.Join("; ", result.Review.Summary.Select(s => s.Text));
                issues.Add(new SeedIssue(IssueSeverity.Error,
                    $"A referência tirou {result.Score.FinalScore} ({result.Score.Rank}), abaixo de {options.MinimumReferenceScore}. Maiores perdas: {top}",
                    variation.Name));
            }

            foreach (var check in failed)
                issues.Add(new SeedIssue(IssueSeverity.Warning, $"A referência reprova {check} — o SLO é alcançável?", variation.Name));

            var budget = challenge.Constraints.BudgetUsdMonth;
            if (budget > 0 && result.Cost.MonthlyUsd > budget)
            {
                issues.Add(new SeedIssue(IssueSeverity.Error,
                    $"A referência custa {Fmt.Usd(result.Cost.MonthlyUsd)}/mês, acima do orçamento de {Fmt.Usd(budget)}.", variation.Name));
            }
            else if (budget > 0 && result.Cost.MonthlyUsd > budget * 0.8)
            {
                issues.Add(new SeedIssue(IssueSeverity.Warning,
                    $"A referência usa {Fmt.Pct(result.Cost.MonthlyUsd / budget)} do orçamento: sobra pouca folga para soluções diferentes.", variation.Name));
            }
        }

        return new SeedValidationReport(seed.Id, issues, results);
    }

    private sealed record Variation(string Name, InstantiationRequest Request);

    private static IEnumerable<Variation> Variations(Seed seed, IEnumerable<Difficulty> difficulties, SeedValidationOptions options)
    {
        foreach (var difficulty in difficulties)
        {
            var corners = new List<(string Name, Dictionary<string, double>? Values)>();
            if (!options.Corners || seed.Variables.Count == 0) corners.Add(("padrão", null));
            else
            {
                corners.Add(("mínimo", Corner(seed, difficulty, 0)));
                corners.Add(("máximo", Corner(seed, difficulty, 1)));
            }

            foreach (var provider in options.Providers)
            {
                foreach (var (corner, values) in corners)
                {
                    yield return new Variation($"{provider.Code()}/{difficulty.ToString().ToLowerInvariant()}/{corner}", new InstantiationRequest
                    {
                        Seed = seed, Provider = provider, Difficulty = difficulty, Variables = values,
                    });
                }
            }

            if (!options.Twists) continue;
            foreach (var twist in seed.Twists.Where(t => t.MinDifficulty <= difficulty))
            {
                var values = options.Corners ? Corner(seed, difficulty, 1) : null;
                var provider = options.Providers.FirstOrDefault(CloudProvider.Aws);
                yield return new Variation($"{provider.Code()}/{difficulty.ToString().ToLowerInvariant()}/{(options.Corners ? "máximo" : "padrão")}+{twist.Id}",
                    new InstantiationRequest { Seed = seed, Provider = provider, Difficulty = difficulty, Variables = values, Twists = [twist.Id] });
            }
        }
    }

    private static Dictionary<string, double> Corner(Seed seed, Difficulty difficulty, int index) =>
        seed.Variables
            .Where(p => p.Value.Ranges.TryGetValue(difficulty, out var r) && r.Length == 2)
            .ToDictionary(p => p.Key, p => p.Value.Ranges[difficulty][index], StringComparer.Ordinal);
}

/// <summary>Checagens semânticas de um desafio instanciado (sem simular).</summary>
public static class ChallengeChecks
{
    private static readonly HashSet<string> Metrics = new(StringComparer.Ordinal)
    {
        "latency.p50", "latency.p95", "latency.p99", "success-rate", "async-completion.p95", "async-completion.p99",
        "duplicate-effects", "lost-events", "ordering-violations",
    };

    public static IReadOnlyList<SeedIssue> Check(ChallengeSpec challenge, ComponentCatalog catalog)
    {
        var issues = new List<SeedIssue>();
        void Error(string message) => issues.Add(new SeedIssue(IssueSeverity.Error, message));
        void Warn(string message) => issues.Add(new SeedIssue(IssueSeverity.Warning, message));

        if (challenge.UseCases.Count == 0) Error("O desafio não tem casos de uso.");
        foreach (var duplicate in challenge.UseCases.GroupBy(u => u.Id).Where(g => g.Count() > 1)) Error($"Caso de uso duplicado: {duplicate.Key}.");
        var useCases = challenge.UseCases.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
        var externals = challenge.ExternalSystems.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var useCase in challenge.UseCases)
        {
            if (catalog.Find(useCase.EntryClient) is not { Archetype: Archetypes.Client })
                Error($"{useCase.Id}: entryClient '{useCase.EntryClient}' não é um cliente do catálogo.");
            if (!challenge.Workload.Mix.ContainsKey(useCase.Id)) Error($"{useCase.Id}: sem participação no workload.mix.");
            foreach (var capability in useCase.RequiredCapabilities.Where(c => c.Target?.StartsWith("external:", StringComparison.Ordinal) == true))
            {
                if (!externals.Contains(capability.Target!)) Error($"{useCase.Id}: capacidade {capability.Capability} aponta para {capability.Target}, que não está em externalSystems.");
            }

            foreach (var negative in useCase.NegativePaths.Where(n => !externals.Contains(n.Dependency)))
                Error($"{useCase.Id}: caminho de falha para {negative.Dependency}, que não está em externalSystems.");
        }

        foreach (var external in challenge.ExternalSystems.Where(e => !e.Id.StartsWith("external:", StringComparison.Ordinal)))
            Error($"Sistema externo '{external.Id}': o ID precisa começar com 'external:'.");

        var shares = challenge.Workload.Mix.Values.Sum(m => m.Share);
        if (Math.Abs(shares - 1) > 0.001) Error($"workload.mix soma {shares:0.###}, precisa somar 1.");
        foreach (var key in challenge.Workload.Mix.Keys.Concat(challenge.Workload.Cacheability.Keys).Where(k => !useCases.Contains(k)).Distinct())
            Error($"workload cita o caso de uso '{key}', que não existe.");
        if (challenge.Workload.BaselineRps <= 0) Error("workload.baselineRps precisa ser positivo.");

        var scenarios = challenge.TestPlan.Scenarios;
        foreach (var duplicate in scenarios.GroupBy(s => s.Id).Where(g => g.Count() > 1)) Error($"Cenário duplicado: {duplicate.Key}.");
        var scenarioIds = scenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (!scenarios.Any(s => s is { Kind: ScenarioKind.Load, Visibility: Visibility.Public })) Error("O plano de testes precisa de ao menos um cenário de carga público.");
        foreach (var scenario in scenarios)
        {
            if (scenario.Load.Ref is { } reference && !scenarioIds.Contains(reference)) Error($"Cenário {scenario.Id}: load.ref '{reference}' não existe.");
            foreach (var fault in scenario.Faults.Where(f => f.Target?.StartsWith("external:", StringComparison.Ordinal) == true && !externals.Contains(f.Target)))
                Error($"Cenário {scenario.Id}: falha em {fault.Target}, que não está em externalSystems.");
            if (scenario.Kind != ScenarioKind.Load && scenario.Faults.Count == 0) Warn($"Cenário {scenario.Id} é de {scenario.Kind} mas não injeta falhas.");
        }

        var slos = challenge.TestPlan.Slos;
        foreach (var duplicate in slos.GroupBy(s => s.Id).Where(g => g.Count() > 1)) Error($"SLO duplicado: {duplicate.Key}.");
        foreach (var slo in slos)
        {
            if (!Metrics.Contains(slo.Metric)) Error($"{slo.Id}: métrica desconhecida '{slo.Metric}'.");
            if (slo.UseCase is { } useCase && !useCases.Contains(useCase)) Error($"{slo.Id}: caso de uso '{useCase}' não existe.");
            if (slo.Metric == "success-rate" ? slo.Min is null : slo.Max is null)
                Error($"{slo.Id}: informe {(slo.Metric == "success-rate" ? "min" : "max")} para {slo.Metric}.");
            if (slo.Metric is "success-rate" && slo.Min is > 1) Error($"{slo.Id}: taxa de sucesso é uma fração (use 99.95% ou 0.9995).");
            if (slo.Scenarios.Count == 0) Error($"{slo.Id}: sem cenários.");
            foreach (var scenario in slo.Scenarios.Where(s => !scenarioIds.Contains(s))) Error($"{slo.Id}: cenário '{scenario}' não existe.");
            if ((slo.Metric.StartsWith("latency.", StringComparison.Ordinal) || slo.Metric == "success-rate") && slo.UseCase is null)
                Error($"{slo.Id}: {slo.Metric} precisa de useCase.");
        }

        foreach (var scenario in scenarios.Where(s => !slos.Any(slo => slo.Scenarios.Contains(s.Id))))
            Warn($"Cenário {scenario.Id} não é verificado por nenhum SLO.");

        if (challenge.Weights.Total <= 0) Error("Os pesos da rubrica somam zero.");
        else if (Math.Abs(challenge.Weights.Total - 100) > 0.001) Warn($"Os pesos da rubrica somam {challenge.Weights.Total}; o padrão é 100.");
        if (challenge.Constraints.BudgetUsdMonth <= 0) Error("constraints.budgetUsdMonth precisa ser positivo.");
        foreach (var hint in challenge.Hints.Where(h => h.Level is < 1 or > 3)) Error($"Dica com nível {hint.Level}: use 1, 2 ou 3.");

        if (challenge.Reference is not { } graph) Error("A semente não tem solução de referência.");
        else
        {
            foreach (var useCase in challenge.UseCases.Where(u => graph.FlowFor(u.Id) is null))
                Error($"A referência não define o fluxo de {useCase.Id}.");
        }

        return issues;
    }
}
