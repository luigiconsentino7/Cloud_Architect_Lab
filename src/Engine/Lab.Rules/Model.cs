using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Flows;

namespace Lab.Rules;

public enum Dimension
{
    Functional,
    Performance,
    Resilience,
    Consistency,
    Security,
    Cost,
    Operability,
}

public enum Severity
{
    Critical,
    High,
    Medium,
    Low,
}

/// <summary>Achado de uma regra, com os elementos envolvidos e as métricas que podem confirmá-lo na simulação.</summary>
public sealed record Finding
{
    public required string RuleId { get; init; }
    public required Dimension Dimension { get; init; }
    public required Severity Severity { get; init; }
    public required string Message { get; init; }
    public IReadOnlyList<string> NodeIds { get; init; } = [];
    public IReadOnlyList<string> EdgeIds { get; init; } = [];
    public string? UseCaseId { get; init; }
    public IReadOnlyList<string> ConfirmedBy { get; init; } = [];

    public IEnumerable<string> IrIds => NodeIds.Concat(EdgeIds);
}

/// <summary>Contexto das regras: IR + desafio + catálogo, com os fluxos já planejados.</summary>
public sealed class RuleContext
{
    private readonly Lazy<IReadOnlyDictionary<string, FlowPlan>> _plans;
    private readonly Lazy<IReadOnlyDictionary<string, double>> _peakRates;

    public RuleContext(ArchitectureGraph graph, ChallengeSpec challenge, ComponentCatalog catalog)
    {
        Graph = graph;
        Challenge = challenge;
        Catalog = catalog;
        _plans = new(() => FlowPlanner.PlanAll(graph, challenge));
        _peakRates = new(() => RateEstimator.EdgeRates(challenge, Plans, RateEstimator.PublicPeakRps(challenge)));
    }

    public ArchitectureGraph Graph { get; }

    public ChallengeSpec Challenge { get; }

    public ComponentCatalog Catalog { get; }

    public IReadOnlyDictionary<string, FlowPlan> Plans => _plans.Value;

    /// <summary>Taxa de chamadas por conexão no pico público.</summary>
    public IReadOnlyDictionary<string, double> PeakRates => _peakRates.Value;

    public ComponentDefinition Definition(Node node) =>
        Catalog.Find(node.CatalogId) ?? Catalog.DefaultFor(Graph.Provider, node.Archetype)
        ?? throw new InvalidOperationException($"Sem definição de catálogo para {node.CatalogId}.");

    public IEnumerable<UseCaseSpec> CriticalUseCases => Challenge.UseCases.Where(u => u.Critical);

    public IEnumerable<UseCaseSpec> MonetaryUseCases => Challenge.UseCases.Where(u => u.Monetary);

    public FlowPlan? PlanFor(UseCaseSpec useCase) => Plans.GetValueOrDefault(useCase.Id);

    /// <summary>Menor limite de latência (ms) entre os SLOs de latência do caso de uso.</summary>
    public double? LatencyBudgetMs(string useCaseId) => Challenge.TestPlan.Slos
        .Where(s => s.UseCase == useCaseId && s.Metric.StartsWith("latency.", StringComparison.Ordinal) && s.Max is not null)
        .Select(s => s.Max)
        .Min();

    public string Label(string nodeId) => Graph.FindNode(nodeId)?.Label ?? nodeId;

    public Finding Create(IArchitectureRule rule, string message, IEnumerable<string>? nodes = null,
        IEnumerable<string>? edges = null, string? useCase = null, Severity? severity = null) => new()
        {
            RuleId = rule.Id,
            Dimension = rule.Dimension,
            Severity = severity ?? rule.DefaultSeverity,
            Message = message,
            NodeIds = nodes?.Distinct(StringComparer.Ordinal).ToList() ?? [],
            EdgeIds = edges?.Distinct(StringComparer.Ordinal).ToList() ?? [],
            UseCaseId = useCase,
            ConfirmedBy = rule.ConfirmedBy,
        };
}

public interface IArchitectureRule
{
    string Id { get; }

    Dimension Dimension { get; }

    Severity DefaultSeverity { get; }

    /// <summary>Métricas da simulação que confirmam o achado (evita penalidade dupla na nota).</summary>
    IReadOnlyList<string> ConfirmedBy { get; }

    bool AppliesTo(ChallengeSpec challenge);

    IEnumerable<Finding> Evaluate(RuleContext context);
}

public abstract class Rule : IArchitectureRule
{
    public abstract string Id { get; }

    public abstract Dimension Dimension { get; }

    public abstract Severity DefaultSeverity { get; }

    public virtual IReadOnlyList<string> ConfirmedBy => [];

    public virtual bool AppliesTo(ChallengeSpec challenge) => true;

    public abstract IEnumerable<Finding> Evaluate(RuleContext context);
}
