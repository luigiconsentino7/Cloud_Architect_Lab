using Lab.Ir.Challenges;
using Lab.Modules.Shared.Persistence;

// Contratos entre módulos: um módulo só conhece os outros por estas interfaces e DTOs (verificado nos testes de arquitetura).

namespace Lab.Modules.Profile.Contracts
{
    public interface IProfileQueries
    {
        /// <summary>O perfil local (criado na inicialização).</summary>
        Task<Guid> CurrentProfileIdAsync(CancellationToken cancellationToken = default);
    }
}

namespace Lab.Modules.Challenges.Contracts
{
    public sealed record ChallengeInfo(Guid Id, Guid ProfileId, string SeedId, int SeedVersion, string Title, string Provider, string Difficulty, string Mode);

    public interface IChallengeQueries
    {
        Task<ChallengeInfo?> FindAsync(Guid challengeId, CancellationToken cancellationToken = default);

        /// <summary>Desafio completo, com cenários ocultos e referência — só para o motor, nunca para o frontend.</summary>
        Task<ChallengeSpec?> SpecAsync(Guid challengeId, CancellationToken cancellationToken = default);

        Task<double?> ReferenceCostAsync(Guid challengeId, CancellationToken cancellationToken = default);

        /// <summary>JSON <c>{ scene, flows }</c> da referência desenhada.</summary>
        Task<string?> ReferenceSceneAsync(Guid challengeId, CancellationToken cancellationToken = default);

        Task SaveReferenceCostAsync(Guid challengeId, double cost, CancellationToken cancellationToken = default);
    }
}

namespace Lab.Modules.Design.Contracts
{
    public sealed record AttemptInfo(
        Guid Id,
        Guid ChallengeId,
        int Number,
        string Provider,
        string Mode,
        string Status,
        IReadOnlyList<int> HintLevelsUsed,
        Guid? SubmissionEvaluationId = null);

    /// <summary>O que foi avaliado: a cena e os fluxos no momento do snapshot.</summary>
    public sealed record SnapshotInfo(Guid Id, Guid AttemptId, string Kind, string SceneJson, string FlowsJson, string IrJson, string IrHash);

    public interface IAttemptQueries
    {
        Task<AttemptInfo?> FindAsync(Guid attemptId, CancellationToken cancellationToken = default);

        Task<SnapshotInfo?> SnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default);

        /// <summary>Muda o estado da tentativa quando a avaliação de submissão começa, termina ou falha.</summary>
        Task SetStatusAsync(Guid attemptId, string status, CancellationToken cancellationToken = default);
    }
}

namespace Lab.Modules.Evaluation.Contracts
{
    /// <summary>Agenda uma avaliação no mesmo contexto (e transação) do snapshot que ela avalia.</summary>
    public interface IEvaluationScheduler
    {
        Guid Schedule(LabDbContext db, Guid attemptId, Guid challengeId, Guid snapshotId, string kind);
    }

    /// <summary>Evento publicado no outbox quando uma avaliação termina (consumido pela gamificação).</summary>
    public sealed record EvaluationCompleted
    {
        public const string Type = "evaluation.completed";

        public required Guid EvaluationId { get; init; }
        public required Guid AttemptId { get; init; }
        public required Guid ChallengeId { get; init; }
        public required Guid ProfileId { get; init; }
        public required string Kind { get; init; }
        public required string SeedId { get; init; }
        public required string Provider { get; init; }
        public required Difficulty Difficulty { get; init; }
        public required GameMode Mode { get; init; }
        public required int AttemptNumber { get; init; }
        public required int FinalScore { get; init; }
        public required string Rank { get; init; }
        public required int AttemptXp { get; init; }
        public IReadOnlyList<int> HintLevelsUsed { get; init; } = [];
        public IReadOnlyDictionary<string, double> DimensionScores { get; init; } = new Dictionary<string, double>();
        public IReadOnlyList<string> Competencies { get; init; } = [];

        // Fatos para as conquistas.
        public bool HasMonetaryUseCase { get; init; }
        public bool MonetaryIntegrityClean { get; init; }
        public bool IntegrityScenarioRan { get; init; }
        public bool UsesOutboxOrCdc { get; init; }
        public bool DualWriteFinding { get; init; }
        public double MaxPassedLoadMultiplier { get; init; }
        public double MaxPassedSpikeMultiplier { get; init; }
        public bool ZoneOutagePassed { get; init; }
        public double CostBudgetRatio { get; init; }
    }
}

namespace Lab.Modules.Gamification.Contracts
{
    public sealed record ProgressSummary(int TotalXp, int Level, string Title, int LevelStartXp, int NextLevelXp, int StreakDays);

    public interface IGamificationQueries
    {
        Task<int> BestAttemptXpAsync(Guid profileId, Guid challengeId, CancellationToken cancellationToken = default);

        Task<ProgressSummary> ProgressAsync(Guid profileId, CancellationToken cancellationToken = default);
    }
}
