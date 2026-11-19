using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lab.Ir;

/// <summary>JSON canônico (chaves ordenadas) e hash estável, usados no manifesto e em caches.</summary>
public static class Canonical
{
    public static string Serialize<T>(T value)
    {
        var node = JsonSerializer.SerializeToNode(value, LabJson.Options);
        return Sort(node)?.ToJsonString() ?? "null";
    }

    public static string Hash<T>(T value) => HashText(Serialize(value));

    public static string HashText(string text) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
        _ => node?.DeepClone(),
    };
}
