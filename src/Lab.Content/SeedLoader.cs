using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Seeds;

namespace Lab.Content;

/// <summary>Carrega as sementes de <c>content/challenges/**.yaml</c>.</summary>
public static class SeedLoader
{
    private sealed record VariableEntry
    {
        public Dictionary<string, double[]> Ranges { get; init; } = [];
        public double Default { get; init; }
        public double Step { get; init; } = 1;
        public string Description { get; init; } = "";
    }

    public static IReadOnlyList<Seed> LoadAll(string challengesDirectory)
    {
        if (!Directory.Exists(challengesDirectory)) return [];
        var seeds = new List<Seed>();
        foreach (var file in Directory.GetFiles(challengesDirectory, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            seeds.Add(Load(file, Path.GetRelativePath(challengesDirectory, file)));

        var duplicates = seeds.GroupBy(s => s.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ContentException($"Sementes com ID duplicado: {string.Join(", ", duplicates)}.");
        return seeds;
    }

    public static Seed Load(string path, string? displayName = null) =>
        Parse(File.ReadAllText(path), displayName ?? Path.GetFileName(path));

    public static Seed Parse(string yaml, string sourceName)
    {
        if (YamlJson.Parse(yaml, sourceName) is not JsonObject body)
            throw new ContentException($"{sourceName}: a semente precisa ser um objeto YAML.");

        try
        {
            var variables = new Dictionary<string, VariableSpec>(StringComparer.Ordinal);
            if (body["variables"] is JsonObject variableObject)
            {
                foreach (var (name, value) in variableObject)
                {
                    var entry = value.Deserialize<VariableEntry>(LabJson.Options) ?? new VariableEntry();
                    variables[name] = new VariableSpec
                    {
                        Default = entry.Default,
                        Step = entry.Step <= 0 ? 1 : entry.Step,
                        Description = entry.Description,
                        Ranges = entry.Ranges.ToDictionary(p => ParseDifficulty(p.Key, sourceName), p => p.Value),
                    };
                }
            }

            return new Seed
            {
                Id = Str(body, "id") ?? throw new ContentException($"{sourceName}: campo 'id' obrigatório."),
                Version = (int)(body["version"]?.GetValue<long>() ?? 1),
                Title = Str(body, "title") ?? throw new ContentException($"{sourceName}: campo 'title' obrigatório."),
                Summary = Str(body, "summary") ?? "",
                Track = Str(body, "track") ?? "",
                Domain = Str(body, "domain") ?? "",
                Tags = body["tags"]?.Deserialize<List<string>>(LabJson.Options) ?? [],
                Difficulties = (body["difficulties"]?.Deserialize<List<string>>(LabJson.Options) ?? [])
                    .Select(d => ParseDifficulty(d, sourceName)).ToList(),
                Variables = variables,
                Twists = body["twists"]?.Deserialize<List<TwistSpec>>(LabJson.Options) ?? [],
                Body = body,
                SourceFile = sourceName,
            };
        }
        catch (JsonException ex)
        {
            throw new ContentException($"{sourceName}: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new ContentException($"{sourceName}: {ex.Message}", ex);
        }
    }

    public static Difficulty ParseDifficulty(string value, string sourceName = "") => value.Trim().ToLowerInvariant() switch
    {
        "junior" or "júnior" => Difficulty.Junior,
        "pleno" => Difficulty.Pleno,
        "senior" or "sênior" => Difficulty.Senior,
        "staff" => Difficulty.Staff,
        _ => throw new ContentException($"{sourceName}: dificuldade desconhecida '{value}'."),
    };

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
}
