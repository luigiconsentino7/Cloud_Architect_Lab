using System.Globalization;
using System.Text;
using Lab.Ir.Challenges;
using Lab.Ir.Seeds;

namespace Lab.Content;

public sealed record SeedMatch(Seed Seed, IReadOnlyList<string> MatchedTerms, double Score);

/// <summary>
/// Busca guiada na biblioteca (a mesma no app, no chat e nos evals): termos casados no título (3), nas tags (2) e no
/// resto do texto (1), sem acentos e ignorando palavras vazias, porque o chat manda frases inteiras.
/// </summary>
public static class SeedSearch
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "de", "da", "do", "das", "dos", "em", "no", "na", "nos", "nas", "um", "uma", "uns", "umas", "os", "as", "ao", "aos", "com", "sem",
        "por", "para", "pra", "pelo", "pela", "que", "se", "ou", "eu", "me", "meu", "minha", "sou", "estou", "trabalho", "quero", "queria",
        "treinar", "estudar", "praticar", "aprender", "sobre", "como", "mais", "muito", "isso", "esse", "essa", "tem", "ter", "ser", "num", "numa",
    };

    /// <summary>Sem termos (ou só palavras vazias), devolve todas as sementes do filtro, sem pontuação.</summary>
    public static IReadOnlyList<SeedMatch> Rank(IEnumerable<Seed> seeds, string? query, Difficulty? difficulty = null, string? track = null)
    {
        var terms = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2 && !StopWords.Contains(t)).Distinct().ToList();
        var results = new List<SeedMatch>();
        foreach (var seed in seeds)
        {
            if (difficulty is { } d && seed.Difficulties.Count > 0 && !seed.Difficulties.Contains(d)) continue;
            if (!string.IsNullOrWhiteSpace(track) && !string.Equals(seed.Track, track, StringComparison.OrdinalIgnoreCase)) continue;

            var title = Normalize(seed.Title);
            var tags = seed.Tags.Select(Normalize).ToList();
            var body = Normalize($"{seed.Summary} {seed.Domain} {seed.Track} {seed.Body["narrative"]}");
            var matched = new List<string>();
            var score = 0.0;
            foreach (var term in terms)
            {
                var hit = 0.0;
                if (title.Contains(term, StringComparison.Ordinal)) hit += 3;
                if (tags.Any(t => t.Contains(term, StringComparison.Ordinal))) hit += 2;
                if (body.Contains(term, StringComparison.Ordinal)) hit += 1;
                if (hit > 0) matched.Add(term);
                score += hit;
            }

            if (terms.Count > 0 && score == 0) continue;
            results.Add(new SeedMatch(seed, matched, score));
        }

        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Seed.Track, StringComparer.Ordinal).ThenBy(r => r.Seed.Title, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// As sementes que o personalizador recebe: as que casaram com o contexto primeiro, completadas pelas demais do
    /// nível até <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<Seed> Candidates(IEnumerable<Seed> seeds, string context, Difficulty difficulty, int max = 6)
    {
        var all = seeds.ToList();
        var matched = Rank(all, context, difficulty).Where(m => m.MatchedTerms.Count > 0).Select(m => m.Seed).Take(max).ToList();
        matched.AddRange(Rank(all, null, difficulty).Select(m => m.Seed).Where(s => !matched.Contains(s)).Take(max - matched.Count));
        return matched;
    }

    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return builder.ToString();
    }
}
