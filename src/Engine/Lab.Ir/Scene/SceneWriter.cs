using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lab.Ir.Scene;

/// <summary>
/// Monta cenas no formato do Excalidraw, com o mesmo customData que o frontend grava. Usado para converter a
/// solução de referência das sementes em cena (e daí em IR, pelo mesmo compilador do desenho do usuário) e nos testes.
/// </summary>
public sealed class SceneWriter
{
    private readonly List<JsonObject> _elements = [];
    private readonly Dictionary<string, string> _anchorOf = new(StringComparer.Ordinal);
    private int _sequence;

    /// <summary>Versão do catálogo gravada em cada componente (só informativa para o compilador).</summary>
    public string CatalogVersion { get; init; } = "2026.11.0";

    public string Component(string catalogId, string label, double x = 0, double y = 0, object? config = null,
        string? id = null, string? externalId = null, string? frameId = null)
    {
        var componentId = id ?? $"cmp_{++_sequence:00}";
        var anchorId = $"el_{componentId}";
        var groupId = $"g_{componentId}";
        var data = new JsonObject
        {
            ["kind"] = "component",
            ["componentId"] = componentId,
            ["catalogId"] = catalogId,
            ["role"] = "anchor",
            ["catalogVersion"] = CatalogVersion,
        };
        if (config is not null) data["config"] = config as JsonNode ?? JsonSerializer.SerializeToNode(config, LabJson.Options);
        if (externalId is not null) data["externalId"] = externalId;

        _elements.Add(Element(anchorId, "rectangle", x, y, 160, 80, groupId, frameId, customData: data));
        _elements.Add(Element($"txt_{componentId}", "text", x + 10, y + 30, 140, 20, groupId, frameId, containerId: anchorId, text: label));
        var nodeId = externalId ?? componentId;
        _anchorOf[nodeId] = anchorId;
        return nodeId;
    }

    public string Arrow(string from, string to, string? interaction = null, object? config = null, string? id = null,
        bool dashed = false, bool reversedHeads = false)
    {
        var connectionId = id ?? $"conn_{++_sequence:00}";
        var data = new JsonObject { ["kind"] = "connection", ["connectionId"] = connectionId };
        if (interaction is not null) data["interaction"] = interaction;
        if (config is not null) data["config"] = config as JsonNode ?? JsonSerializer.SerializeToNode(config, LabJson.Options);

        var arrow = Element($"arrow_{connectionId}", "arrow", 0, 0, 100, 0, null, null, customData: data);
        arrow["startBinding"] = new JsonObject { ["elementId"] = Anchor(reversedHeads ? to : from) };
        arrow["endBinding"] = new JsonObject { ["elementId"] = Anchor(reversedHeads ? from : to) };
        arrow["strokeStyle"] = dashed ? "dashed" : "solid";
        arrow["startArrowhead"] = reversedHeads ? "arrow" : null;
        arrow["endArrowhead"] = reversedHeads ? null : "arrow";
        _elements.Add(arrow);
        return connectionId;
    }

    public string LooseArrowFrom(string from)
    {
        var id = $"loose_{++_sequence:00}";
        var arrow = Element(id, "arrow", 0, 0, 100, 0, null, null);
        arrow["startBinding"] = new JsonObject { ["elementId"] = Anchor(from) };
        arrow["endArrowhead"] = "arrow";
        _elements.Add(arrow);
        return id;
    }

    public string Shape(string label, double x = 500, double y = 500)
    {
        var id = $"shape_{++_sequence:00}";
        _elements.Add(Element(id, "rectangle", x, y, 160, 80, null, null));
        _elements.Add(Element($"{id}_txt", "text", x + 10, y + 30, 140, 20, null, null, containerId: id, text: label));
        return id;
    }

    public string Note(string text, double x = 900, double y = 900)
    {
        var id = $"note_{++_sequence:00}";
        _elements.Add(Element(id, "text", x, y, 200, 20, null, null, text: text));
        return id;
    }

    public string Frame(string name, double x, double y, double width, double height)
    {
        var id = $"frame_{++_sequence:00}";
        var frame = Element(id, "frame", x, y, width, height, null, null);
        frame["name"] = name;
        _elements.Add(frame);
        return id;
    }

    public bool Contains(string nodeId) => _anchorOf.ContainsKey(nodeId);

    public JsonObject Build(bool reverse = false)
    {
        var elements = new JsonArray();
        foreach (var element in reverse ? Enumerable.Reverse(_elements) : _elements)
            elements.Add(element.DeepClone());
        return new JsonObject { ["type"] = "excalidraw", ["version"] = 2, ["elements"] = elements };
    }

    private string Anchor(string nodeId) =>
        _anchorOf.TryGetValue(nodeId, out var anchor) ? anchor : throw new ArgumentException($"Componente '{nodeId}' não existe na cena.", nameof(nodeId));

    private static JsonObject Element(string id, string type, double x, double y, double width, double height,
        string? groupId, string? frameId, JsonObject? customData = null, string? containerId = null, string? text = null)
    {
        var element = new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["isDeleted"] = false,
            ["groupIds"] = groupId is null ? new JsonArray() : new JsonArray(groupId),
            ["frameId"] = frameId,
        };
        if (customData is not null) element["customData"] = customData;
        if (containerId is not null) element["containerId"] = containerId;
        if (text is not null) element["text"] = text;
        return element;
    }
}
