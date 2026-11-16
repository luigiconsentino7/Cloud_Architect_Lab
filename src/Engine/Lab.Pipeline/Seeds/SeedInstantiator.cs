using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Scene;
using Lab.Ir.Seeds;
using Lab.Ir.Templating;
using Lab.Simulation.Core;

namespace Lab.Pipeline.Seeds;

public sealed class SeedException(string message) : Exception(message);

public sealed record InstantiationRequest
{
    public required Seed Seed { get; init; }
    public CloudProvider Provider { get; init; } = CloudProvider.Aws;
    public Difficulty Difficulty { get; init; } = Difficulty.Senior;
    public GameMode Mode { get; init; } = GameMode.Treino;

    /// <summary>Valores escolhidos para as variáveis (precisam estar na faixa da dificuldade).</summary>
    public IReadOnlyDictionary<string, double>? Variables { get; init; }

    public IReadOnlyList<string> Twists { get; init; } = [];

    /// <summary>Sorteia as variáveis não informadas dentro da faixa (determinístico pela semente). Nulo = valor padrão.</summary>
    public long? VariationSeed { get; init; }

    public long? MasterSeed { get; init; }
    public string? ChallengeId { get; init; }
}

public sealed record Instantiation(ChallengeSpec Challenge, IReadOnlyList<Diagnostic> ReferenceDiagnostics);

/// <summary>
/// Semente + variação + provedor → desafio. Aplica as pegadinhas, avalia os templates, converte unidades e compila a
/// solução de referência pelo mesmo compilador do desenho do usuário, já no provedor escolhido.
/// </summary>
public static partial class SeedInstantiator
{
    private static readonly string[] AuthoringKeys = ["id", "version", "difficulties", "variables", "twists", "reference"];

    [GeneratedRegex(@"^(?<name>[A-Za-z0-9_-]+)(\[(?<selector>[^\]]+)\])?$", RegexOptions.CultureInvariant)]
    private static partial Regex PathSegment();

    public static Instantiation Instantiate(InstantiationRequest request, ComponentCatalog catalog) =>
        EngineCulture.Run(() => InstantiateCore(request, catalog));

    /// <summary>Valores das variáveis para a dificuldade, com as faixas validadas.</summary>
    public static IReadOnlyDictionary<string, double> ResolveVariables(InstantiationRequest request)
    {
        var seed = request.Seed;
        var overrides = request.Variables ?? new Dictionary<string, double>();
        var unknown = overrides.Keys.Where(k => !seed.Variables.ContainsKey(k)).ToList();
        if (unknown.Count > 0) throw new SeedException($"{seed.Id}: variáveis desconhecidas: {string.Join(", ", unknown)}.");

        var values = new SortedDictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, spec) in seed.Variables.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var range = spec.Ranges.TryGetValue(request.Difficulty, out var r) && r.Length == 2 ? r : null;
            double value;
            if (overrides.TryGetValue(name, out var chosen)) value = chosen;
            else if (request.VariationSeed is { } variation && range is not null)
                value = Snap(range[0] + (Rng.For(variation, $"var:{name}").NextDouble() * (range[1] - range[0])), range, spec.Step);
            else value = range is not null && (spec.Default < range[0] || spec.Default > range[1])
                ? Snap((range[0] + range[1]) / 2, range, spec.Step)
                : spec.Default;

            if (range is not null && (value < range[0] - 1e-9 || value > range[1] + 1e-9))
            {
                throw new SeedException(
                    $"{seed.Id}: {name} = {value} fora da faixa de {request.Difficulty} [{range[0]}, {range[1]}].");
            }

