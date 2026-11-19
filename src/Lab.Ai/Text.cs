using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Lab.Ai;

/// <summary>Utilitários de texto das saídas da IA: normalização, extração de JSON e checagem de números citados.</summary>
public static partial class AiText
{
    /// <summary>Minúsculas, sem acentos e sem pontuação (para casar palavras-chave).</summary>
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>Texto de uma linha só, sem caracteres de controle, cortado no fim de frase mais próximo do limite.</summary>
    public static string Clean(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (c is '\n' or '\r' or '\t') builder.Append(' ');
            else if (!char.IsControl(c)) builder.Append(c);
        }

        var clean = Spaces().Replace(builder.ToString(), " ").Trim();
        if (clean.Length <= maxLength) return clean;
        var cut = clean[..maxLength];
        var sentence = cut.LastIndexOfAny(['.', '!', '?']);
        return sentence > maxLength / 2 ? cut[..(sentence + 1)] : cut.TrimEnd() + "…";
    }

    /// <summary>O objeto JSON da resposta (tolera cercas de markdown que alguns modelos pequenos insistem em pôr).</summary>
    public static JsonObject? ParseObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonNode.Parse(text[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Números citados num texto em pt-BR ("1.277", "99,95", "3.000,5") ou em JSON ("1277.5"). Ponto seguido de
    /// exatamente três dígitos é separador de milhar; vírgula é decimal.
    /// </summary>
    public static IReadOnlyList<double> Numbers(string text)
    {
        var numbers = new List<double>();
        foreach (Match match in NumberToken().Matches(text))
        {
            if (TryParseNumber(match.Value, out var value)) numbers.Add(value);
        }

        return numbers;
    }

    public static bool TryParseNumber(string token, out double value)
    {
        var t = token;
        if (t.Contains(',', StringComparison.Ordinal))
        {
            t = t.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        }
        else if (t.Contains('.', StringComparison.Ordinal))
        {
            var groups = t.Split('.');
            if (groups.Skip(1).All(g => g.Length == 3)) t = t.Replace(".", "", StringComparison.Ordinal);
            else if (groups.Length > 2) t = string.Concat(groups[..^1]) + "." + groups[^1];
        }

        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Os números do texto que não aparecem nos permitidos (inteiros de 0 a 10 sempre passam: "3 pontos", "2 zonas").</summary>
    public static IReadOnlyList<double> UnknownNumbers(string text, IEnumerable<double> allowed)
    {
        var known = allowed.ToList();
        return Numbers(text)
            .Where(n => !(n >= 0 && n <= 10 && Math.Abs(n - Math.Round(n)) < 1e-9))
            .Where(n => !known.Any(k => Math.Abs(k - n) <= Math.Max(1e-6, Math.Abs(k) * 1e-6)))
            .Distinct()
            .ToList();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"(?<![\p{L}\d])\d+(?:[.,]\d+)*")]
    private static partial Regex NumberToken();
}
