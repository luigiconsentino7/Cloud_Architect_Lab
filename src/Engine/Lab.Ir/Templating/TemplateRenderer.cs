using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lab.Ir.Templating;

/// <summary>
/// Substitui <c>{{ expressão }}</c> nos valores de texto de uma árvore JSON.
/// Um valor que é só a expressão vira número; dentro de um texto, é interpolado no formato pt-BR.
/// Sufixos de formato: <c>| n0</c>, <c>| n1</c>, <c>| n2</c>, <c>| pct</c>.
/// </summary>
public static partial class TemplateRenderer
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    [GeneratedRegex(@"\{\{\s*(?<expr>[^{}|]+?)\s*(\|\s*(?<fmt>[a-z0-9]+)\s*)?\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    public static JsonNode? Render(JsonNode? node, IReadOnlyDictionary<string, double> variables) => node switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, Render(p.Value, variables)))),
        JsonArray array => new JsonArray(array.Select(item => Render(item, variables)).ToArray()),
        JsonValue value when value.TryGetValue<string>(out var text) => RenderText(text, variables),
        _ => node?.DeepClone(),
    };

    public static JsonNode RenderText(string text, IReadOnlyDictionary<string, double> variables)
    {
        var matches = Placeholder().Matches(text);
        if (matches.Count == 0) return JsonValue.Create(text);

        if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == text.Length && !matches[0].Groups["fmt"].Success)
            return JsonValue.Create(Normalize(Expression.Evaluate(matches[0].Groups["expr"].Value, variables)));

        var builder = new StringBuilder(text.Length + 16);
        var last = 0;
        foreach (Match match in matches)
        {
            builder.Append(text, last, match.Index - last);
            var value = Expression.Evaluate(match.Groups["expr"].Value, variables);
            builder.Append(Format(value, match.Groups["fmt"].Success ? match.Groups["fmt"].Value : null));
            last = match.Index + match.Length;
        }

        builder.Append(text, last, text.Length - last);
        return JsonValue.Create(builder.ToString());
    }

    public static string Format(double value, string? format) => format switch
    {
        "n0" => value.ToString("N0", PtBr),
        "n1" => value.ToString("N1", PtBr),
        "n2" => value.ToString("N2", PtBr),
        "pct" => (value * 100).ToString("0.##", PtBr) + "%",
        _ => Math.Abs(value - Math.Round(value)) < 1e-9 ? value.ToString("N0", PtBr) : value.ToString("N2", PtBr),
    };

    private static double Normalize(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9 ? Math.Round(value) : value;
}
