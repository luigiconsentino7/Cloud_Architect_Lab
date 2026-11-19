using Lab.Ir.Challenges;

namespace Lab.Ir.Flows;

/// <summary>Uma chamada na árvore do fluxo: a conexão percorrida e o nó que a atende.</summary>
public sealed class CallNode
{
    private readonly List<CallNode> _children = [];

    internal CallNode(Edge? edge, Node target, CallNode? parent)
    {
        Edge = edge;
        Target = target;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        InSyncSegment = parent is null || (parent.InSyncSegment
            && !Archetypes.IsMessaging(parent.Target.Archetype)
            && edge!.Interaction is not (Interaction.AsyncConsume or Interaction.Cdc));
    }

    /// <summary>Conexão percorrida (nula na raiz, que é o cliente).</summary>
    public Edge? Edge { get; }

    public Node Target { get; }

    public CallNode? Parent { get; }

    public IReadOnlyList<CallNode> Children => _children;

    public int Depth { get; }

    /// <summary>Faz parte do caminho síncrono até a resposta ao cliente.</summary>
    public bool InSyncSegment { get; }

    public TriggerKind Trigger => Edge?.Config.Trigger ?? TriggerKind.Always;

    /// <summary>Executa no caminho feliz (sempre ou em cache miss) — não só em falha.</summary>
    public bool OnHappyPath => Trigger != TriggerKind.OnFailure && (Parent?.OnHappyPath ?? true);

    public IEnumerable<CallNode> Ancestors()
    {
        for (var current = Parent; current is not null; current = current.Parent)
            yield return current;
    }

    internal void Add(CallNode child) => _children.Add(child);
}

public sealed class FlowPlan
{
    internal FlowPlan(string useCaseId, CallNode? root, IReadOnlyList<CallNode> calls, IReadOnlyList<string> problems)
    {
        UseCaseId = useCaseId;
        Root = root;
        Calls = calls;
        Problems = problems;
    }

    public string UseCaseId { get; }

    /// <summary>Raiz (cliente). Nula quando o fluxo não existe ou não começa num cliente.</summary>
    public CallNode? Root { get; }

    /// <summary>Todas as chamadas, na ordem dos passos (sem a raiz).</summary>
    public IReadOnlyList<CallNode> Calls { get; }

    public IReadOnlyList<string> Problems { get; }

    public bool IsValid => Root is not null && Problems.Count == 0 && Calls.Count > 0;

    public IEnumerable<CallNode> HappyPath => Calls.Where(c => c.OnHappyPath);

    public IEnumerable<CallNode> SyncHappyPath => HappyPath.Where(c => c.InSyncSegment);

    public IEnumerable<Node> Nodes => (Root is null ? [] : new[] { Root.Target }).Concat(Calls.Select(c => c.Target)).DistinctBy(n => n.Id);

    public bool Contains(string nodeId) => Root?.Target.Id == nodeId || Calls.Any(c => c.Target.Id == nodeId);

    public bool ContainsEdge(string edgeId) => Calls.Any(c => c.Edge!.Id == edgeId);
}

public static class FlowPlanner
{
    public static FlowPlan Plan(ArchitectureGraph graph, UseCaseSpec useCase)
    {
        var flow = graph.FlowFor(useCase.Id);
        if (flow is null || flow.Steps.Count == 0)
            return new FlowPlan(useCase.Id, null, [], [$"{useCase.Id} não tem fluxo definido."]);

        var problems = new List<string>();
        var first = graph.FindEdge(flow.Steps[0]);
        var start = first is null ? null : graph.FindNode(first.From);
        if (start is null || !Archetypes.IsClient(start.Archetype))
        {
            return new FlowPlan(useCase.Id, null, [],
                [$"{useCase.Id}: o fluxo precisa começar num cliente (ex.: {useCase.EntryClient})."]);
        }

        var root = new CallNode(null, start, null);
        var firstArrival = new Dictionary<string, CallNode>(StringComparer.Ordinal) { [start.Id] = root };
        var calls = new List<CallNode>(flow.Steps.Count);

        foreach (var stepId in flow.Steps)
        {
            var edge = graph.FindEdge(stepId);
            if (edge is null) continue;
            if (!firstArrival.TryGetValue(edge.From, out var parent))
            {
                var fromLabel = graph.FindNode(edge.From)?.Label ?? edge.From;
                problems.Add($"{useCase.Id}: o passo que sai de \"{fromLabel}\" está desconectado — \"{fromLabel}\" ainda não foi alcançado no fluxo.");
                continue;
            }

            var target = graph.FindNode(edge.To);
            if (target is null) continue;
            var call = new CallNode(edge, target, parent);
            parent.Add(call);
            calls.Add(call);
            firstArrival.TryAdd(target.Id, call);
        }

        if (calls.Count > 0 && !calls.Any(c => c.InSyncSegment))
            problems.Add($"{useCase.Id}: nenhuma chamada síncrona devolve resposta ao cliente.");

        return new FlowPlan(useCase.Id, root, calls, problems);
    }

    public static IReadOnlyDictionary<string, FlowPlan> PlanAll(ArchitectureGraph graph, ChallengeSpec challenge) =>
        challenge.UseCases.ToDictionary(u => u.Id, u => Plan(graph, u), StringComparer.Ordinal);
}

/// <summary>Taxa esperada de chamadas por conexão, propagada pela árvore de cada fluxo.</summary>
public static class RateEstimator
{
    public static IReadOnlyDictionary<string, double> EdgeRates(
        ChallengeSpec challenge, IReadOnlyDictionary<string, FlowPlan> plans, double totalRps)
    {
        var rates = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var useCase in challenge.UseCases)
        {
            if (!plans.TryGetValue(useCase.Id, out var plan) || plan.Root is null) continue;
            var share = challenge.Workload.Mix.TryGetValue(useCase.Id, out var load) ? load.Share : 0;
            var cacheability = challenge.Workload.Cacheability.GetValueOrDefault(useCase.Id);
            Propagate(plan.Root, totalRps * share, cacheability, rates);
        }

        return rates;
    }

    private static void Propagate(CallNode node, double rate, double cacheability, Dictionary<string, double> rates)
    {
        foreach (var child in node.Children)
        {
            var multiplier = child.Edge!.Config.CallRatio * child.Trigger switch
            {
                TriggerKind.OnFailure => 0,
                TriggerKind.OnCacheMiss => 1 - cacheability,
                _ => 1,
            };
            var childRate = rate * multiplier;
            rates[child.Edge.Id] = rates.GetValueOrDefault(child.Edge.Id) + childRate;
            Propagate(child, childRate, cacheability, rates);
        }
    }

    /// <summary>Pico de carga declarado nos cenários públicos (o que o usuário pode ver antes de submeter).</summary>
    public static double PublicPeakRps(ChallengeSpec challenge)
    {
        var peak = challenge.Workload.BaselineRps;
        foreach (var scenario in challenge.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Public))
        {
            var load = scenario.Load;
            peak = Math.Max(peak, new[] { load.Rps, load.ToRps, load.PeakRps, load.BaseRps, load.FromRps }.Max() ?? 0);
        }

        return peak;
    }
}
