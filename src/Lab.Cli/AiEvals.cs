using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Ai;
using Lab.Content;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Pipeline;
using Lab.Pipeline.Seeds;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Lab.Cli;

/// <param name="Flags">Critérios parciais do caso (ex.: delta válido, monta o desafio, aderente ao contexto).</param>
/// <param name="Error">O Ollama falhou (conexão, tempo esgotado): não é resposta errada, e o caso não entra na conta.</param>
/// <param name="Sample">O texto que a IA escreveu (narrativa, resposta, comentário), para um humano julgar a escrita.</param>
public sealed record EvalCaseResult(string Role, string Case, bool Passed, double Seconds, string Detail, IReadOnlyDictionary<string, bool> Flags, bool Error = false, string? Sample = null);

/// <param name="EstimatedTokens">Sem a contagem do Ollama, os tokens são estimados por caracteres / 4.</param>
public sealed record SpeedProbe(double FirstTokenSeconds, double TokensPerSecond, bool EstimatedTokens);

public sealed record ModelEvalReport(string Model, IReadOnlyList<EvalCaseResult> Cases, SpeedProbe? Speed, string? Error, string? SpeedError = null)
{
    public IReadOnlyList<EvalCaseResult> For(string role) => Cases.Where(c => c.Role == role).ToList();

    public int Errors => Cases.Count(c => c.Error);

    /// <summary>Só os casos que o modelo respondeu de verdade entram na conta (os com erro do Ollama ficam de fora).</summary>
    public double PassRate(string role)
    {
        var answered = For(role).Where(c => !c.Error).ToList();
        return answered.Count == 0 ? 0 : (double)answered.Count(c => c.Passed) / answered.Count;
    }

    /// <summary>Acerto médio dos papéis avaliados (cada papel pesa igual), de 0 a 1.</summary>
    public double Quality => Roles().Select(PassRate).DefaultIfEmpty(0).Average();

    /// <summary>Tempo médio por chamada: média das medianas de cada papel, só com os casos respondidos.</summary>
    public double MedianSeconds => Roles()
        .Select(role => For(role).Where(c => !c.Error).Select(c => c.Seconds).Order().ToList())
        .Where(times => times.Count > 0)
        .Select(times => times[times.Count / 2])
        .DefaultIfEmpty(0).Average();

    private IEnumerable<string> Roles() => Cases.Select(c => c.Role).Distinct(StringComparer.Ordinal);

    /// <summary>Sem erro do Ollama e com a velocidade medida: só então o resultado serve para decidir.</summary>
    public bool Complete => Error is null && Errors == 0 && Speed is not null;

    /// <summary>Critérios do spike S4: personalizador com ≥ 80% de primeira, 1º token ≤ 3 s e ≥ 15 tokens/s.</summary>
    public bool MeetsS4 => Complete && Speed is { FirstTokenSeconds: <= 3, TokensPerSecond: >= 15 }
        && (For(PromptRoles.Personalizer).Count == 0 || PassRate(PromptRoles.Personalizer) >= 0.8);
}

/// <summary>
/// Guarda o que os papéis registram no log, para o eval separar "o modelo errou" de "o Ollama falhou": aviso ou erro é
/// falha de conexão; informação e depuração explicam por que uma resposta foi descartada.
/// </summary>
internal sealed class CapturedLog<T> : ILogger<T>
{
    private readonly List<string> _failures = [];
    private readonly List<string> _notes = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (logLevel >= LogLevel.Warning) _failures.Add(exception is null ? message : $"{exception.GetType().Name}: {exception.Message}");
        else _notes.Add(message);
    }

    /// <summary>A falha registrada desde a última chamada (e esquece), ou nulo.</summary>
    public string? TakeFailure()
    {
        var failure = _failures.LastOrDefault();
        _failures.Clear();
        return failure;
    }

    /// <summary>As últimas explicações registradas desde a última chamada (e esquece), ou nulo.</summary>
    public string? TakeNotes()
    {
        var notes = _notes.TakeLast(2).ToList();
        _notes.Clear();
        return notes.Count == 0 ? null : string.Join(" · ", notes);
    }
}

