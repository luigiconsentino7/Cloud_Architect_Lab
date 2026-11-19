using Lab.Ir.Challenges;
using Lab.Modules.Evaluation.Contracts;
using Lab.Modules.Gamification.Contracts;
using Lab.Modules.Profile.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Outbox;
using Lab.Modules.Shared.Persistence;
using Lab.Modules.Shared.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Gamification;

/// <summary>Níveis e títulos da spec 07.</summary>
public static class Levels
{
    private static readonly (int Xp, string Title)[] Table =
    [
        (0, "Estagiário(a) de Nuvem"), (150, "Estagiário(a) de Nuvem"),
        (400, "Júnior I"), (800, "Júnior II"), (1300, "Júnior III"),
        (2000, "Pleno I"), (2900, "Pleno II"), (4000, "Pleno III"),
        (5400, "Sênior I"), (7100, "Sênior II"), (9100, "Sênior III"),
        (11500, "Staff I"), (14300, "Staff II"), (17500, "Staff III"),
        (21200, "Principal I"), (25400, "Principal II"), (30000, "Principal III"),
        (35000, "Arquiteto(a) Distinto(a)"),
    ];

    /// <summary>XP acumulado para chegar ao nível (1-based). Do 18 em diante, +6.000 por nível.</summary>
    public static int Threshold(int level) => level <= Table.Length ? Table[Math.Max(1, level) - 1].Xp : Table[^1].Xp + ((level - Table.Length) * 6000);

    public static int LevelFor(int totalXp)
    {
        var level = 1;
        while (Threshold(level + 1) <= totalXp) level++;
        return level;
    }

    public static string TitleFor(int level) => level <= Table.Length ? Table[level - 1].Title : Table[^1].Title;
}

public sealed record AchievementDefinition(string Code, string Title, string Criterion);

/// <summary>As 10 conquistas do MVP (spec 07).</summary>
public static class Achievements
{
    public static IReadOnlyList<AchievementDefinition> All { get; } =
    [
        new("primeiro-deploy", "Primeiro Deploy", "Concluir a primeira avaliação de uma submissão."),
        new("zero-duplicidade", "Zero Duplicidade", "0 efeitos duplicados no cenário de integridade de um desafio com dinheiro."),
        new("outbox-na-veia", "Outbox na Veia", "Usar outbox ou CDC (sem dual-write) em 3 desafios diferentes."),
        new("sobrevivente-dia-de-pagamento", "Sobrevivente do Dia de Pagamento", "Passar em todos os SLOs de um pico de 5x ou mais."),
        new("black-friday", "Black Friday", "Passar num spike de 10x ou mais."),
        new("az-que-az", "AZ? Que AZ?", "Nenhum SLO violado na queda de uma zona."),
        new("mao-de-vaca-consciente", "Mão de Vaca Consciente", "Custo de até 70% do orçamento com rank A ou S."),
        new("poliglota-de-nuvem", "Poliglota de Nuvem", "O mesmo desafio com rank B ou melhor em AWS, Azure e GCP."),
        new("sem-rodinhas", "Sem Rodinhas", "Rank A ou S sem dicas num desafio Sênior ou Staff."),
        new("persistente", "Persistente", "Melhorar a nota em 30 pontos ou mais entre tentativas do mesmo desafio."),
    ];

    public static AchievementDefinition? Find(string code) => All.FirstOrDefault(a => a.Code == code);
}

public sealed class GamificationQueries(LabDbContext db) : IGamificationQueries
{
    public async Task<int> BestAttemptXpAsync(Guid profileId, Guid challengeId, CancellationToken cancellationToken = default) =>
        await db.ChallengeStats.Where(s => s.ProfileId == profileId && s.ChallengeId == challengeId)
            .Select(s => s.BestAttemptXp).FirstOrDefaultAsync(cancellationToken);

    public async Task<ProgressSummary> ProgressAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var progress = await db.UserProgress.AsNoTracking().FirstOrDefaultAsync(p => p.ProfileId == profileId, cancellationToken)
            ?? new UserProgressEntity { ProfileId = profileId };
        return new ProgressSummary(progress.TotalXp, progress.Level, Levels.TitleFor(progress.Level), Levels.Threshold(progress.Level),
            Levels.Threshold(progress.Level + 1), progress.StreakDays);
    }
}

