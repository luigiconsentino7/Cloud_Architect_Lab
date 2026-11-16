namespace Lab.Ai;

/// <summary>
/// Portas por papel (spec 06). Cada porta tem o caminho determinístico (modo <c>none</c>, sempre disponível) e o local
/// com o Ollama. A IA nunca mexe na nota: ela personaliza o enunciado, conversa e narra o que o motor já decidiu.
/// </summary>
public enum AiMode
{
    None,
    Ollama,
}

public sealed record AiStatus(
    AiMode Mode,
    string ConfiguredMode,
    bool OllamaReachable,
    string? Model,
    string Endpoint,
    IReadOnlyList<string> InstalledModels,
    string Detail);

/// <param name="Role"><c>user</c> ou <c>assistant</c>.</param>
public sealed record ChatTurn(string Role, string Text);

// ---- contexto → desafio ----

public sealed record VariableRange(double Min, double Max, double Step, string Description);

public sealed record TwistCandidate(string Id, string Title, string Description);

public sealed record SeedCandidate(
    string SeedId,
    string Title,
    string Summary,
    string Track,
    IReadOnlyList<string> Tags,
    IReadOnlyList<TwistCandidate> Twists,
    IReadOnlyDictionary<string, VariableRange> Variables);

public sealed record PersonalizationRequest(string Context, string Difficulty, IReadOnlyList<SeedCandidate> Candidates);

public sealed record ExtraClarification(string Question, string Answer);

/// <summary>O delta pequeno que a IA devolve; o desafio completo é montado e validado de forma determinística.</summary>
public sealed record ChallengeDelta
{
    public required string SeedId { get; init; }
    public string? Narrative { get; init; }
    public IReadOnlyDictionary<string, double> Variables { get; init; } = new Dictionary<string, double>();
    public IReadOnlyList<string> Twists { get; init; } = [];
    public IReadOnlyList<ExtraClarification> ClarificationsExtra { get; init; } = [];
    public string Reason { get; init; } = "";
}

/// <param name="Adjustments">O que a validação corrigiu ou descartou da resposta do modelo.</param>
public sealed record PersonalizationResult(ChallengeDelta Delta, string Model, string PromptVersion, IReadOnlyList<string> Adjustments);

public interface IChallengePersonalizer
{
    /// <summary>Nulo sem IA ou quando a resposta não serve (o app cai para a busca guiada na biblioteca).</summary>
    Task<PersonalizationResult?> PersonalizeAsync(PersonalizationRequest request, CancellationToken cancellationToken);
}

// ---- entrevistador ----

public sealed record CuratedAnswer(string Question, IReadOnlyList<string> Keywords, string Answer);

/// <summary>Só a parte pública do desafio: o entrevistador não conhece cenários ocultos nem a referência.</summary>
public sealed record InterviewContext(string Title, string Statement, IReadOnlyList<CuratedAnswer> Clarifications);

public sealed record InterviewRequest(InterviewContext Context, IReadOnlyList<ChatTurn> History, string Question);

/// <param name="Source"><see cref="AnswerSource.Ai"/> ou <see cref="AnswerSource.Curated"/>.</param>
/// <param name="MatchedQuestion">A pergunta curada usada na resposta, quando houver.</param>
public sealed record InterviewAnswer(string Text, string Source, string? Model, string? PromptVersion, string? MatchedQuestion);

public static class AnswerSource
{
    public const string Ai = "ia";
    public const string Curated = "curado";
}

public interface IInterviewer
{
    /// <param name="onDelta">Recebe os pedaços da resposta enquanto o modelo escreve (só no modo com IA).</param>
    Task<InterviewAnswer> AnswerAsync(InterviewRequest request, Func<string, ValueTask>? onDelta, CancellationToken cancellationToken);
}

// ---- narrador da revisão ----

/// <param name="ReviewJson">A revisão determinística (resumo, melhorias com evidências, pontos fortes) em JSON.</param>
public sealed record NarrationRequest(string Title, int FinalScore, string Rank, string ReviewJson);

public sealed record ReviewNarrative(IReadOnlyList<string> Paragraphs, string Model, string PromptVersion);

public interface IReviewNarrator
{
    /// <summary>
    /// Nulo sem IA ou quando a narrativa cita número que a revisão não tem (ela nunca chega à tela). Falha do modelo
    /// lança exceção: a narrativa roda num job, que tenta de novo.
    /// </summary>
    Task<ReviewNarrative?> NarrateAsync(NarrationRequest request, CancellationToken cancellationToken);
}
