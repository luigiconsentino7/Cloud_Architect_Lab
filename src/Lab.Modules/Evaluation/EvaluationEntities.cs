namespace Lab.Modules.Evaluation;

/// <summary>Avaliação (<c>eva_evaluations</c>) — de rascunho (simulação no Treino) ou de submissão.</summary>
public sealed class EvaluationEntity
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public Guid ChallengeId { get; set; }
    public Guid SnapshotId { get; set; }
    public required string Kind { get; set; }
    public string Status { get; set; } = EvaluationStatus.Queued;
    public string Stage { get; set; } = "queued";
    public string? Detail { get; set; }
    public double Percent { get; set; }
    public string? ManifestJson { get; set; }
    public string? ResultJson { get; set; }
    public int? FinalScore { get; set; }
    public string? Rank { get; set; }
    public string? Error { get; set; }

    /// <summary>Narrativa da revisão escrita pela IA local (<see cref="NarrativeState"/>), quando ligada.</summary>
    public string? NarrativeJson { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class EvaluationStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

/// <summary>Estado da narrativa: <c>pending</c>, <c>ready</c>, <c>rejected</c> (citou o que a revisão não tem), <c>unavailable</c> ou <c>failed</c>.</summary>
public sealed record NarrativeState(string Status, IReadOnlyList<string> Paragraphs, string? Model, string? PromptVersion, string? Detail, DateTime UpdatedAt)
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Rejected = "rejected";
    public const string Unavailable = "unavailable";
    public const string Failed = "failed";
}

public static class EvaluationKind
{
    public const string Draft = "draft";
    public const string Submission = "submission";
}

public sealed class FindingEntity
{
    public Guid Id { get; set; }
    public Guid EvaluationId { get; set; }
    public required string RuleId { get; set; }
    public required string Severity { get; set; }
    public required string Dimension { get; set; }
    public string Message { get; set; } = "";
    public string ElementRefsJson { get; set; } = "[]";
    public bool Confirmed { get; set; }
}

/// <summary>Resumo de um cenário e as séries por segundo comprimidas (gzip) para gráficos e replay.</summary>
public sealed class ScenarioResultEntity
{
    public Guid Id { get; set; }
    public Guid EvaluationId { get; set; }
    public required string ScenarioId { get; set; }
    public string SummaryJson { get; set; } = "{}";
    public byte[]? Series { get; set; }
}