/// <summary>
/// Consome <see cref="EvaluationCompleted"/>: credita só a melhoria sobre a melhor tentativa do desafio (ledger com
/// chave única), atualiza nível, streak, competências e conquistas. Reprocessar o mesmo evento não credita de novo.
/// </summary>
public sealed class EvaluationCompletedHandler(LabDbContext db, ILabNotifier notifier, TimeProvider clock) : IOutboxHandler
{
    private static readonly TimeZoneInfo SaoPaulo = FindSaoPaulo();

    public string EventType => EvaluationCompleted.Type;

    public async Task HandleAsync(Guid messageId, string payloadJson, CancellationToken cancellationToken)
    {
        var e = Json.Deserialize<EvaluationCompleted>(payloadJson);
        if (e.Kind != "submission") return; // simulação de rascunho não rende XP
        var source = e.EvaluationId.ToString();
        if (await db.XpEntries.AnyAsync(x => x.ProfileId == e.ProfileId && x.Reason == "evaluation" && x.SourceId == source, cancellationToken))
            return;

        var now = clock.GetUtcNow().UtcDateTime;
        var progress = await db.UserProgress.FirstOrDefaultAsync(p => p.ProfileId == e.ProfileId, cancellationToken);
        if (progress is null)
        {
            progress = new UserProgressEntity { ProfileId = e.ProfileId };
            db.UserProgress.Add(progress);
        }

        var stats = await db.ChallengeStats.FirstOrDefaultAsync(s => s.ProfileId == e.ProfileId && s.ChallengeId == e.ChallengeId, cancellationToken);
        var firstOfChallenge = stats is null;
        stats ??= db.ChallengeStats.Add(new ChallengeStatsEntity
        {
            ProfileId = e.ProfileId,
            ChallengeId = e.ChallengeId,
            SeedId = e.SeedId,
            Provider = e.Provider,
            FirstScore = e.FinalScore,
        }).Entity;

        var credited = Math.Max(0, e.AttemptXp - stats.BestAttemptXp);
        db.XpEntries.Add(new XpEntryEntity
        {
            Id = Guid.CreateVersion7(),
            ProfileId = e.ProfileId,
            Amount = credited,
            Reason = "evaluation",
            SourceType = "evaluation",
            SourceId = source,
            CreatedAt = now,
        });

        var previousLevel = progress.Level;
        progress.TotalXp += credited;
        progress.Level = Levels.LevelFor(progress.TotalXp);
        UpdateStreak(progress, now);

        var previousBestScore = firstOfChallenge ? (int?)null : stats.BestScore;
        stats.BestAttemptXp = Math.Max(stats.BestAttemptXp, e.AttemptXp);
        if (firstOfChallenge || e.FinalScore > stats.BestScore)
        {
            stats.BestScore = e.FinalScore;
            stats.BestRank = e.Rank;
        }

        stats.Evaluations++;
        await UpdateCompetenciesAsync(e, now, cancellationToken);
        var unlocked = await UnlockAsync(e, previousBestScore, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        await notifier.XpAwarded(new XpAwardedEvent(credited, progress.TotalXp, credited > 0 ? $"Avaliação: nota {e.FinalScore} ({e.Rank})" : "Sem melhoria sobre a melhor tentativa"));
        if (progress.Level > previousLevel) await notifier.LevelUp(new LevelUpEvent(progress.Level, Levels.TitleFor(progress.Level)));
        foreach (var code in unlocked) await notifier.AchievementUnlocked(new AchievementUnlockedEvent(code, Achievements.Find(code)!.Title));
    }

    private static void UpdateStreak(UserProgressEntity progress, DateTime nowUtc)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, SaoPaulo));
        if (progress.LastActivityDate == today) return;
        if (progress.LastActivityDate is { } last && last.AddDays(1) == today) progress.StreakDays++;
        else if (progress.LastActivityDate is { } before && today.DayNumber - before.DayNumber == 2 && progress.StreakFreezes > 0)
        {
            progress.StreakFreezes--; // o congelamento cobre o dia sem estudo
            progress.StreakDays++;
        }
        else progress.StreakDays = 1;

        if (progress.StreakDays % 7 == 0) progress.StreakFreezes = Math.Min(2, progress.StreakFreezes + 1);
        progress.LastActivityDate = today;
    }

    /// <summary>rating += k × (desempenho − rating), k = max(0,15; 0,5/√n), desempenho = nota da dimensão × fator da dificuldade.</summary>
    private async Task UpdateCompetenciesAsync(EvaluationCompleted e, DateTime now, CancellationToken cancellationToken)
    {
        var factor = e.Difficulty switch { Difficulty.Junior => 0.7, Difficulty.Pleno => 0.85, Difficulty.Senior => 1.0, _ => 1.15 };
        var touched = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["escalabilidade"] = e.DimensionScores.GetValueOrDefault("performance"),
            ["resiliencia"] = e.DimensionScores.GetValueOrDefault("resilience"),
            ["consistencia"] = e.DimensionScores.GetValueOrDefault("consistency"),
            ["seguranca"] = e.DimensionScores.GetValueOrDefault("security"),
            ["custo"] = e.DimensionScores.GetValueOrDefault("cost"),
            ["observabilidade"] = e.DimensionScores.GetValueOrDefault("operability"),
        };
        if (e.Competencies.Contains("mensageria"))
            touched["mensageria"] = (e.DimensionScores.GetValueOrDefault("consistency") + e.DimensionScores.GetValueOrDefault("resilience")) / 2;
        if (e.Competencies.Contains("baixa-latencia")) touched["baixa-latencia"] = e.DimensionScores.GetValueOrDefault("performance");

        foreach (var (competency, score) in touched)
        {
            var rating = await db.CompetencyRatings.FirstOrDefaultAsync(r => r.ProfileId == e.ProfileId && r.Competency == competency, cancellationToken);
            if (rating is null)
            {
                rating = new CompetencyRatingEntity { ProfileId = e.ProfileId, Competency = competency };
                db.CompetencyRatings.Add(rating);
            }

            rating.Samples++;
            var performance = Math.Min(100, score * factor);
            var k = Math.Max(0.15, 0.5 / Math.Sqrt(rating.Samples));
            rating.Rating = Math.Round(rating.Samples == 1 ? performance : rating.Rating + (k * (performance - rating.Rating)), 2);
            rating.UpdatedAt = now;
        }
    }

    private async Task<List<string>> UnlockAsync(EvaluationCompleted e, int? previousBestScore, DateTime now, CancellationToken cancellationToken)
    {
        var owned = await db.UserAchievements.Where(a => a.ProfileId == e.ProfileId).Select(a => a.Code).ToListAsync(cancellationToken);
        var unlocked = new List<string>();

        async Task<bool> Outbox3()
        {
            if (!(e.UsesOutboxOrCdc && !e.DualWriteFinding)) return false;
            var key = $"outbox:{e.ChallengeId}";
            var known = await db.XpEntries.AnyAsync(x => x.ProfileId == e.ProfileId && x.Reason == key, cancellationToken);
            if (!known)
            {
                // Marcador sem XP: conta os desafios em que o outbox/CDC foi usado corretamente (gravado junto com o resto).
                db.XpEntries.Add(new XpEntryEntity { Id = Guid.CreateVersion7(), ProfileId = e.ProfileId, Amount = 0, Reason = key, SourceType = "pattern", SourceId = e.ChallengeId.ToString(), CreatedAt = now });
            }

            var stored = await db.XpEntries.CountAsync(x => x.ProfileId == e.ProfileId && x.Reason.StartsWith("outbox:"), cancellationToken);
            return stored + (known ? 0 : 1) >= 3;
        }

        async Task<bool> Polyglot()
        {
            if (e.Rank is not ("S" or "A" or "B")) return false;
            var providers = await db.ChallengeStats
                .Where(s => s.ProfileId == e.ProfileId && s.SeedId == e.SeedId && (s.BestRank == "S" || s.BestRank == "A" || s.BestRank == "B"))
                .Select(s => s.Provider).Distinct().ToListAsync(cancellationToken);
            if (!providers.Contains(e.Provider)) providers.Add(e.Provider);
            return new[] { "aws", "azure", "gcp" }.All(providers.Contains);
        }

        var candidates = new List<(string Code, Func<Task<bool>> Test)>
        {
            ("primeiro-deploy", () => Task.FromResult(true)),
            ("zero-duplicidade", () => Task.FromResult(e.HasMonetaryUseCase && e.MonetaryIntegrityClean)),
            ("outbox-na-veia", Outbox3),
            ("sobrevivente-dia-de-pagamento", () => Task.FromResult(e.MaxPassedLoadMultiplier >= 5)),
            ("black-friday", () => Task.FromResult(e.MaxPassedSpikeMultiplier >= 10)),
            ("az-que-az", () => Task.FromResult(e.ZoneOutagePassed)),
            ("mao-de-vaca-consciente", () => Task.FromResult(e.CostBudgetRatio is > 0 and <= 0.7 && e.Rank is "S" or "A")),
            ("poliglota-de-nuvem", Polyglot),
            ("sem-rodinhas", () => Task.FromResult(e.Rank is "S" or "A" && e.HintLevelsUsed.Count == 0 && e.Difficulty is Difficulty.Senior or Difficulty.Staff)),
            ("persistente", () => Task.FromResult(previousBestScore is { } best && e.FinalScore - best >= 30)),
        };

        foreach (var (code, test) in candidates)
        {
            if (owned.Contains(code) || !await test()) continue;
            db.UserAchievements.Add(new UserAchievementEntity { ProfileId = e.ProfileId, Code = code, UnlockedAt = now, SourceEvaluationId = e.EvaluationId });
            unlocked.Add(code);
        }

        return unlocked;
    }

    private static TimeZoneInfo FindSaoPaulo()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("BRT", TimeSpan.FromHours(-3), "Brasília", "Brasília");
        }
    }
}

