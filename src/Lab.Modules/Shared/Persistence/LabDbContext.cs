using Lab.Modules.Challenges;
using Lab.Modules.Design;
using Lab.Modules.Evaluation;
using Lab.Modules.Gamification;
using Lab.Modules.Profile;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Shared.Persistence;

/// <summary>
/// Um arquivo SQLite, tabelas com prefixo por módulo. Um contexto só, para o outbox entrar na mesma transação da
/// mudança; as fronteiras entre módulos são verificadas pelos testes de arquitetura, não pelo banco.
/// </summary>
public sealed class LabDbContext(DbContextOptions<LabDbContext> options) : DbContext(options)
{
    public DbSet<ProfileEntity> Profiles => Set<ProfileEntity>();
    public DbSet<ChallengeEntity> Challenges => Set<ChallengeEntity>();
    public DbSet<AttemptEntity> Attempts => Set<AttemptEntity>();
    public DbSet<SnapshotEntity> Snapshots => Set<SnapshotEntity>();
    public DbSet<EvaluationEntity> Evaluations => Set<EvaluationEntity>();
    public DbSet<FindingEntity> Findings => Set<FindingEntity>();
    public DbSet<ScenarioResultEntity> ScenarioResults => Set<ScenarioResultEntity>();
    public DbSet<XpEntryEntity> XpEntries => Set<XpEntryEntity>();
    public DbSet<UserProgressEntity> UserProgress => Set<UserProgressEntity>();
    public DbSet<CompetencyRatingEntity> CompetencyRatings => Set<CompetencyRatingEntity>();
    public DbSet<UserAchievementEntity> UserAchievements => Set<UserAchievementEntity>();
    public DbSet<ChallengeStatsEntity> ChallengeStats => Set<ChallengeStatsEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<OutboxMessageEntity> Outbox => Set<OutboxMessageEntity>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ProfileEntity>(e =>
        {
            e.ToTable("profile_profiles");
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(80);
        });

        model.Entity<ChallengeEntity>(e =>
        {
            e.ToTable("chl_challenges");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ProfileId, x.CreatedAt });
        });

        model.Entity<AttemptEntity>(e =>
        {
            e.ToTable("dsg_attempts");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ChallengeId, x.Number }).IsUnique();
            e.Property(x => x.DraftVersion).IsConcurrencyToken();
        });

        model.Entity<SnapshotEntity>(e =>
        {
            e.ToTable("dsg_snapshots");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AttemptId, x.CreatedAt });
        });

        model.Entity<EvaluationEntity>(e =>
        {
            e.ToTable("eva_evaluations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.AttemptId, x.CreatedAt });
            e.HasIndex(x => x.ChallengeId);
        });

        model.Entity<FindingEntity>(e =>
        {
            e.ToTable("eva_findings");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.EvaluationId);
        });

        model.Entity<ScenarioResultEntity>(e =>
        {
            e.ToTable("eva_scenario_results");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.EvaluationId, x.ScenarioId }).IsUnique();
        });

        model.Entity<XpEntryEntity>(e =>
        {
            e.ToTable("gam_xp_entries");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.ProfileId, x.Reason, x.SourceId }).IsUnique();
        });

        model.Entity<UserProgressEntity>(e =>
        {
            e.ToTable("gam_user_progress");
            e.HasKey(x => x.ProfileId);
        });

        model.Entity<CompetencyRatingEntity>(e =>
        {
            e.ToTable("gam_competency_ratings");
            e.HasKey(x => new { x.ProfileId, x.Competency });
        });

        model.Entity<UserAchievementEntity>(e =>
        {
            e.ToTable("gam_user_achievements");
            e.HasKey(x => new { x.ProfileId, x.Code });
        });

        model.Entity<ChallengeStatsEntity>(e =>
        {
            e.ToTable("gam_challenge_stats");
            e.HasKey(x => new { x.ProfileId, x.ChallengeId });
        });

        model.Entity<JobEntity>(e =>
        {
            e.ToTable("sys_jobs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.Status, x.RunAfter });
        });

        model.Entity<OutboxMessageEntity>(e =>
        {
            e.ToTable("sys_outbox");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ProcessedAt);
        });
    }
}
