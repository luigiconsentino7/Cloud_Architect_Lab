namespace Lab.Modules.Design;

/// <summary>Tentativa (<c>dsg_attempts</c>): o rascunho do autosave fica aqui, com versão para concorrência otimista.</summary>
public sealed class AttemptEntity
{
    public Guid Id { get; set; }
    public Guid ChallengeId { get; set; }
    public int Number { get; set; }
    public required string Provider { get; set; }
    public required string Mode { get; set; }
    public string Status { get; set; } = AttemptStatus.Draft;
    public string DraftSceneJson { get; set; } = """{"type":"excalidraw","version":2,"elements":[]}""";
    public int DraftVersion { get; set; }
    public string FlowsJson { get; set; } = "[]";
    public string HintsUsedJson { get; set; } = "[]";
    public DateTime StartedAt { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? DeadlineAt { get; set; }
    public Guid? SubmissionEvaluationId { get; set; }
    public string? SubmitIdempotencyKey { get; set; }
}

public static class AttemptStatus
{
    public const string Draft = "draft";
    public const string Submitted = "submitted";
    public const string Evaluating = "evaluating";
    public const string Evaluated = "evaluated";
    public const string Failed = "failed";
}

/// <summary>Snapshot em ações explícitas (<c>dsg_snapshots</c>): validar, simular, submeter.</summary>
public sealed class SnapshotEntity
{
    public Guid Id { get; set; }
    public Guid AttemptId { get; set; }
    public required string Kind { get; set; }
    public string SceneJson { get; set; } = "{}";
    public string FlowsJson { get; set; } = "[]";
    public string IrJson { get; set; } = "{}";
    public string IrHash { get; set; } = "";
    public string DiagnosticsJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
}
