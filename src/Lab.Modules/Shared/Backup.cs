using Lab.Modules.Challenges;
using Lab.Modules.Conversations;
using Lab.Modules.Design;
using Lab.Modules.Evaluation;
using Lab.Modules.Gamification;
using Lab.Modules.Profile;
using Lab.Modules.Shared.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Shared;

/// <summary>Os dados do usuário, tabela por tabela (jobs e outbox ficam de fora: são transitórios).</summary>
public sealed record BackupTables
{
    public List<ProfileEntity> Profiles { get; init; } = [];
    public List<ChallengeEntity> Challenges { get; init; } = [];
    public List<AttemptEntity> Attempts { get; init; } = [];
    public List<SnapshotEntity> Snapshots { get; init; } = [];
    public List<EvaluationEntity> Evaluations { get; init; } = [];
    public List<FindingEntity> Findings { get; init; } = [];
    public List<ScenarioResultEntity> ScenarioResults { get; init; } = [];
    public List<XpEntryEntity> XpEntries { get; init; } = [];
    public List<UserProgressEntity> UserProgress { get; init; } = [];
    public List<CompetencyRatingEntity> CompetencyRatings { get; init; } = [];
    public List<UserAchievementEntity> UserAchievements { get; init; } = [];
    public List<ChallengeStatsEntity> ChallengeStats { get; init; } = [];
    public List<ConversationEntity> Conversations { get; init; } = [];
    public List<ConversationMessageEntity> ConversationMessages { get; init; } = [];

    public int Rows => Profiles.Count + Challenges.Count + Attempts.Count + Snapshots.Count + Evaluations.Count + Findings.Count
        + ScenarioResults.Count + XpEntries.Count + UserProgress.Count + CompetencyRatings.Count + UserAchievements.Count
        + ChallengeStats.Count + Conversations.Count + ConversationMessages.Count;
}

/// <param name="Schema">A última migration aplicada no banco que gerou o backup.</param>
public sealed record BackupFile(string Format, int Version, DateTime ExportedAt, string Schema, string ContentHash, BackupTables Tables)
{
    public const string FormatName = "cloud-architect-lab-backup";
    public const int CurrentVersion = 1;
}

public sealed record BackupImportResult(int Rows, int Challenges, int Evaluations, string SafetyCopy);

public sealed class BackupException(string message) : Exception(message);

