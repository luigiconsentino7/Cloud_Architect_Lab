using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;

namespace Lab.Ir.Scene;

/// <summary>Fluxo de um caso de uso como definido no editor: IDs de conexões, em ordem.</summary>
public sealed record FlowDefinition(string UseCaseId, IReadOnlyList<string> Steps);

public sealed record SceneCompileRequest
{
    public required JsonNode? Scene { get; init; }
    public IReadOnlyList<FlowDefinition> Flows { get; init; } = [];
    public required CloudProvider Provider { get; init; }
    public required ComponentCatalog Catalog { get; init; }
    public ChallengeSpec? Challenge { get; init; }
}

public sealed record CompilationResult(ArchitectureGraph Graph, IReadOnlyList<Diagnostic> Diagnostics, string IrHash)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>Compila a cena do Excalidraw (desenho + customData) na IR que o motor testa. Determinístico.</summary>
public static class SceneCompiler
{
    public static CompilationResult Compile(SceneCompileRequest request)
    {
        var context = new Context(request);
        context.CollectComponents();
        context.CollectOwnedElements();
        context.CollectBoundaries();
        context.ResolvePlacements();
        context.CollectEdges();
        context.CollectUntypedAndAnnotations();
        context.CollectFlows();
        context.CheckStructure();
        return context.Build();
    }

    private sealed class Context(SceneCompileRequest request)
    {
        private readonly ComponentCatalog _catalog = request.Catalog;
        private readonly List<SceneElement> _elements = SceneElement.ParseAll(request.Scene).Where(e => !e.IsDeleted).ToList();
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly Dictionary<string, string> _ownerOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, SceneElement> _anchorOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ComponentDefinition> _definitionOf = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _sourceMap = new(StringComparer.Ordinal);
        private readonly List<Node> _nodes = [];
        private readonly List<Edge> _edges = [];
        private readonly List<Boundary> _boundaries = [];
        private readonly HashSet<string> _boundaryElements = new(StringComparer.Ordinal);
        private readonly List<Annotation> _annotations = [];
        private readonly List<Flow> _flows = [];
        private readonly string _defaultRegion = request.Challenge?.Region ?? request.Provider.BrazilRegion();

        public void CollectComponents()
        {
            foreach (var element in _elements)
            {
                if (element.Kind != "component" || (Json.Str(element.CustomData!["role"]) ?? "anchor") != "anchor")
                    continue;

                var data = element.CustomData!;
                var catalogId = Json.Str(data["catalogId"]);
                if (catalogId is null || _catalog.Find(catalogId) is not { } definition)
                {
                    Add(DiagnosticCodes.UnknownComponent, DiagnosticSeverity.Error,
                        $"Componente desconhecido no catálogo: '{catalogId ?? "(sem catalogId)"}'.", [element.Id], []);
                    continue;
                }

                var componentId = Json.Str(data["componentId"]) ?? element.Id;
                var nodeId = definition.Archetype == Archetypes.External
                    ? Json.Str(data["externalId"]) ?? componentId
                    : componentId;

                if (_anchorOf.ContainsKey(nodeId))
                {
                    if (definition.Archetype == Archetypes.External)
                    {
                        // O mesmo sistema externo desenhado duas vezes é um só nó.
                        _ownerOf[element.Id] = nodeId;
                        _sourceMap[nodeId].Add(element.Id);
                        continue;
                    }

                    var duplicated = nodeId;
                    nodeId = $"{componentId}_{element.Id}";
                    Add(DiagnosticCodes.DuplicateId, DiagnosticSeverity.Warning,
                        $"Dois componentes com o mesmo ID ('{duplicated}'); um deles foi renomeado.", [element.Id], [nodeId]);
                }

                var label = (BoundText(element) ?? Json.Str(data["label"]) ?? definition.ShortName ?? definition.DisplayName).Trim();
                var rawConfig = data["config"] as JsonObject;
                if (rawConfig is not null)
                {
                    foreach (var error in MiniSchema.Validate(rawConfig, definition.ConfigSchema))
                        Add(DiagnosticCodes.InvalidConfig, DiagnosticSeverity.Error, $"{label}: {error}", [element.Id], [nodeId]);
                }

                var config = MiniSchema.WithDefaults(rawConfig, definition.ConfigSchema);
                _nodes.Add(new Node(nodeId, definition.Id, definition.Archetype, label, config, new Placement(null, [])));
                _anchorOf[nodeId] = element;
                _definitionOf[nodeId] = definition;
                _ownerOf[element.Id] = nodeId;
                _sourceMap[nodeId] = [element.Id];

                if (definition.Provider != request.Provider && !definition.Shared)
                {
                    var equivalent = _catalog.DefaultFor(request.Provider, definition.Archetype);
                    Add(DiagnosticCodes.ForeignProvider, DiagnosticSeverity.Warning,
                        $"\"{label}\" é um serviço de {definition.Provider.DisplayName()}, mas esta tentativa é em {request.Provider.DisplayName()}." +
                        (equivalent is null ? "" : $" Equivalente: {equivalent.DisplayName}."),
                        [element.Id], [nodeId],
                        equivalent is null ? null : [new Suggestion(equivalent.Id, equivalent.DisplayName)]);
                }
            }
        }

