namespace Lab.Ir.Content;

public sealed record LinkRef(string Title, string Url);

/// <summary>Texto curado de uma regra (criado em tempo de autoria com o Claude Code).</summary>
public sealed record RuleText
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Why { get; init; } = "";
    public string How { get; init; } = "";
    public string Tradeoffs { get; init; } = "";
    public IReadOnlyList<string> FollowUps { get; init; } = [];
    public IReadOnlyList<string> StudyTopics { get; init; } = [];
    public IReadOnlyList<LinkRef> Links { get; init; } = [];
}

public sealed class RuleTextLibrary(IEnumerable<RuleText> texts)
{
    private readonly Dictionary<string, RuleText> _texts = texts.ToDictionary(t => t.Id, StringComparer.Ordinal);

    public static RuleTextLibrary Empty { get; } = new([]);

    public IReadOnlyCollection<RuleText> All => _texts.Values;

    public RuleText? Find(string id) => _texts.GetValueOrDefault(id);
}
