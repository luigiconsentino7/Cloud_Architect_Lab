using System.Text.Json.Nodes;
using Lab.Ai;
using Lab.Ir;
using Lab.Ir.Scene;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Shared;

namespace Lab.Modules.Design;

/// <param name="Source"><c>catalogo</c> (alias do catálogo) ou <c>ia</c> (sugestão da IA local).</param>
public sealed record MappingOption(string CatalogId, string DisplayName, string Source, string? Reason = null);

/// <param name="Kind"><c>sem-tipo</c> (o rótulo casou com um alias) ou <c>anotacao</c> (não casou: pode ser texto livre).</param>
public sealed record LabelMappingView(string ElementId, string Label, string Kind, IReadOnlyList<MappingOption> Options);

/// <summary>
/// Sugestões para tipar formas desenhadas à mão: os aliases do catálogo sempre; para as que não casaram, a IA local,
/// quando ligada, escolhe um serviço da paleta do provedor (ou diz que é só anotação).
/// </summary>
public sealed class LabelMappingService(AttemptService attempts, IChallengeQueries challenges, LabContent content, ILabelMapper mapper)
{
    /// <summary>Cada consulta à IA leva alguns segundos: no máximo estas por pedido.</summary>
    public const int MaxAiLookups = 8;

    public async Task<IReadOnlyList<LabelMappingView>> SuggestAsync(AttemptEntity attempt, CancellationToken cancellationToken)
    {
        var spec = await challenges.SpecAsync(attempt.ChallengeId, cancellationToken)
            ?? throw new InvalidOperationException("Desafio da tentativa não existe.");
        var compilation = attempts.Compile(attempt, spec);
        var labels = BoundLabels(attempt.DraftSceneJson);
        var palette = content.Catalog.ForProvider(spec.Provider)
            .Where(c => !Archetypes.IsClient(c.Archetype) && !Archetypes.IsExternal(c.Archetype))
            .ToList();
        var nodeLabels = compilation.Graph.Nodes.ToDictionary(n => n.Id, n => n.Label, StringComparer.Ordinal);

        var views = new List<LabelMappingView>();
        foreach (var diagnostic in compilation.Diagnostics.Where(d => d.Code == DiagnosticCodes.UntypedShape))
        {
            var options = (diagnostic.Suggestions ?? []).Select(s => new MappingOption(s.CatalogId, s.DisplayName, "catalogo")).ToList();
            foreach (var elementId in diagnostic.ElementIds)
                views.Add(new LabelMappingView(elementId, labels.GetValueOrDefault(elementId, ""), "sem-tipo", options));
        }

        var candidates = palette.Select(c => new MappingCandidate(c.Id, c.DisplayName, c.Archetype)).ToList();
        var asked = 0;
        foreach (var annotation in compilation.Graph.Annotations)
        {
            var options = new List<MappingOption>();
            if (asked++ < MaxAiLookups)
            {
                var near = annotation.Near is { } nodeId && nodeLabels.TryGetValue(nodeId, out var label) ? label : null;
                if (await mapper.SuggestAsync(new LabelMappingRequest(annotation.Text, near, candidates), cancellationToken) is { } suggestion)
                {
                    var component = palette.First(c => c.Id == suggestion.CatalogId);
                    options.Add(new MappingOption(component.Id, component.DisplayName, "ia", suggestion.Reason));
                }
            }

            views.Add(new LabelMappingView(annotation.Id, annotation.Text, "anotacao", options));
        }

        return views;
    }

    /// <summary>Texto vinculado de cada forma da cena (o rótulo que o usuário escreveu dentro dela).</summary>
    public static IReadOnlyDictionary<string, string> BoundLabels(string sceneJson)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (JsonNode.Parse(sceneJson)?["elements"] is not JsonArray elements) return labels;
        foreach (var element in elements.OfType<JsonObject>())
        {
            if (element["type"]?.GetValue<string>() != "text" || element["isDeleted"]?.GetValue<bool>() == true) continue;
            if (element["containerId"] is JsonValue container && container.TryGetValue<string>(out var id)
                && element["text"] is JsonValue text && text.TryGetValue<string>(out var value))
                labels[id] = value.Trim();
        }

        return labels;
    }
}