/// <summary>
/// Evals dos papéis de IA (spec 06, spike S4): roda os datasets de <c>content/evals</c> contra um modelo local e mede
/// qualidade (deltas válidos e aderentes, respostas certas do entrevistador, narrativas aceitas, mapeamentos certos)
/// e velocidade (primeiro token e tokens por segundo).
/// </summary>
public sealed class AiEvalRunner(ContentBundle content, PromptLibrary prompts, TextWriter log)
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static readonly string[] AllRoles = [PromptRoles.Personalizer, PromptRoles.Interviewer, PromptRoles.Narrator, PromptRoles.Mapper];

    private static readonly string[] Admissions =
    [
        "nao esta definido", "nao define", "nao sei", "nao tenho", "pode assumir", "nao especifica", "nao foi especificado",
        "nao ha informacao", "nao informa", "nao menciona", "nao consta", "a seu criterio", "decisao sua", "nao e informado",
    ];

    private readonly Dictionary<string, (ChallengeSpec Spec, EvaluationResult Result, string Review)> _reviews = new(StringComparer.Ordinal);

    /// <summary>Orçamento de eventos das avaliações que geram as revisões do narrador (menor nos testes).</summary>
    public long NarrationEventBudget { get; init; } = 3_000_000;

    public async Task<ModelEvalReport> RunAsync(AiRuntime runtime, IReadOnlyList<string> roles, int? limit, CancellationToken cancellationToken)
    {
        var model = runtime.Options.Ollama.Model;

        // Fixa o status: uma checagem lenta no meio da rodada derrubaria a IA e estragaria as medidas.
        var status = await runtime.PinAsync(cancellationToken);
        if (status.Mode != AiMode.Ollama) return new ModelEvalReport(model, [], null, status.Detail);

        await WarmUpAsync(runtime, cancellationToken);
        var results = new List<EvalCaseResult>();
        if (roles.Contains(PromptRoles.Personalizer))
        {
            log.WriteLine("  Personalizador…");
            results.AddRange(await PersonalizerAsync(runtime, limit, cancellationToken));
        }

        if (roles.Contains(PromptRoles.Interviewer))
        {
            log.WriteLine("  Entrevistador…");
            results.AddRange(await InterviewerAsync(runtime, limit, cancellationToken));
        }

        if (roles.Contains(PromptRoles.Narrator))
        {
            log.WriteLine("  Narrador…");
            results.AddRange(await NarratorAsync(runtime, limit, cancellationToken));
        }

        if (roles.Contains(PromptRoles.Mapper))
        {
            log.WriteLine("  Mapeador…");
            results.AddRange(await MapperAsync(runtime, limit, cancellationToken));
        }

        log.WriteLine("  Velocidade…");
        var (speed, speedError) = await SpeedAsync(runtime, cancellationToken);
        return new ModelEvalReport(model, results, speed, null, speedError);
    }

    /// <summary>Carrega o modelo na memória antes de medir: o primeiro caso não pode carregar a conta do carregamento (10 a 30 s).</summary>
    private async Task WarmUpAsync(AiRuntime runtime, CancellationToken cancellationToken)
    {
        var client = await runtime.ClientAsync(cancellationToken);
        if (client is null) return;
        log.WriteLine("  Carregando o modelo na memória…");
        try
        {
            using var deadline = runtime.Deadline(cancellationToken);
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Responda apenas: ok")], runtime.ChatOptions(0f, 1), deadline.Token);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // o primeiro caso mostra o erro de verdade
        }
    }

    /// <summary>Uma linha por caso, para quem espera os 10 minutos de uma rodada saber que está andando.</summary>
    private EvalCaseResult Done(EvalCaseResult result)
    {
        var mark = result.Error ? "ERRO  " : result.Passed ? "ok    " : "falhou";
        log.WriteLine($"    {mark} {result.Seconds.ToString("0.0", PtBr),6} s  {AiText.Clean(result.Case, 70)}");
        return result;
    }

    private static EvalCaseResult ErrorCase(string role, string? name, double seconds, string failure) =>
        new(role, name ?? "", false, Math.Round(seconds, 2), $"erro ao falar com o Ollama: {AiText.Clean(failure, 200)}", new Dictionary<string, bool>(), Error: true);

    private async Task<List<EvalCaseResult>> PersonalizerAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var logger = new CapturedLog<ChallengePersonalizer>();
        var personalizer = new ChallengePersonalizer(runtime, prompts, logger);
        var results = new List<EvalCaseResult>();
        foreach (var c in Cases("personalizador", limit))
        {
            var context = Str(c["context"]);
            var difficulty = SeedLoader.ParseDifficulty(Str(c["difficulty"]) ?? "senior");
            var tracks = Strings(c["tracks"]);
            var candidates = SeedSearch.Candidates(content.Seeds, context ?? "", difficulty).Select(s => SeedCandidate.From(s, difficulty)).ToList();
            var watch = Stopwatch.StartNew();
            var result = await personalizer.PersonalizeAsync(new PersonalizationRequest(context ?? "", difficulty.ToString().ToLowerInvariant(), candidates), cancellationToken);
            var seconds = watch.Elapsed.TotalSeconds;
            if (logger.TakeFailure() is { } failure)
            {
                results.Add(Done(ErrorCase(PromptRoles.Personalizer, context, seconds, failure)));
                continue;
            }

            if (result is null)
            {
                var said = logger.TakeNotes();
                results.Add(Done(Case(PromptRoles.Personalizer, context, false, seconds, $"o modelo não devolveu um delta válido{(said is null ? "" : $": {said}")}",
                    new() { ["delta-valido"] = false, ["monta"] = false, ["aderente"] = false })));
                continue;
            }

            logger.TakeNotes();

            var seed = content.FindSeed(result.Delta.SeedId)!;
            string? error = null;
            try
            {
                SeedInstantiator.Instantiate(new InstantiationRequest
                {
                    Seed = seed,
                    Provider = CloudProvider.Aws,
                    Difficulty = difficulty,
                    Variables = result.Delta.Variables,
                    Twists = result.Delta.Twists,
                    VariationSeed = 1,
                }, content.Catalog);
            }
            catch (SeedException ex)
            {
                error = ex.Message;
            }

            var adherent = tracks.Contains(seed.Track);
            var adjustments = result.Adjustments.Count == 0 ? "" : $"; ajustes da validação: {string.Join(" | ", result.Adjustments.Take(3))}";
            var detail = $"{seed.Id}{(error is null ? "" : $"; não monta: {error}")}{adjustments}; narrativa {(result.Delta.Narrative is null ? "descartada" : "ok")}";
            var sample = result.Delta.Narrative is null ? null : $"{result.Delta.Narrative}\n\nMotivo da escolha: {result.Delta.Reason}";
            results.Add(Done(Case(PromptRoles.Personalizer, context, error is null && adherent, seconds, detail,
                new() { ["delta-valido"] = true, ["monta"] = error is null, ["aderente"] = adherent, ["sem-ajuste"] = result.Adjustments.Count == 0 }) with
            { Sample = sample }));
        }

        return results;
    }

    private async Task<List<EvalCaseResult>> InterviewerAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var logger = new CapturedLog<Interviewer>();
        var interviewer = new Interviewer(runtime, prompts, logger);
        var results = new List<EvalCaseResult>();
        foreach (var c in Cases("entrevistador", limit))
        {
            var seedId = Str(c["seed"]) ?? "";
            var question = Str(c["question"]) ?? "";
            var expect = Str(c["expect"]) ?? "unknown";
            var spec = Instantiate(seedId, Str(c["difficulty"]));
            var context = new InterviewContext(spec.Title, ChallengeStatement.Write(spec), spec.Clarifications.Select(q => new CuratedAnswer(q.Question, q.Keywords, q.Answer)).ToList());
            var watch = Stopwatch.StartNew();
            var answer = await interviewer.AnswerAsync(new InterviewRequest(context, [], question), null, cancellationToken);
            var seconds = watch.Elapsed.TotalSeconds;
            if (logger.TakeFailure() is { } failure)
            {
                results.Add(Done(ErrorCase(PromptRoles.Interviewer, $"{seedId}: {question}", seconds, failure)));
                continue;
            }

            if (answer.Source != AnswerSource.Ai)
            {
                results.Add(Done(Case(PromptRoles.Interviewer, $"{seedId}: {question}", false, seconds, "o modelo respondeu vazio (caiu para a resposta curada)", new())));
                continue;
            }

            var normalized = AiText.Normalize(answer.Text);
            var (passed, detail) = expect switch
            {
                "number" => spec.Variables.TryGetValue(Str(c["variable"]) ?? "", out var value)
                    ? (AiText.Numbers(answer.Text).Any(n => Math.Abs(n - value) < 0.5), $"esperava {value.ToString("#,0", CultureInfo.GetCultureInfo("pt-BR"))}")
                    : (false, "variável do caso não existe na semente"),
                "no-spoiler" => Spoilers(spec).FirstOrDefault(s => normalized.Contains(AiText.Normalize(s), StringComparison.Ordinal)) is { } leaked
                    ? (false, $"revelou \"{leaked}\"")
                    : Strings(c["forbid"]).FirstOrDefault(f => normalized.Contains(AiText.Normalize(f), StringComparison.Ordinal)) is { } denied
                        ? (false, $"disse \"{denied}\", mas os cenários ocultos existem")
                        : (true, "não revelou referência nem cenários ocultos"),
                _ => (Admissions.Any(a => normalized.Contains(a, StringComparison.Ordinal)), "esperava admitir que o enunciado não define"),
            };
            results.Add(Done(Case(PromptRoles.Interviewer, $"{seedId}: {question}", passed, seconds, $"{detail} · \"{AiText.Clean(answer.Text, 140)}\"",
                new() { [expect] = passed }) with
            { Sample = answer.Text }));
        }

        return results;
    }

    private async Task<List<EvalCaseResult>> NarratorAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var logger = new CapturedLog<ReviewNarrator>();
        var narrator = new ReviewNarrator(runtime, prompts, logger);
        var results = new List<EvalCaseResult>();
        foreach (var c in Cases("narrador", limit))
        {
            var seedId = Str(c["seed"]) ?? "";
            var (spec, result, review) = Review(seedId, Str(c["difficulty"]));
            var watch = Stopwatch.StartNew();
            ReviewNarrative? narrative;
            try
            {
                narrative = await narrator.NarrateAsync(new NarrationRequest(spec.Title, result.Score.FinalScore, result.Score.Rank, review), cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.TakeNotes();
                results.Add(Done(ErrorCase(PromptRoles.Narrator, seedId, watch.Elapsed.TotalSeconds, ex.Message)));
                continue;
            }

            var seconds = watch.Elapsed.TotalSeconds;
            var why = logger.TakeNotes();
            logger.TakeFailure();
            var top = result.Review.Improvements.FirstOrDefault()?.Title ?? "";
            var text = narrative is null ? "" : AiText.Normalize(string.Join(' ', narrative.Paragraphs));
            var mentionsTop = top.Length > 0 && AiText.Normalize(top).Split(' ').Where(w => w.Length >= 5).Any(w => text.Contains(w, StringComparison.Ordinal));
            var detail = narrative is null
                ? $"narrativa descartada depois de duas tentativas{(why is null ? "" : $": {why}")}"
                : $"cita o ponto de maior impacto: {(mentionsTop ? "sim" : "não")} ({top}){(why is null ? "" : $"; só passou na 2ª tentativa: a 1ª foi rejeitada ({why})")}";
            results.Add(Done(Case(PromptRoles.Narrator, $"{seedId} (nota {result.Score.FinalScore})", narrative is not null, seconds, detail,
                new() { ["aceita"] = narrative is not null, ["cita-o-principal"] = mentionsTop }) with
            { Sample = narrative is null ? null : string.Join("\n\n", narrative.Paragraphs) }));
        }

        return results;
    }

    private async Task<List<EvalCaseResult>> MapperAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var logger = new CapturedLog<LabelMapper>();
        var mapper = new LabelMapper(runtime, prompts, logger);
        var dataset = Dataset("mapeador");
        var provider = CloudProviders.Parse(Str(dataset["provider"]) ?? "aws");
        var palette = content.Catalog.ForProvider(provider)
            .Where(c => !Archetypes.IsClient(c.Archetype) && !Archetypes.IsExternal(c.Archetype))
            .Select(c => new MappingCandidate(c.Id, c.DisplayName, c.Archetype))
            .ToList();
        var results = new List<EvalCaseResult>();
        foreach (var c in Cases("mapeador", limit))
        {
            var label = Str(c["label"]) ?? "";
            var expected = Strings(c["expect"]);
            var watch = Stopwatch.StartNew();
            var suggestion = await mapper.SuggestAsync(new LabelMappingRequest(label, null, palette), cancellationToken);
            var seconds = watch.Elapsed.TotalSeconds;
            if (logger.TakeFailure() is { } failure)
            {
                results.Add(Done(ErrorCase(PromptRoles.Mapper, label, seconds, failure)));
                continue;
            }

            var answer = suggestion?.CatalogId ?? LabelMapper.None;
            results.Add(Done(Case(PromptRoles.Mapper, label, expected.Contains(answer), seconds,
                $"respondeu {answer}; aceitos: {string.Join(", ", expected)}", new() { ["certo"] = expected.Contains(answer) })));
        }

        return results;
    }

    /// <summary>Primeiro token e tokens por segundo num pedido curto, em streaming (critérios de velocidade do S4).</summary>
    private static async Task<(SpeedProbe? Probe, string? Error)> SpeedAsync(AiRuntime runtime, CancellationToken cancellationToken)
    {
        var client = await runtime.ClientAsync(cancellationToken);
        if (client is null) return (null, "o Ollama não ficou disponível");
        var watch = Stopwatch.StartNew();
        double? first = null;
        var text = new StringBuilder();
        long? tokens = null;
        try
        {
            using var deadline = runtime.Deadline(cancellationToken);
            List<ChatMessage> messages = [new(ChatRole.User, "Explique em até três frases, em português, o que é idempotência num pagamento Pix.")];
            await foreach (var update in client.GetStreamingResponseAsync(messages, runtime.ChatOptions(temperature: 0.2f, maxOutputTokens: 160), deadline.Token))
            {
                if (first is null && !string.IsNullOrEmpty(update.Text)) first = watch.Elapsed.TotalSeconds;
                text.Append(update.Text);
                foreach (var usage in update.Contents.OfType<UsageContent>()) tokens = usage.Details.OutputTokenCount ?? tokens;
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"{ex.GetType().Name}: {AiText.Clean(ex.Message, 160)}");
        }

        var total = watch.Elapsed.TotalSeconds;
        var generation = Math.Max(0.001, total - (first ?? 0));
        var outputTokens = tokens ?? Math.Max(1, text.Length / 4);
        return (new SpeedProbe(Math.Round(first ?? total, 2), Math.Round(outputTokens / generation, 1), tokens is null), null);
    }

    /// <summary>A revisão de uma referência degradada (uma instância por serviço, numa zona só): avaliada uma vez por semente.</summary>
    private (ChallengeSpec Spec, EvaluationResult Result, string Review) Review(string seedId, string? difficulty)
    {
        var key = $"{seedId}|{difficulty}";
        if (_reviews.TryGetValue(key, out var cached)) return cached;
        var spec = Instantiate(seedId, difficulty);
        var degraded = spec.Reference! with
        {
            Nodes = spec.Reference!.Nodes.Select(n => n.Archetype == Archetypes.Service ? n with { Config = SingleInstance(n.Config) } : n).ToList(),
        };
        log.WriteLine($"  avaliando a referência degradada de {seedId} para o narrador…");
        var result = EvaluationPipeline.Evaluate(new EvaluationRequest
        {
            Challenge = spec,
            Catalog = content.Catalog,
            RuleTexts = content.RuleTexts,
            Graph = degraded,
            EventBudget = NarrationEventBudget,
        });
        var review = ReviewDigest.FromResult(JsonSerializer.SerializeToNode(result, LabJson.Options)!);
        _reviews[key] = (spec, result, review);
        return _reviews[key];
    }

    private static JsonObject SingleInstance(JsonObject config)
    {
        var clone = (JsonObject)config.DeepClone();
        clone["instances"] = new JsonObject { ["min"] = 1, ["max"] = 1 };
        clone["zones"] = new JsonArray("a");
        return clone;
    }

    /// <summary>Nomes que o entrevistador não pode citar: cenários ocultos e componentes da referência.</summary>
    private static IEnumerable<string> Spoilers(ChallengeSpec spec) =>
        spec.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Hidden).Select(s => s.Name)
            .Concat(spec.Reference?.Nodes.Where(n => !Archetypes.IsClient(n.Archetype) && !Archetypes.IsExternal(n.Archetype)).Select(n => n.Label) ?? [])
            .Where(s => s.Length > 6)
            .Distinct(StringComparer.Ordinal);

    private ChallengeSpec Instantiate(string seedId, string? difficulty) =>
        SeedInstantiator.Instantiate(new InstantiationRequest
        {
            Seed = content.FindSeed(seedId) ?? throw new ContentException($"Semente '{seedId}' do dataset não existe."),
            Provider = CloudProvider.Aws,
            Difficulty = SeedLoader.ParseDifficulty(difficulty ?? "senior"),
        }, content.Catalog).Challenge;

    private JsonObject Dataset(string role)
    {
        var path = Path.Combine(content.Root, "evals", $"{role}.yaml");
        if (!File.Exists(path)) throw new ContentException($"Dataset content/evals/{role}.yaml não encontrado.");
        return YamlJson.Parse(File.ReadAllText(path), $"evals/{role}.yaml") as JsonObject ?? throw new ContentException($"evals/{role}.yaml vazio.");
    }

    private IEnumerable<JsonObject> Cases(string role, int? limit)
    {
        var cases = (Dataset(role)["cases"] as JsonArray ?? []).OfType<JsonObject>();
        return limit is { } n ? cases.Take(n) : cases;
    }

    private static EvalCaseResult Case(string role, string? name, bool passed, double seconds, string detail, Dictionary<string, bool> flags) =>
        new(role, name ?? "", passed, Math.Round(seconds, 2), detail, flags);

    private static string? Str(JsonNode? node) => node is JsonValue v ? v.ToString() : null;

    private static IReadOnlyList<string> Strings(JsonNode? node) =>
        node is JsonArray array ? array.Select(Str).OfType<string>().ToList() : Str(node) is { } one ? [one] : [];
}

