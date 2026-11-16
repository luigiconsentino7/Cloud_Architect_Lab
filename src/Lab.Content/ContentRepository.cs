using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Content;
using Lab.Ir.Seeds;

namespace Lab.Content;

/// <summary>Todo o conteúdo versionado do repositório, carregado na inicialização.</summary>
public sealed record ContentBundle(
    string Root,
    ComponentCatalog Catalog,
    IReadOnlyList<Seed> Seeds,
    RuleTextLibrary RuleTexts,
    string ContentHash)
{
    public Seed? FindSeed(string id) => Seeds.FirstOrDefault(s => s.Id == id);
}

public static class ContentRepository
{
    public static ContentBundle Load(string contentRoot)
    {
        var root = Path.GetFullPath(contentRoot);
        if (!Directory.Exists(Path.Combine(root, "catalog")))
            throw new ContentException($"Pasta de conteúdo inválida: '{root}' (falta catalog/).");

        var catalog = CatalogLoader.Load(Path.Combine(root, "catalog"));
        var seeds = SeedLoader.LoadAll(Path.Combine(root, "challenges"));
        var rules = LoadRuleTexts(Path.Combine(root, "rules"));
        return new ContentBundle(root, catalog, seeds, rules, HashDirectory(root));
    }

    /// <summary>Procura a pasta <c>content/</c> subindo a partir de um diretório (útil em testes e na CLI).</summary>
    public static string? Discover(string? startDirectory = null)
    {
        var directory = new DirectoryInfo(startDirectory ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "content");
            if (File.Exists(Path.Combine(candidate, "catalog", "archetypes.yaml"))) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    public static ContentBundle LoadDiscovered(string? startDirectory = null) =>
        Load(Discover(startDirectory) ?? throw new ContentException("Pasta content/ não encontrada."));

    private sealed record RuleFile(IReadOnlyList<RuleText> Rules);

    private static RuleTextLibrary LoadRuleTexts(string directory)
    {
        if (!Directory.Exists(directory)) return RuleTextLibrary.Empty;
        var texts = new List<RuleText>();
        foreach (var file in Directory.GetFiles(directory, "*.yaml").Order(StringComparer.Ordinal))
        {
            var node = YamlJson.Parse(File.ReadAllText(file), Path.GetFileName(file));
            try
            {
                texts.AddRange(node.Deserialize<RuleFile>(LabJson.Options)?.Rules ?? []);
            }
            catch (JsonException ex)
            {
                throw new ContentException($"{Path.GetFileName(file)}: {ex.Message}", ex);
            }
        }

        return new RuleTextLibrary(texts);
    }

    /// <summary>Só o que muda a avaliação entra no hash (prompts e datasets de evals não mudam a nota).</summary>
    private static readonly string[] HashedFolders = ["catalog/", "challenges/", "rules/"];

    private static string HashDirectory(string root)
    {
        var files = Directory.GetFiles(root, "*.yaml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(f => HashedFolders.Any(folder => f.StartsWith(folder, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal);
        var manifest = new JsonObject();
        foreach (var file in files)
            manifest[file] = Canonical.HashText(File.ReadAllText(Path.Combine(root, file)));
        return Canonical.HashText(manifest.ToJsonString());
    }
}
