namespace Lab.Modules.Gamification;

/// <summary>Ledger de XP só de inserção: único em (perfil, motivo, origem), então creditar duas vezes é impossível.</summary>
public sealed class XpEntryEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public int Amount { get; set; }
    public required string Reason { get; set; }
    public required string SourceType { get; set; }
    public required string SourceId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Saldo, nível e streak materializados; recalculáveis a partir do ledger.</summary>
public sealed class UserProgressEntity
{
    public Guid ProfileId { get; set; }
    public int TotalXp { get; set; }
    public int Level { get; set; } = 1;
    public int StreakDays { get; set; }
    public int StreakFreezes { get; set; }
    public DateOnly? LastActivityDate { get; set; }
}

public sealed class CompetencyRatingEntity
{
    public Guid ProfileId { get; set; }
    public required string Competency { get; set; }
    public double Rating { get; set; }
    public int Samples { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class UserAchievementEntity
{
    public Guid ProfileId { get; set; }
    public required string Code { get; set; }
    public DateTime UnlockedAt { get; set; }
    public Guid SourceEvaluationId { get; set; }
}

/// <summary>Melhor resultado por desafio: base do "só a melhoria rende XP" e de conquistas como Persistente.</summary>
public sealed class ChallengeStatsEntity
{
    public Guid ProfileId { get; set; }
    public Guid ChallengeId { get; set; }
    public required string SeedId { get; set; }
    public required string Provider { get; set; }
    public int BestScore { get; set; }
    public string BestRank { get; set; } = "D";
    public int BestAttemptXp { get; set; }
    public int FirstScore { get; set; }
    public int Evaluations { get; set; }
}
