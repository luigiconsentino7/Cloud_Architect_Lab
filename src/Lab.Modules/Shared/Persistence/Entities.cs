namespace Lab.Modules.Shared.Persistence;

/// <summary>Job da fila em background (<c>sys_jobs</c>): entregue pelo menos uma vez, retomado após reinício.</summary>
public sealed class JobEntity
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public string Status { get; set; } = JobStatus.Queued;
    public int Attempts { get; set; }
    public DateTime RunAfter { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public static class JobStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>Evento de domínio gravado na mesma transação da mudança (<c>sys_outbox</c>).</summary>
public sealed class OutboxMessageEntity
{
    public Guid Id { get; set; }
    public required string Type { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTime OccurredAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}
