namespace Lab.Modules.Conversations;

/// <summary>
/// Conversa (<c>conv_conversations</c>): <see cref="ConversationKind.Context"/> é o chat da tela inicial (contexto →
/// desafio); <see cref="ConversationKind.Interview"/> é o entrevistador de um desafio.
/// </summary>
public sealed class ConversationEntity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public required string Kind { get; set; }
    public Guid? ChallengeId { get; set; }
    public string Title { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Mensagem (<c>conv_messages</c>), numerada dentro da conversa.</summary>
public sealed class ConversationMessageEntity
{
    public Guid Id { get; set; }
    public Guid ConversationId { get; set; }
    public int Number { get; set; }

    /// <summary><c>user</c> ou <c>assistant</c>.</summary>
    public required string Role { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Quem respondeu: <c>usuario</c>, <c>ia</c>, <c>curado</c> ou <c>biblioteca</c>.</summary>
    public string Source { get; set; } = MessageSource.User;

    public string? Model { get; set; }
    public string? PromptVersion { get; set; }

    /// <summary>Dados estruturados da resposta: sugestões da biblioteca, desafio criado, pergunta curada usada.</summary>
    public string? PayloadJson { get; set; }

    public DateTime CreatedAt { get; set; }
}

public static class ConversationKind
{
    public const string Context = "contexto";
    public const string Interview = "entrevista";
}

public static class MessageSource
{
    public const string User = "usuario";
    public const string Ai = "ia";
    public const string Curated = "curado";
    public const string Library = "biblioteca";
}
