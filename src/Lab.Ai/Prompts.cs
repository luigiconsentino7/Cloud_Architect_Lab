using System.Globalization;
using System.Text.RegularExpressions;

namespace Lab.Ai;

/// <summary>Prompt versionado (<c>content/prompts/&lt;papel&gt;/v&lt;n&gt;.md</c>); a versão usada vai para o manifesto.</summary>
public sealed record PromptTemplate(string Role, int Version, string Text)
{
    public string Id => $"{Role}/v{Version}";
}

public static class PromptRoles
{
    public const string Personalizer = "personalizador";
    public const string Interviewer = "entrevistador";
    public const string Narrator = "narrador";
}

/// <summary>Carrega a versão mais nova de cada papel. O front-matter (modelos-alvo, changelog) não vai para o modelo.</summary>
public sealed partial class PromptLibrary(IReadOnlyDictionary<string, PromptTemplate> latest)
{
    public static readonly PromptLibrary Empty = new(new Dictionary<string, PromptTemplate>());

    public IReadOnlyCollection<PromptTemplate> All => latest.Values.ToList();

    public static PromptLibrary Load(string promptsDirectory)
    {
        var latest = new Dictionary<string, PromptTemplate>(StringComparer.Ordinal);
        if (!Directory.Exists(promptsDirectory)) return new PromptLibrary(latest);
        foreach (var file in Directory.EnumerateFiles(promptsDirectory, "v*.md", SearchOption.AllDirectories))
        {
            var match = VersionFile().Match(Path.GetFileName(file));
            if (!match.Success) continue;
            var role = Path.GetFileName(Path.GetDirectoryName(file))!;
            var version = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (latest.TryGetValue(role, out var current) && current.Version >= version) continue;
            latest[role] = new PromptTemplate(role, version, StripFrontMatter(File.ReadAllText(file)).Trim());
        }

        return new PromptLibrary(latest);
    }

    public PromptTemplate Get(string role) =>
        latest.TryGetValue(role, out var prompt) ? prompt : throw new InvalidOperationException($"Prompt '{role}' não encontrado em content/prompts.");

    public bool Has(string role) => latest.ContainsKey(role);

    public static string StripFrontMatter(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal)) return normalized;
        var end = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? normalized : normalized[(end + 5)..];
    }

    [GeneratedRegex(@"^v(\d+)\.md$")]
    private static partial Regex VersionFile();
}
