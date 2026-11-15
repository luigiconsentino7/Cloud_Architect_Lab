using System.Globalization;
using System.Text;
using Lab.Ir.Catalog;

namespace Lab.Ir.Scene;

/// <summary>Casa rótulos de formas desenhadas à mão ("Redis", "Kafka") com os aliases do catálogo.</summary>
public static class LabelMatcher
{
    public static string Normalize(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var lastWasSpace = true;
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    public static IReadOnlyList<(ComponentDefinition Component, double Score)> Match(
        string label, IEnumerable<ComponentDefinition> candidates, int max = 3)
    {
        var normalized = Normalize(label);
        if (normalized.Length == 0) return [];
        var padded = $" {normalized} ";

        var scored = new List<(ComponentDefinition, double)>();
        foreach (var component in candidates)
        {
            var best = 0.0;
            var names = component.Aliases.Append(component.DisplayName).Append(component.ShortName ?? "");
            foreach (var name in names)
            {
                var alias = Normalize(name);
                if (alias.Length < 2) continue;
                double score;
                if (alias == normalized) score = 1.0;
                else if (padded.Contains($" {alias} ", StringComparison.Ordinal)) score = 0.8 + Math.Min(alias.Length, 15) / 100.0;
                else if ($" {alias} ".Contains(padded, StringComparison.Ordinal) && normalized.Length >= 3) score = 0.6;
                else continue;
                best = Math.Max(best, score);
            }

            if (best > 0) scored.Add((component, best));
        }

        return scored
            .OrderByDescending(s => s.Item2)
            .ThenBy(s => s.Item1.IsDefault ? 0 : 1)
            .ThenBy(s => s.Item1.Id, StringComparer.Ordinal)
            .Take(max)
            .ToList();
    }
}