public sealed record AchievementView(string Code, string Title, string Criterion, bool Unlocked, DateTime? UnlockedAt);

public sealed record CompetencyView(string Competency, double Rating, int Samples);

public sealed record XpEntryView(int Amount, string Reason, string SourceType, string SourceId, DateTime CreatedAt);

public static class GamificationEndpoints
{
    public static void MapGamification(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me").WithTags("Gamificação");

        group.MapGet("/progress", async (IProfileQueries profiles, IGamificationQueries queries, LabDbContext db, CancellationToken ct) =>
        {
            var profileId = await profiles.CurrentProfileIdAsync(ct);
            var entries = await db.XpEntries.AsNoTracking().Where(x => x.ProfileId == profileId && x.Reason == "evaluation")
                .OrderByDescending(x => x.CreatedAt).Take(30).ToListAsync(ct);
            return Results.Ok(new
            {
                summary = await queries.ProgressAsync(profileId, ct),
                recent = entries.Select(x => new XpEntryView(x.Amount, x.Reason, x.SourceType, x.SourceId, x.CreatedAt)),
            });
        });

        group.MapGet("/achievements", async (IProfileQueries profiles, LabDbContext db, CancellationToken ct) =>
        {
            var profileId = await profiles.CurrentProfileIdAsync(ct);
            var owned = await db.UserAchievements.AsNoTracking().Where(a => a.ProfileId == profileId).ToListAsync(ct);
            return Results.Ok(Achievements.All.Select(a =>
            {
                var unlocked = owned.FirstOrDefault(o => o.Code == a.Code);
                return new AchievementView(a.Code, a.Title, a.Criterion, unlocked is not null, unlocked?.UnlockedAt);
            }));
        });

        group.MapGet("/competencies", async (IProfileQueries profiles, LabDbContext db, CancellationToken ct) =>
        {
            var profileId = await profiles.CurrentProfileIdAsync(ct);
            var ratings = await db.CompetencyRatings.AsNoTracking().Where(r => r.ProfileId == profileId).OrderBy(r => r.Competency).ToListAsync(ct);
            return Results.Ok(ratings.Select(r => new CompetencyView(r.Competency, r.Rating, r.Samples)));
        });
    }
}