        public void CollectOwnedElements()
        {
            var groupOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            var ambiguousGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (nodeId, anchor) in _anchorOf)
            {
                if (anchor.GroupIds.Count == 0) continue;
                var group = anchor.GroupIds[0];
                if (!groupOwner.TryAdd(group, nodeId) && groupOwner[group] != nodeId)
                    ambiguousGroups.Add(group);
            }

            foreach (var group in ambiguousGroups) groupOwner.Remove(group);

            foreach (var element in _elements)
            {
                if (_ownerOf.ContainsKey(element.Id)) continue;
                string? owner = null;
                if (element.Kind == "component" && Json.Str(element.CustomData!["componentId"]) is { } componentId && _anchorOf.ContainsKey(componentId))
                    owner = componentId;
                else if (element.ContainerId is { } container && _ownerOf.TryGetValue(container, out var containerOwner))
                    owner = containerOwner;
                else
                {
                    foreach (var group in element.GroupIds)
                    {
                        if (groupOwner.TryGetValue(group, out var groupNode))
                        {
                            owner = groupNode;
                            break;
                        }
                    }
                }

                if (owner is null) continue;
                _ownerOf[element.Id] = owner;
                _sourceMap[owner].Add(element.Id);
            }
        }

        public void CollectBoundaries()
        {
            foreach (var element in _elements)
            {
                var isBoundaryData = element.Kind == "boundary";
                if (!element.IsFrame && !isBoundaryData) continue;

                BoundaryType? type = null;
                string? region = null, zone = null;
                if (isBoundaryData)
                {
                    var data = element.CustomData!;
                    type = Json.Str(data["boundaryType"]) switch
                    {
                        "region" => BoundaryType.Region,
                        "zone" => BoundaryType.Zone,
                        "network" => BoundaryType.Network,
                        "subnet-public" => BoundaryType.SubnetPublic,
                        "subnet-private" => BoundaryType.SubnetPrivate,
                        _ => null,
                    };
                    region = Json.Str(data["region"]);
                    zone = Json.Str(data["zone"]);
                }
                else if (element.Name is { } name)
                {
                    (type, region, zone) = InferFromName(name);
                }

                if (type is null) continue;
                _boundaryElements.Add(element.Id);
                var members = _anchorOf
                    .Where(p => p.Value.FrameId == element.Id || (!element.IsFrame && element.Contains(p.Value)))
                    .Select(p => p.Key)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList();
                _boundaries.Add(new Boundary(element.Id, type.Value, region, zone, members));
                _sourceMap[element.Id] = [element.Id];
            }
        }

        private static (BoundaryType?, string?, string?) InferFromName(string name)
        {
            var normalized = LabelMatcher.Normalize(name);
            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return (null, null, null);
            var last = tokens[^1];
            return tokens[0] switch
            {
                "az" or "zona" or "zone" when tokens.Length >= 2 => (BoundaryType.Zone, null, last),
                "regiao" or "region" when tokens.Length >= 2 => (BoundaryType.Region, string.Join('-', tokens[1..]), null),
                "vpc" or "vnet" or "rede" => (BoundaryType.Network, null, null),
                "subrede" or "subnet" when normalized.Contains("public", StringComparison.Ordinal) => (BoundaryType.SubnetPublic, null, null),
                "subrede" or "subnet" => (BoundaryType.SubnetPrivate, null, null),
                _ => (null, null, null),
            };
        }

        public void ResolvePlacements()
        {
            for (var i = 0; i < _nodes.Count; i++)
            {
                var node = _nodes[i];
                var definition = _definitionOf[node.Id];
                var region = _boundaries.FirstOrDefault(b => b.Type == BoundaryType.Region && b.Members.Contains(node.Id))?.Region
                    ?? node.Cfg.String("region")
                    ?? _defaultRegion;

                var zones = _boundaries
                    .Where(b => b.Type == BoundaryType.Zone && b.Zone is not null && b.Members.Contains(node.Id))
                    .Select(b => b.Zone!)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToList();
                if (zones.Count == 0) zones = node.Cfg.Strings("zones").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                if (zones.Count == 0 && definition.Scope == ComponentScope.Zonal && !Archetypes.IsClient(node.Archetype) && !Archetypes.IsExternal(node.Archetype))
                    zones = node.Cfg.Bool("multiAz") ? ["a", "b"] : ["a"];

                _nodes[i] = node with { Placement = new Placement(region, zones) };
            }
        }

