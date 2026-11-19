using System.Globalization;
using System.Text.Json.Nodes;

namespace Lab.Ir;

/// <summary>Acesso tipado, com caminhos "a.b.c", à configuração de um componente.</summary>
public readonly struct NodeConfig(JsonObject? root)
{
    public JsonNode? Get(string path)
    {
        JsonNode? current = root;
        foreach (var part in path.Split('.'))
        {
            if (current is not JsonObject obj || !obj.TryGetPropertyValue(part, out current))
                return null;
        }

        return current;
    }

    public double? Double(string path) => Get(path) is JsonValue value ? ToDouble(value) : null;

    public double Double(string path, double fallback) => Double(path) ?? fallback;

    public int Int(string path, int fallback) => Double(path) is { } d ? (int)Math.Round(d) : fallback;

    public bool Bool(string path, bool fallback = false) =>
        Get(path) is JsonValue value && value.TryGetValue<bool>(out var b) ? b : fallback;

    public string? String(string path) =>
        Get(path) is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    public IReadOnlyList<string> Strings(string path)
    {
        if (Get(path) is not JsonArray array) return [];
        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                result.Add(s);
        }

        return result;
    }

    public static double? ToDouble(JsonValue value)
    {
        if (value.TryGetValue<double>(out var d)) return d;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<decimal>(out var m)) return (double)m;
        if (value.TryGetValue<string>(out var s) &&
            double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        return null;
    }
}