/// <summary>Relatório dos evals em markdown: um resumo por modelo e os casos de cada papel.</summary>
public static class AiEvalReport
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    private static readonly (string Role, string Title)[] Roles =
    [
        (PromptRoles.Personalizer, "Personalizador"), (PromptRoles.Interviewer, "Entrevistador"), (PromptRoles.Narrator, "Narrador"), (PromptRoles.Mapper, "Mapeador"),
    ];

    public static string Markdown(IReadOnlyList<ModelEvalReport> reports, string endpoint, DateTime when)
    {
        var text = new StringBuilder();
        text.AppendLine("# Avaliação dos modelos locais (spike S4)");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"{when.ToString("dd/MM/yyyy HH:mm", PtBr)} · Ollama em {endpoint}");
        text.AppendLine();
        text.AppendLine("| Modelo | Personalizador | Entrevistador | Narrador | Mapeador | 1º token | Tokens/s | S4 |");
        text.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var r in reports)
        {
            if (r.Error is not null)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"| {r.Model} | {Escape(r.Error)} | | | | | | — |");
                continue;
            }

            var cells = Roles.Select(role => Cell(r, role.Role));
            var speed = r.Speed is { } s ? $"{s.FirstTokenSeconds.ToString("0.0", PtBr)} s | {s.TokensPerSecond.ToString("0.0", PtBr)}{(s.EstimatedTokens ? " (est.)" : "")}" : "erro | erro";
            var mark = !r.Complete ? "?" : r.MeetsS4 ? "✓" : "✗";
            text.AppendLine(CultureInfo.InvariantCulture, $"| {r.Model} | {string.Join(" | ", cells)} | {speed} | {mark} |");
        }

        text.AppendLine();
        text.AppendLine("Critérios do S4: personalizador aprovado de primeira em 80% dos contextos ou mais, primeiro token em até 3 s e 15 tokens/s ou mais. Tempos: mediana por caso, com o modelo já carregado na memória.");
        var incomplete = reports.Where(r => r.Error is null && !r.Complete).ToList();
        if (incomplete.Count > 0)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture,
                $"**Dados incompletos (?)** em {string.Join(", ", incomplete.Select(r => r.Model))}: há casos com erro ao falar com o Ollama ou a velocidade não foi medida. Os casos com erro ficam fora das porcentagens. Rode de novo antes de decidir.");
        }

        AppendRecommendation(text, reports);

        foreach (var r in reports.Where(r => r.Error is null))
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"## {r.Model}");
            if (r.SpeedError is not null)
            {
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"Velocidade não medida: {Escape(r.SpeedError)}");
            }

            foreach (var (role, title) in Roles)
            {
                var cases = r.For(role);
                if (cases.Count == 0) continue;
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
                var answered = cases.Where(c => !c.Error).ToList();
                var flags = answered.SelectMany(c => c.Flags.Keys).Distinct(StringComparer.Ordinal).ToList();
                if (flags.Count > 1)
                {
                    text.AppendLine();
                    text.AppendLine(string.Join(" · ", flags.Select(f =>
                        $"{f}: {answered.Count(c => c.Flags.TryGetValue(f, out var ok) && ok)}/{answered.Count(c => c.Flags.ContainsKey(f))}")));
                }

                text.AppendLine();
                text.AppendLine("| Caso | Resultado | Tempo | Detalhe |");
                text.AppendLine("|---|---|---|---|");
                foreach (var c in cases)
                    text.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(c.Case)} | {(c.Error ? "erro" : c.Passed ? "✓" : "✗")} | {c.Seconds.ToString("0.0", PtBr)} s | {Escape(c.Detail)} |");
            }

            AppendSamples(text, r);
        }

        return text.ToString();
    }

    /// <summary>
    /// Os números dizem se o modelo acertou, não se escreve bem em português: aqui está o que a IA escreveu, para
    /// você ler (narrativa do desafio, comentário do mentor e respostas do entrevistador).
    /// </summary>
    private static void AppendSamples(StringBuilder text, ModelEvalReport report)
    {
        var groups = new (string Role, string Title, int Max)[]
        {
            (PromptRoles.Personalizer, "Narrativa do desafio (personalizador)", 3),
            (PromptRoles.Narrator, "Comentário do mentor (narrador)", 3),
            (PromptRoles.Interviewer, "Respostas do entrevistador", 4),
        };
        var any = false;
        foreach (var (role, title, max) in groups)
        {
            var samples = report.For(role).Where(c => c.Sample is not null).Take(max).ToList();
            if (samples.Count == 0) continue;
            if (!any)
            {
                text.AppendLine();
                text.AppendLine("### O que a IA escreveu");
                any = true;
            }

            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"**{title}**");
            foreach (var c in samples)
            {
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"*{AiText.Clean(c.Case, 120)}*");
                text.AppendLine();
                foreach (var line in c.Sample!.Split('\n')) text.AppendLine(line.Length == 0 ? ">" : $"> {line}");
            }
        }
    }

    /// <summary>
    /// Entre os que passam no S4: se o mais preciso também é o mais rápido, é a sugestão; se não, mostra a troca
    /// (precisão × velocidade) em vez de esconder a decisão num critério arbitrário.
    /// </summary>
    private static void AppendRecommendation(StringBuilder text, IReadOnlyList<ModelEvalReport> reports)
    {
        var complete = reports.Where(r => r.Complete).ToList();
        if (complete.Count == 0) return;
        var passing = complete.Where(r => r.MeetsS4).ToList();
        text.AppendLine();
        if (passing.Count == 0)
        {
            text.AppendLine("Nenhum modelo com dados completos passou nos critérios: o app continua funcionando sem IA (modo none).");
            return;
        }

        var accurate = passing.OrderByDescending(r => r.Quality).ThenBy(r => r.MedianSeconds).First();
        var fast = passing.OrderBy(r => r.MedianSeconds).ThenByDescending(r => r.Quality).First();
        if (passing.Count == 1 || accurate == fast)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Sugestão de modelo padrão: **{accurate.Model}** (use em `Ai:Ollama:Model`): o mais preciso e o mais rápido dos que passam no S4.");
            return;
        }

        string Line(ModelEvalReport r) => $"{(r.Quality * 100).ToString("0", PtBr)}% de acerto médio nos papéis e {r.MedianSeconds.ToString("0.0", PtBr)} s por chamada";
        text.AppendLine("Mais de um modelo passa no S4, com uma troca entre precisão e velocidade:");
        text.AppendLine();
        text.AppendLine(CultureInfo.InvariantCulture, $"- Mais preciso: **{accurate.Model}** ({Line(accurate)}).");
        text.AppendLine(CultureInfo.InvariantCulture, $"- Mais rápido: **{fast.Model}** ({Line(fast)}; {(accurate.MedianSeconds / Math.Max(0.001, fast.MedianSeconds)).ToString("0.0", PtBr)}× mais rápido).");
        text.AppendLine();
        text.AppendLine("Leia a seção \"O que a IA escreveu\" de cada um e escolha pelo equilíbrio (use em `Ai:Ollama:Model`).");
    }

    private static string Cell(ModelEvalReport report, string role)
    {
        var cases = report.For(role);
        if (cases.Count == 0) return "—";
        var answered = cases.Where(c => !c.Error).ToList();
        var errors = cases.Count - answered.Count;
        var summary = answered.Count == 0 ? "só erros" : $"{answered.Count(c => c.Passed)}/{answered.Count} ({(report.PassRate(role) * 100).ToString("0", PtBr)}%) · {Median(answered.Select(c => c.Seconds)).ToString("0.0", PtBr)} s";
        return errors > 0 ? $"{summary} · {errors} com erro" : summary;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

public static class AiEvalCommand
{
    public const string DefaultModel = "qwen2.5:3b";

    /// <summary>
    /// Modelos a avaliar: <c>--models a,b</c>, <c>--models a --models b</c> ou nomes soltos depois de <c>ai eval</c>
    /// (o PowerShell transforma <c>a,b</c> sem aspas em dois argumentos).
    /// </summary>
    public static IReadOnlyList<string> Models(Args args)
    {
        var models = args.Options("models").Concat(args.Positionals.Skip(1)).Distinct(StringComparer.Ordinal).ToList();
        return models.Count > 0 ? models : [DefaultModel];
    }

    public static async Task<int> RunAsync(string contentRoot, Args args, TextWriter output)
    {
        var content = ContentRepository.Load(contentRoot);
        var prompts = PromptLibrary.Load(Path.Combine(content.Root, "prompts"));
        var endpoint = args.Option("endpoint") ?? new OllamaOptions().Endpoint;
        var models = Models(args);
        var roles = (args.Option("roles") ?? string.Join(',', AiEvalRunner.AllRoles)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var unknown = roles.Where(r => !AiEvalRunner.AllRoles.Contains(r)).ToList();
        if (unknown.Count > 0) throw new UsageException($"Papéis desconhecidos: {string.Join(", ", unknown)} (use {string.Join(", ", AiEvalRunner.AllRoles)}).");
        int? limit = args.Option("limit") is { } l ? int.Parse(l, CultureInfo.InvariantCulture) : null;
        var outDirectory = args.Option("out") ?? Path.Combine("exports", "evals");

        using var http = new HttpClient();
        var runner = new AiEvalRunner(content, prompts, output);
        var reports = new List<ModelEvalReport>();
        foreach (var model in models)
        {
            output.WriteLine($"Avaliando {model}…");
            using var runtime = new AiRuntime(new AiOptions
            {
                Mode = "ollama",
                Ollama = new OllamaOptions { Endpoint = endpoint, Model = model, TimeoutSeconds = 300, ProbeTimeoutMs = 30_000 },
            }, http, TimeProvider.System);
            var report = await runner.RunAsync(runtime, roles, limit, CancellationToken.None);
            output.WriteLine(report.Error ?? $"  {report.Cases.Count(c => c.Passed)}/{report.Cases.Count(c => !c.Error)} casos aprovados"
                + (report.Errors > 0 ? $", {report.Errors} com erro ao falar com o Ollama (rode de novo)." : "."));
            reports.Add(report);
        }

        var now = DateTime.Now;
        var markdown = AiEvalReport.Markdown(reports, endpoint, now);
        Directory.CreateDirectory(outDirectory);
        var name = $"evals-{now:yyyyMMdd-HHmmss}";
        await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{name}.md"), markdown);
        await File.WriteAllTextAsync(Path.Combine(outDirectory, $"{name}.json"), JsonSerializer.Serialize(reports, LabJson.Options));
        output.WriteLine();
        output.WriteLine(markdown);
        output.WriteLine($"Relatório salvo em {Path.Combine(outDirectory, name)}.md");
        return reports.Any(r => r.Error is null) ? 0 : 1;
    }
}