            values[name] = value;
        }

        values["difficultyLevel"] = (int)request.Difficulty + 1;
        return values;
    }

    private static Instantiation InstantiateCore(InstantiationRequest request, ComponentCatalog catalog)
    {
        var seed = request.Seed;
        if (seed.Difficulties.Count > 0 && !seed.Difficulties.Contains(request.Difficulty))
            throw new SeedException($"{seed.Id}: a semente não tem a dificuldade {request.Difficulty}.");
        if (request.Provider == CloudProvider.Generic)
            throw new SeedException($"{seed.Id}: escolha um provedor de nuvem (AWS, Azure ou GCP).");

        var variables = ResolveVariables(request);
        var twists = ResolveTwists(seed, request);

        var body = (JsonObject)seed.Body.DeepClone();
        foreach (var twist in twists)
        {
            foreach (var op in twist.Ops) Apply(body, op, $"{seed.Id}/{twist.Id}");
        }

        var referenceSource = body["reference"]?.DeepClone() as JsonObject;
        foreach (var key in AuthoringKeys) body.Remove(key);

        var errors = new List<string>();
        var rendered = Render(body, variables, seed.Id, errors);
        var publicVariables = variables.Where(p => p.Key != "difficultyLevel").ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var masterSeed = request.MasterSeed ?? DeriveMasterSeed(seed, request, publicVariables, twists);
        rendered["id"] = request.ChallengeId ?? $"chl_{(ulong)masterSeed:x16}";
        rendered["seedId"] = seed.Id;
        rendered["seedVersion"] = seed.Version;
        rendered["title"] ??= seed.Title;
        rendered["summary"] ??= seed.Summary;
        rendered["track"] ??= seed.Track;
        rendered["domain"] ??= seed.Domain;
        rendered["difficulty"] = JsonSerializer.SerializeToNode(request.Difficulty, LabJson.Options);
        rendered["provider"] = JsonSerializer.SerializeToNode(request.Provider, LabJson.Options);
        rendered["mode"] = JsonSerializer.SerializeToNode(request.Mode, LabJson.Options);
        rendered["variables"] = JsonSerializer.SerializeToNode(publicVariables, LabJson.Options);
        rendered["twists"] = new JsonArray(twists.Select(t => (JsonNode)JsonValue.Create(t.Id)).ToArray());
        rendered["masterSeed"] = masterSeed;
        if (errors.Count > 0) throw new SeedException(string.Join(Environment.NewLine, errors));

        ChallengeSpec challenge;
        try
        {
            challenge = rendered.Deserialize<ChallengeSpec>(LabJson.Options)
                ?? throw new SeedException($"{seed.Id}: desafio vazio.");
        }
        catch (JsonException ex)
        {
            throw new SeedException($"{seed.Id}: {ex.Message}");
        }

        if (referenceSource is null) return new Instantiation(challenge, []);
        var reference = Render(referenceSource, variables, $"{seed.Id}/reference", errors);
        if (errors.Count > 0) throw new SeedException(string.Join(Environment.NewLine, errors));
        var (scene, flows) = ReferenceScene(reference, request.Provider, catalog, seed.Id);
        var compilation = SceneCompiler.Compile(new SceneCompileRequest
        {
            Scene = scene,
            Flows = flows,
            Provider = request.Provider,
            Catalog = catalog,
            Challenge = challenge,
        });
        return new Instantiation(
            challenge with { Reference = compilation.Graph, ReferenceRationale = Str(reference["rationale"]) ?? "" },
            compilation.Diagnostics);
    }

    private static List<TwistSpec> ResolveTwists(Seed seed, InstantiationRequest request)
    {
        var twists = new List<TwistSpec>();
        foreach (var id in request.Twists.Distinct(StringComparer.Ordinal))
        {
            var twist = seed.Twists.FirstOrDefault(t => t.Id == id)
                ?? throw new SeedException($"{seed.Id}: pegadinha desconhecida '{id}'.");
            if (twist.MinDifficulty > request.Difficulty)
                throw new SeedException($"{seed.Id}: a pegadinha '{id}' exige dificuldade {twist.MinDifficulty} ou maior.");
            twists.Add(twist);
        }

        return twists;
    }

    private static JsonObject Render(JsonObject source, IReadOnlyDictionary<string, double> variables, string name, List<string> errors)
    {
        JsonNode? rendered;
        try
        {
            rendered = TemplateRenderer.Render(source, variables);
        }
        catch (Exception ex) when (ex is FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SeedException($"{name}: template inválido: {ex.Message}");
        }

        return (JsonObject)UnitNormalizer.Normalize(rendered, errors)!;
    }

    /// <summary>
    /// Converte a referência (nós por arquétipo ou componente, setas, fluxos) numa cena no formato do canvas.
    /// <c>component</c> pode ser um ID ou um mapa por provedor; sem ele, usa o componente padrão do arquétipo.
    /// </summary>
    private static (JsonObject Scene, IReadOnlyList<FlowDefinition> Flows) ReferenceScene(
        JsonObject reference, CloudProvider provider, ComponentCatalog catalog, string seedId)
    {
        var writer = new SceneWriter { CatalogVersion = catalog.Version };
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var x = 0;
        foreach (var item in reference["nodes"] as JsonArray ?? [])
        {
            if (item is not JsonObject node) continue;
            var id = Str(node["id"]) ?? throw new SeedException($"{seedId}: nó da referência sem 'id'.");
            node = ForProvider(node, provider);
            var label = Str(node["label"]) ?? id;
            var external = Str(node["external"]);
            var catalogId = external is not null ? "external.system" : ResolveComponent(node, provider, catalog, seedId, id);
            var config = node["config"]?.DeepClone() as JsonObject;
            ids[id] = writer.Component(catalogId, label, x: x, y: 0, config: config, id: id, externalId: external);
            x += 220;
        }

        foreach (var item in reference["edges"] as JsonArray ?? [])
        {
            if (item is not JsonObject edge) continue;
            var id = Str(edge["id"]) ?? throw new SeedException($"{seedId}: seta da referência sem 'id'.");
            var from = Str(edge["from"]) ?? throw new SeedException($"{seedId}: seta {id} sem 'from'.");
            var to = Str(edge["to"]) ?? throw new SeedException($"{seedId}: seta {id} sem 'to'.");
            try
            {
                writer.Arrow(ids.GetValueOrDefault(from, from), ids.GetValueOrDefault(to, to),
                    interaction: Str(edge["interaction"]), config: edge["config"]?.DeepClone() as JsonObject, id: id);
            }
            catch (ArgumentException ex)
            {
                throw new SeedException($"{seedId}: seta {id}: {ex.Message}");
            }
        }

        var flows = new List<FlowDefinition>();
        if (reference["flows"] is JsonObject flowObject)
        {
            foreach (var (useCase, steps) in flowObject)
            {
                var list = (steps as JsonArray ?? []).Select(s => Str(s) ?? "").Where(s => s.Length > 0).ToList();
                flows.Add(new FlowDefinition(useCase, list));
            }
        }

        return (writer.Build(), flows);
    }

    /// <summary>
    /// Aplica <c>providers.&lt;código&gt;</c> (ex.: <c>providers.azure: { component: azure.cosmosdb, config: {...} }</c>) quando o
    /// equivalente direto não existe naquele provedor. <c>component</c>/<c>archetype</c> trocam juntos; <c>config</c> substitui.
    /// </summary>
    private static JsonObject ForProvider(JsonObject node, CloudProvider provider)
    {
        if (node["providers"]?[provider.Code()] is not JsonObject over) return node;
        var merged = (JsonObject)node.DeepClone();
        merged.Remove("providers");
        if (over["component"] is not null || over["archetype"] is not null)
        {
            merged.Remove("component");
            merged.Remove("archetype");
        }

        foreach (var (key, value) in over) merged[key] = value?.DeepClone();
        return merged;
    }

    private static string ResolveComponent(JsonObject node, CloudProvider provider, ComponentCatalog catalog, string seedId, string nodeId)
    {
        var providerCode = provider.Code();
        var component = node["component"] switch
        {
            JsonObject byProvider => Str(byProvider[providerCode]),
            var value => Str(value),
        };

        if (component is not null && catalog.Find(component) is { } definition)
        {
            if (definition.Shared || definition.Provider == provider) return definition.Id;
            return catalog.DefaultFor(provider, definition.Archetype)?.Id
                ?? throw new SeedException($"{seedId}: {nodeId}: sem equivalente de {definition.Archetype} em {provider.DisplayName()}.");
        }

        if (component is not null && node["component"] is not JsonObject)
            throw new SeedException($"{seedId}: {nodeId}: componente '{component}' não existe no catálogo.");

        var archetype = Str(node["archetype"])
            ?? (node["component"] is JsonObject map
                ? map.Select(p => catalog.Find(Str(p.Value) ?? "")?.Archetype).FirstOrDefault(a => a is not null)
                : null)
            ?? throw new SeedException($"{seedId}: {nodeId}: informe 'archetype' ou 'component'.");
        return catalog.DefaultFor(provider, archetype)?.Id
            ?? throw new SeedException($"{seedId}: {nodeId}: nenhum componente de {provider.DisplayName()} para {archetype}.");
    }

    /// <summary>
    /// Pegadinha: <c>merge</c> mescla um objeto, <c>append</c> adiciona a uma lista, <c>set</c> troca o valor. O caminho usa
    /// pontos e seletores por <c>id</c> ou índice: <c>externalSystems[external:spi].latency</c>, <c>testPlan.scenarios</c>.
    /// </summary>
    public static void Apply(JsonObject body, TwistOp op, string source)
    {
        var segments = op.Path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new SeedException($"{source}: caminho vazio.");
        JsonNode parent = body;
        for (var i = 0; i < segments.Length; i++)
        {
            var match = PathSegment().Match(segments[i]);
            if (!match.Success) throw new SeedException($"{source}: trecho de caminho inválido '{segments[i]}'.");
            var name = match.Groups["name"].Value;
            var selector = match.Groups["selector"].Success ? match.Groups["selector"].Value : null;
            var last = i == segments.Length - 1;
            if (parent is not JsonObject obj) throw new SeedException($"{source}: '{segments[i]}' não está dentro de um objeto.");

            if (selector is null)
            {
                if (last)
                {
                    SetOrMerge(obj, name, op, source);
                    return;
                }

                if (obj[name] is null) obj[name] = new JsonObject();
                parent = obj[name]!;
                continue;
            }

            if (obj[name] is not JsonArray array) throw new SeedException($"{source}: '{name}' não é uma lista.");
            var element = int.TryParse(selector, out var index) && index >= 0 && index < array.Count
                ? array[index]
                : array.FirstOrDefault(e => e is JsonObject o && Str(o["id"]) == selector);
            if (element is null) throw new SeedException($"{source}: '{name}[{selector}]' não existe.");
            if (last)
            {
                if (op.Op == "merge" && element is JsonObject target && op.Value is JsonObject patch) Merge(target, patch);
                else if (op.Op == "set") array[array.IndexOf(element)] = op.Value?.DeepClone();
                else throw new SeedException($"{source}: operação '{op.Op}' inválida para '{segments[i]}'.");
                return;
            }

            parent = element;
        }
    }

    private static void SetOrMerge(JsonObject obj, string name, TwistOp op, string source)
    {
        switch (op.Op)
        {
            case "set":
                obj[name] = op.Value?.DeepClone();
                break;
            case "merge" when op.Value is JsonObject patch:
                if (obj[name] is not JsonObject target)
                {
                    target = new JsonObject();
                    obj[name] = target;
                }

                Merge(target, patch);
                break;
            case "append":
                if (obj[name] is not JsonArray array)
                {
                    array = [];
                    obj[name] = array;
                }

                if (op.Value is JsonArray many) foreach (var item in many) array.Add(item?.DeepClone());
                else array.Add(op.Value?.DeepClone());
                break;
            default:
                throw new SeedException($"{source}: operação '{op.Op}' inválida (use merge, append ou set).");
        }
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = value?.DeepClone();
        }
    }

    private static double Snap(double value, double[] range, double step)
    {
        var snapped = step > 0 ? range[0] + (Math.Round((value - range[0]) / step) * step) : value;
        return Math.Clamp(Math.Round(snapped, 6), range[0], range[1]);
    }

    private static long DeriveMasterSeed(Seed seed, InstantiationRequest request, IReadOnlyDictionary<string, double> variables, IEnumerable<TwistSpec> twists)
    {
        var key = Canonical.Serialize(new
        {
            seed = seed.Id,
            version = seed.Version,
            difficulty = request.Difficulty,
            provider = request.Provider,
            variables,
            twists = twists.Select(t => t.Id).Order(StringComparer.Ordinal).ToList(),
            variation = request.VariationSeed,
        });
        return (long)(Rng.Fnv1a(key) & 0x7FFF_FFFF_FFFF_FFFF);
    }

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
