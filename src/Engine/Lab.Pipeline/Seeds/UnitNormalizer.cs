using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lab.Pipeline.Seeds;

/// <summary>
/// Converte valores com unidade escritos nas sementes (<c>120s</c>, <c>700ms</c>, <c>5min</c>, <c>99.95%</c>) para os números
/// que o motor usa. A unidade de destino vem do nome do campo: <c>duration</c>, <c>warmup</c>, <c>rampFor</c>, <c>holdFor</c>,
/// <c>at</c>, <c>for</c> e campos terminados em <c>Seconds</c> são em segundos; os demais tempos, em milissegundos.
/// </summary>
public static partial class UnitNormalizer
{
    private static readonly HashSet<string> SecondKeys = new(StringComparer.Ordinal) { "duration", "warmup", "rampFor", "holdFor", "at", "for" };

    [GeneratedRegex(@"^\s*(?<value>-?\d+(?:\.\d+)?)\s*(?<unit>ms|s|min|h|%)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex WithUnit();

    [GeneratedRegex(@"^\s*-?\d{1,3}(?:\.\d{3})+\s*(ms|s|min|h|%)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ThousandsSeparator();

    public static JsonNode? Normalize(JsonNode? node, List<string> errors, string path = "") => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Convert(p.Key, p.Value, errors, Join(path, p.Key))))),
        JsonArray array => new JsonArray(array.Select((item, i) => Normalize(item, errors, $"{path}[{i}]")).ToArray()),
        _ => node?.DeepClone(),
    };

    private static JsonNode? Convert(string key, JsonNode? value, List<string> errors, string path)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text)) return Normalize(value, errors, path);
        if (ThousandsSeparator().IsMatch(text))
        {
            errors.Add($"{path}: \"{text}\" parece usar separador de milhar; escreva o número sem ele (ex.: 1500ms).");
            return value.DeepClone();
        }

        var match = WithUnit().Match(text);
        if (!match.Success) return value.DeepClone();
        var number = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        var seconds = SecondKeys.Contains(key) || key.EndsWith("Seconds", StringComparison.Ordinal);
        var converted = match.Groups["unit"].Value switch
        {
            "%" => number / 100,
            "ms" => seconds ? number / 1000 : number,
            "s" => seconds ? number : number * 1000,
            "min" => seconds ? number * 60 : number * 60_000,
            _ => seconds ? number * 3600 : number * 3_600_000,
        };
        return JsonValue.Create(Math.Abs(converted - Math.Round(converted)) < 1e-9 ? Math.Round(converted) : converted);
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
}
