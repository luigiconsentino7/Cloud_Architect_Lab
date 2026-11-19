using System.Text.Json.Nodes;

namespace Lab.Ir.Scene;

/// <summary>Visão mínima de um elemento da cena do Excalidraw (só o que o compilador usa).</summary>
public sealed record SceneElement
{
    public required string Id { get; init; }
    public required string Type { get; init; }
    public bool IsDeleted { get; init; }
    public IReadOnlyList<string> GroupIds { get; init; } = [];
    public string? FrameId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public string? ContainerId { get; init; }
    public string? Text { get; init; }
    public string? Name { get; init; }
    public string? StartElementId { get; init; }
    public string? EndElementId { get; init; }
    public string? StartArrowhead { get; init; }
    public string? EndArrowhead { get; init; }
    public string StrokeStyle { get; init; } = "solid";
    public JsonObject? CustomData { get; init; }

    public string? Kind => Json.Str(CustomData?["kind"]);

    public double CenterX => X + (Width / 2);

    public double CenterY => Y + (Height / 2);

    public bool IsShape => Type is "rectangle" or "ellipse" or "diamond";

    public bool IsFrame => Type is "frame" or "magicframe";

    public bool Contains(SceneElement other) =>
        other.X >= X && other.Y >= Y && other.X + other.Width <= X + Width && other.Y + other.Height <= Y + Height;

    public static IReadOnlyList<SceneElement> ParseAll(JsonNode? scene)
    {
        var elements = scene switch
        {
            JsonArray array => array,
            JsonObject obj when obj["elements"] is JsonArray array => array,
            _ => null,
        };
        if (elements is null) return [];

        var result = new List<SceneElement>(elements.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elements)
        {
            if (Parse(item) is { } element && seen.Add(element.Id))
                result.Add(element);
        }

        return result;
    }

    private static SceneElement? Parse(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var id = Json.Str(obj["id"]);
        var type = Json.Str(obj["type"]);
        if (id is null || type is null) return null;

        return new SceneElement
        {
            Id = id,
            Type = type,
            IsDeleted = Json.Bool(obj["isDeleted"]),
            GroupIds = obj["groupIds"] is JsonArray groups ? groups.Select(Json.Str).OfType<string>().ToList() : [],
            FrameId = Json.Str(obj["frameId"]),
            X = Json.Num(obj["x"]),
            Y = Json.Num(obj["y"]),
            Width = Math.Abs(Json.Num(obj["width"])),
            Height = Math.Abs(Json.Num(obj["height"])),
            ContainerId = Json.Str(obj["containerId"]),
            Text = Json.Str(obj["text"]) ?? Json.Str(obj["originalText"]),
            Name = Json.Str(obj["name"]),
            StartElementId = Json.Str((obj["startBinding"] as JsonObject)?["elementId"]),
            EndElementId = Json.Str((obj["endBinding"] as JsonObject)?["elementId"]),
            StartArrowhead = Json.Str(obj["startArrowhead"]),
            EndArrowhead = Json.Str(obj["endArrowhead"]),
            StrokeStyle = Json.Str(obj["strokeStyle"]) ?? "solid",
            CustomData = obj["customData"] as JsonObject,
        };
    }
}

internal static class Json
{
    public static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public static double Num(JsonNode? node, double fallback = 0) =>
        node is JsonValue value && NodeConfig.ToDouble(value) is { } d ? d : fallback;

    public static bool Bool(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var b) && b;
}
