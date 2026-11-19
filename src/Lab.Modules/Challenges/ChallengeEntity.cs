namespace Lab.Modules.Challenges;

/// <summary>
/// Desafio instanciado (<c>chl_challenges</c>). <see cref="SpecJson"/> guarda tudo, inclusive cenários ocultos e a
/// referência; <see cref="PublicJson"/> é o que o frontend pode ver antes da submissão.
/// </summary>
public sealed class ChallengeEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public required string SeedId { get; set; }
    public int SeedVersion { get; set; }
    public string Title { get; set; } = "";
    public required string Provider { get; set; }
    public required string Difficulty { get; set; }
    public required string Mode { get; set; }
    public string VariationJson { get; set; } = "{}";
    public string? PersonalizationJson { get; set; }
    public string SpecJson { get; set; } = "{}";
    public string PublicJson { get; set; } = "{}";
    public long MasterSeed { get; set; }
    public double? ReferenceCostUsd { get; set; }

    /// <summary>A referência como cena do canvas + fluxos — só sai da API depois da submissão.</summary>
    public string? ReferenceSceneJson { get; set; }
    public string Status { get; set; } = ChallengeStatus.Ready;
    public DateTime CreatedAt { get; set; }
}

public static class ChallengeStatus
{
    public const string Assembling = "assembling";
    public const string Ready = "ready";
    public const string Rejected = "rejected";
}