/// <summary>
/// Backup completo em JSON. A importação substitui todos os dados numa transação, depois de copiar o banco atual para
/// <c>backups/</c>; avaliações que estavam na fila voltam como falha (os jobs não vão no backup).
/// </summary>
public sealed class BackupService(LabDbContext db, LabContent content, LabOptions options, TimeProvider clock)
{
    public async Task<BackupFile> ExportAsync(CancellationToken cancellationToken)
    {
        var schema = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).LastOrDefault() ?? "";
        var tables = new BackupTables
        {
            Profiles = await db.Profiles.AsNoTracking().ToListAsync(cancellationToken),
            Challenges = await db.Challenges.AsNoTracking().ToListAsync(cancellationToken),
            Attempts = await db.Attempts.AsNoTracking().ToListAsync(cancellationToken),
            Snapshots = await db.Snapshots.AsNoTracking().ToListAsync(cancellationToken),
            Evaluations = await db.Evaluations.AsNoTracking().ToListAsync(cancellationToken),
            Findings = await db.Findings.AsNoTracking().ToListAsync(cancellationToken),
            ScenarioResults = await db.ScenarioResults.AsNoTracking().ToListAsync(cancellationToken),
            XpEntries = await db.XpEntries.AsNoTracking().ToListAsync(cancellationToken),
            UserProgress = await db.UserProgress.AsNoTracking().ToListAsync(cancellationToken),
            CompetencyRatings = await db.CompetencyRatings.AsNoTracking().ToListAsync(cancellationToken),
            UserAchievements = await db.UserAchievements.AsNoTracking().ToListAsync(cancellationToken),
            ChallengeStats = await db.ChallengeStats.AsNoTracking().ToListAsync(cancellationToken),
            Conversations = await db.Conversations.AsNoTracking().ToListAsync(cancellationToken),
            ConversationMessages = await db.ConversationMessages.AsNoTracking().ToListAsync(cancellationToken),
        };
        return new BackupFile(BackupFile.FormatName, BackupFile.CurrentVersion, clock.GetUtcNow().UtcDateTime, schema, content.ContentHash, tables);
    }

    public async Task<BackupImportResult> ImportAsync(BackupFile backup, CancellationToken cancellationToken)
    {
        if (backup.Format != BackupFile.FormatName) throw new BackupException("O arquivo não é um backup do Cloud Architect Lab.");
        if (backup.Version > BackupFile.CurrentVersion)
            throw new BackupException($"Backup na versão {backup.Version}, mais nova que a deste app ({BackupFile.CurrentVersion}). Atualize o app antes de importar.");
        if (backup.Tables.Profiles.Count == 0) throw new BackupException("O backup não tem perfil: não parece um backup completo.");

        var safetyCopy = SafetyCopy();
        var tables = backup.Tables;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ConversationMessages.ExecuteDeleteAsync(cancellationToken);
        await db.Conversations.ExecuteDeleteAsync(cancellationToken);
        await db.ChallengeStats.ExecuteDeleteAsync(cancellationToken);
        await db.UserAchievements.ExecuteDeleteAsync(cancellationToken);
        await db.CompetencyRatings.ExecuteDeleteAsync(cancellationToken);
        await db.UserProgress.ExecuteDeleteAsync(cancellationToken);
        await db.XpEntries.ExecuteDeleteAsync(cancellationToken);
        await db.ScenarioResults.ExecuteDeleteAsync(cancellationToken);
        await db.Findings.ExecuteDeleteAsync(cancellationToken);
        await db.Evaluations.ExecuteDeleteAsync(cancellationToken);
        await db.Snapshots.ExecuteDeleteAsync(cancellationToken);
        await db.Attempts.ExecuteDeleteAsync(cancellationToken);
        await db.Challenges.ExecuteDeleteAsync(cancellationToken);
        await db.Profiles.ExecuteDeleteAsync(cancellationToken);
        await db.Jobs.ExecuteDeleteAsync(cancellationToken);
        await db.Outbox.ExecuteDeleteAsync(cancellationToken);

        // Sem os jobs, o que estava na fila não roda mais: volta como falha técnica, e a tentativa pode ser refeita.
        foreach (var evaluation in tables.Evaluations.Where(e => e.Status is EvaluationStatus.Queued or EvaluationStatus.Running))
        {
            evaluation.Status = EvaluationStatus.Failed;
            evaluation.Error = "Interrompida: a avaliação estava na fila quando o backup foi feito.";
        }

        foreach (var attempt in tables.Attempts.Where(a => a.Status == "evaluating")) attempt.Status = "failed";

        db.Profiles.AddRange(tables.Profiles);
        db.Challenges.AddRange(tables.Challenges);
        db.Attempts.AddRange(tables.Attempts);
        db.Snapshots.AddRange(tables.Snapshots);
        db.Evaluations.AddRange(tables.Evaluations);
        db.Findings.AddRange(tables.Findings);
        db.ScenarioResults.AddRange(tables.ScenarioResults);
        db.XpEntries.AddRange(tables.XpEntries);
        db.UserProgress.AddRange(tables.UserProgress);
        db.CompetencyRatings.AddRange(tables.CompetencyRatings);
        db.UserAchievements.AddRange(tables.UserAchievements);
        db.ChallengeStats.AddRange(tables.ChallengeStats);
        db.Conversations.AddRange(tables.Conversations);
        db.ConversationMessages.AddRange(tables.ConversationMessages);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return new BackupImportResult(tables.Rows, tables.Challenges.Count, tables.Evaluations.Count, safetyCopy);
    }

    /// <summary>Cópia do banco atual antes de substituir tudo (backup online do SQLite, com o app rodando).</summary>
    private string SafetyCopy()
    {
        var backups = Path.Combine(options.DataDirectory!, "backups");
        Directory.CreateDirectory(backups);
        var target = Path.Combine(backups, $"lab-{clock.GetUtcNow().UtcDateTime:yyyyMMdd-HHmmss}-antes-da-importacao.db");
        using var source = new SqliteConnection($"Data Source={LabModules.DatabasePath(options)};Pooling=False");
        using var destination = new SqliteConnection($"Data Source={target};Pooling=False");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        return Path.GetFileName(target);
    }
}