        public void CollectEdges()
        {
            var usedIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var arrow in _elements.Where(e => e.Type == "arrow"))
            {
                var start = arrow.StartElementId is { } s ? _ownerOf.GetValueOrDefault(s) : null;
                var end = arrow.EndElementId is { } t ? _ownerOf.GetValueOrDefault(t) : null;
                if (arrow.EndArrowhead is null && arrow.StartArrowhead is not null)
                    (start, end) = (end, start);

                var connection = arrow.Kind == "connection" ? arrow.CustomData : null;
                if (start is null || end is null)
                {
                    if (arrow.StartElementId is not null || arrow.EndElementId is not null || connection is not null)
                    {
                        Add(DiagnosticCodes.DanglingArrow, DiagnosticSeverity.Warning,
                            "Seta com uma das pontas solta ou ligada a algo que não é componente; ela não entra na simulação.",
                            [arrow.Id], []);
                    }

                    continue;
                }

                if (start == end) continue;

                var id = (connection is null ? null : Json.Str(connection["connectionId"])) ?? $"conn_{arrow.Id}";
                if (!usedIds.Add(id))
                {
                    id = $"{id}_{arrow.Id}";
                    usedIds.Add(id);
                }

                var from = _nodes.First(n => n.Id == start);
                var to = _nodes.First(n => n.Id == end);
                var interaction = InteractionRules.Parse(connection is null ? null : Json.Str(connection["interaction"]))
                    ?? InteractionRules.Infer(from.Archetype, to.Archetype);

                if (InteractionRules.Problem(interaction, from.Archetype, to.Archetype) is { } problem)
                {
                    Add(DiagnosticCodes.InvalidInteraction, DiagnosticSeverity.Error,
                        $"{from.Label} → {to.Label}: {problem}.", [arrow.Id], [id]);
                }

                var config = EdgeConfig.Default;
                if (connection?["config"] is JsonObject configJson)
                {
                    try
                    {
                        config = configJson.Deserialize<EdgeConfig>(LabJson.Options) ?? EdgeConfig.Default;
                    }
                    catch (JsonException ex)
                    {
                        Add(DiagnosticCodes.InvalidEdgeConfig, DiagnosticSeverity.Error,
                            $"{from.Label} → {to.Label}: configuração inválida ({ex.Message}).", [arrow.Id], [id]);
                    }
                }

                if (interaction == Interaction.DataAccess && (config.Ops is null || config.Ops.Count == 0))
                    config = config with { Ops = [new DataOp(DataOpKind.Read), new DataOp(DataOpKind.Write)] };

                _edges.Add(new Edge(id, from.Id, to.Id, interaction, config));
                _sourceMap[id] = [arrow.Id];
            }
        }

        public void CollectUntypedAndAnnotations()
        {
            var palette = _catalog.ForProvider(request.Provider).ToList();
            foreach (var shape in _elements.Where(e => e.IsShape && !_ownerOf.ContainsKey(e.Id) && !_boundaryElements.Contains(e.Id)))
            {
                var label = BoundText(shape);
                if (string.IsNullOrWhiteSpace(label)) continue;
                var matches = LabelMatcher.Match(label, palette);
                if (matches.Count > 0)
                {
                    Add(DiagnosticCodes.UntypedShape, DiagnosticSeverity.Warning,
                        $"\"{label.Trim()}\" não é um componente tipado. Mapear para {matches[0].Component.DisplayName}?",
                        [shape.Id], [],
                        matches.Select(m => new Suggestion(m.Component.Id, m.Component.DisplayName)).ToList());
                }
                else
                {
                    _annotations.Add(new Annotation(shape.Id, label.Trim(), Nearest(shape)));
                }
            }

            foreach (var text in _elements.Where(e => e.Type == "text" && e.ContainerId is null && !_ownerOf.ContainsKey(e.Id)))
            {
                if (!string.IsNullOrWhiteSpace(text.Text))
                    _annotations.Add(new Annotation(text.Id, text.Text.Trim(), Nearest(text)));
            }
        }

        public void CollectFlows()
        {
            var edgeIds = _edges.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var flow in request.Flows)
            {
                var steps = new List<string>(flow.Steps.Count);
                foreach (var step in flow.Steps)
                {
                    if (edgeIds.Contains(step)) steps.Add(step);
                    else
                    {
                        Add(DiagnosticCodes.MissingFlowStep, DiagnosticSeverity.Error,
                            $"Fluxo {flow.UseCaseId}: um passo aponta para uma conexão que não existe mais.", [], [step]);
                    }
                }

                _flows.Add(new Flow(flow.UseCaseId, steps));
            }
        }

        public void CheckStructure()
        {
            foreach (var node in _nodes)
            {
                if (Archetypes.IsPassive(node.Archetype)) continue;
                if (!_edges.Any(e => e.From == node.Id || e.To == node.Id))
                {
                    Add(DiagnosticCodes.Isolated, DiagnosticSeverity.Warning,
                        $"\"{node.Label}\" não está conectado a nada.", _sourceMap[node.Id], [node.Id]);
                }
            }

            if (_boundaries.Any(b => b.Type == BoundaryType.Region))
            {
                foreach (var node in _nodes.Where(n => !Archetypes.IsClient(n.Archetype) && !Archetypes.IsExternal(n.Archetype)))
                {
                    if (!_boundaries.Any(b => b.Type == BoundaryType.Region && b.Members.Contains(node.Id)))
                    {
                        Add(DiagnosticCodes.OutsideRegion, DiagnosticSeverity.Warning,
                            $"\"{node.Label}\" está fora de qualquer região desenhada; assumida a região {_defaultRegion}.",
                            _sourceMap[node.Id], [node.Id]);
                    }
                }
            }

            foreach (var cycle in SyncCycles())
            {
                Add(DiagnosticCodes.SyncCycle, DiagnosticSeverity.Error,
                    "Ciclo de chamadas síncronas: " + string.Join(" → ", cycle.Select(id => _nodes.First(n => n.Id == id).Label)) + ".",
                    [], cycle);
            }
        }

        private IEnumerable<List<string>> SyncCycles()
        {
            var adjacency = _edges
                .Where(e => e.Interaction == Interaction.SyncRequest)
                .GroupBy(e => e.From)
                .ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<string>();
            var cycles = new List<List<string>>();

            void Visit(string node)
            {
                state[node] = 1;
                stack.Add(node);
                foreach (var next in adjacency.GetValueOrDefault(node) ?? [])
                {
                    var nextState = state.GetValueOrDefault(next);
                    if (nextState == 0) Visit(next);
                    else if (nextState == 1) cycles.Add([.. stack.Skip(stack.IndexOf(next)), next]);
                }

                stack.RemoveAt(stack.Count - 1);
                state[node] = 2;
            }

            foreach (var node in adjacency.Keys.Order(StringComparer.Ordinal))
            {
                if (state.GetValueOrDefault(node) == 0) Visit(node);
            }

            return cycles;
        }

        public CompilationResult Build()
        {
            var graph = new ArchitectureGraph
            {
                Provider = request.Provider,
                CatalogVersion = _catalog.Version,
                Nodes = _nodes.OrderBy(n => n.Id, StringComparer.Ordinal).ToList(),
                Edges = _edges.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),
                Boundaries = _boundaries.OrderBy(b => b.Id, StringComparer.Ordinal).ToList(),
                Flows = _flows.OrderBy(f => f.UseCaseId, StringComparer.Ordinal).ToList(),
                Annotations = _annotations.OrderBy(a => a.Id, StringComparer.Ordinal).ToList(),
                SourceMap = _sourceMap
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value.Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal),
            };
            return new CompilationResult(graph, _diagnostics, Canonical.Hash(graph));
        }

        private string? BoundText(SceneElement container) =>
            _elements.FirstOrDefault(e => e.Type == "text" && e.ContainerId == container.Id)?.Text;

        private string? Nearest(SceneElement element)
        {
            string? best = null;
            var bestDistance = 400.0 * 400.0;
            foreach (var (nodeId, anchor) in _anchorOf)
            {
                var dx = anchor.CenterX - element.CenterX;
                var dy = anchor.CenterY - element.CenterY;
                var distance = (dx * dx) + (dy * dy);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = nodeId;
                }
            }

            return best;
        }

        private void Add(string code, DiagnosticSeverity severity, string message, IReadOnlyList<string> elementIds,
            IReadOnlyList<string> irIds, IReadOnlyList<Suggestion>? suggestions = null) =>
            _diagnostics.Add(new Diagnostic(code, severity, message, elementIds, irIds, suggestions));
    }
}
