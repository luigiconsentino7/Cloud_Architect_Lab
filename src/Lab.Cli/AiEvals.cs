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
using Microsoft.Extensions.Logging.Abstractions;

namespace Lab.Cli;

/// <param name="Flags">Critérios parciais do caso (ex.: delta válido, monta o desafio, aderente ao contexto).</param>
public sealed record EvalCaseResult(string Role, string Case, bool Passed, double Seconds, string Detail, IReadOnlyDictionary<string, bool> Flags);

/// <param name="EstimatedTokens">Sem a contagem do Ollama, os tokens são estimados por caracteres / 4.</param>
public sealed record SpeedProbe(double FirstTokenSeconds, double TokensPerSecond, bool EstimatedTokens);

public sealed record ModelEvalReport(string Model, IReadOnlyList<EvalCaseResult> Cases, SpeedProbe? Speed, string? Error)
{
    public IReadOnlyList<EvalCaseResult> For(string role) => Cases.Where(c => c.Role == role).ToList();

    public double PassRate(string role) => For(role) is { Count: > 0 } list ? (double)list.Count(c => c.Passed) / list.Count : 0;

    /// <summary>Critérios do spike S4: personalizador com ≥ 80% de primeira, 1º token ≤ 3 s e ≥ 15 tokens/s.</summary>
    public bool MeetsS4 => Error is null && Speed is { FirstTokenSeconds: <= 3, TokensPerSecond: >= 15 }
        && (For(PromptRoles.Personalizer).Count == 0 || PassRate(PromptRoles.Personalizer) >= 0.8);
}

/// <summary>
/// Evals dos papéis de IA (spec 06, spike S4): roda os datasets de <c>content/evals</c> contra um modelo local e mede
/// qualidade (deltas válidos e aderentes, respostas certas do entrevistador, narrativas aceitas, mapeamentos certos)
/// e velocidade (primeiro token e tokens por segundo).
/// </summary>
public sealed class AiEvalRunner(ContentBundle content, PromptLibrary prompts, TextWriter log)
{
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
        var status = await runtime.StatusAsync(cancellationToken);
        if (status.Mode != AiMode.Ollama) return new ModelEvalReport(model, [], null, status.Detail);

        var results = new List<EvalCaseResult>();
        if (roles.Contains(PromptRoles.Personalizer)) results.AddRange(await PersonalizerAsync(runtime, limit, cancellationToken));
        if (roles.Contains(PromptRoles.Interviewer)) results.AddRange(await InterviewerAsync(runtime, limit, cancellationToken));
        if (roles.Contains(PromptRoles.Narrator)) results.AddRange(await NarratorAsync(runtime, limit, cancellationToken));
        if (roles.Contains(PromptRoles.Mapper)) results.AddRange(await MapperAsync(runtime, limit, cancellationToken));
        var speed = await SpeedAsync(runtime, cancellationToken);
        return new ModelEvalReport(model, results, speed, null);
    }

    private async Task<List<EvalCaseResult>> PersonalizerAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var personalizer = new ChallengePersonalizer(runtime, prompts, NullLogger<ChallengePersonalizer>.Instance);
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
            if (result is null)
            {
                results.Add(Case(PromptRoles.Personalizer, context, false, seconds, "o modelo não devolveu um delta válido",
                    new() { ["delta-valido"] = false, ["monta"] = false, ["aderente"] = false }));
                continue;
            }

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
            var detail = $"{seed.Id}{(error is null ? "" : $"; não monta: {error}")}; ajustes da validação: {result.Adjustments.Count}; narrativa {(result.Delta.Narrative is null ? "descartada" : "ok")}";
            results.Add(Case(PromptRoles.Personalizer, context, error is null && adherent, seconds, detail,
                new() { ["delta-valido"] = true, ["monta"] = error is null, ["aderente"] = adherent }));
        }

        return results;
    }

    private async Task<List<EvalCaseResult>> InterviewerAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var interviewer = new Interviewer(runtime, prompts, NullLogger<Interviewer>.Instance);
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
            if (answer.Source != AnswerSource.Ai)
            {
                results.Add(Case(PromptRoles.Interviewer, $"{seedId}: {question}", false, seconds, "o modelo não respondeu (caiu para a resposta curada)", new()));
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
                    : (true, "não revelou referência nem cenários ocultos"),
                _ => (Admissions.Any(a => normalized.Contains(a, StringComparison.Ordinal)), "esperava admitir que o enunciado não define"),
            };
            results.Add(Case(PromptRoles.Interviewer, $"{seedId}: {question}", passed, seconds, $"{detail} · \"{AiText.Clean(answer.Text, 140)}\"",
                new() { [expect] = passed }));
        }

        return results;
    }

    private async Task<List<EvalCaseResult>> NarratorAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var narrator = new ReviewNarrator(runtime, prompts, NullLogger<ReviewNarrator>.Instance);
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
                results.Add(Case(PromptRoles.Narrator, seedId, false, watch.Elapsed.TotalSeconds, $"falha do modelo: {ex.Message}", new()));
                continue;
            }

            var seconds = watch.Elapsed.TotalSeconds;
            var top = result.Review.Improvements.FirstOrDefault()?.Title ?? "";
            var text = narrative is null ? "" : AiText.Normalize(string.Join(' ', narrative.Paragraphs));
            var mentionsTop = top.Length > 0 && AiText.Normalize(top).Split(' ').Where(w => w.Length >= 5).Any(w => text.Contains(w, StringComparison.Ordinal));
            results.Add(Case(PromptRoles.Narrator, $"{seedId} (nota {result.Score.FinalScore})", narrative is not null, seconds,
                narrative is null ? "narrativa descartada: citou números fora da revisão duas vezes" : $"cita o ponto de maior impacto: {(mentionsTop ? "sim" : "não")} ({top})",
                new() { ["aceita"] = narrative is not null, ["cita-o-principal"] = mentionsTop }));
        }

        return results;
    }

    private async Task<List<EvalCaseResult>> MapperAsync(AiRuntime runtime, int? limit, CancellationToken cancellationToken)
    {
        var mapper = new LabelMapper(runtime, prompts, NullLogger<LabelMapper>.Instance);
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
            var answer = suggestion?.CatalogId ?? LabelMapper.None;
            results.Add(Case(PromptRoles.Mapper, label, expected.Contains(answer), watch.Elapsed.TotalSeconds,
                $"respondeu {answer}; aceitos: {string.Join(", ", expected)}", new() { ["certo"] = expected.Contains(answer) }));
        }

        return results;
    }

    /// <summary>Primeiro token e tokens por segundo num pedido curto, em streaming (critérios de velocidade do S4).</summary>
    private static async Task<SpeedProbe?> SpeedAsync(AiRuntime runtime, CancellationToken cancellationToken)
    {
        var client = await runtime.ClientAsync(cancellationToken);
        if (client is null) return null;
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
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var total = watch.Elapsed.TotalSeconds;
        var generation = Math.Max(0.001, total - (first ?? 0));
        var outputTokens = tokens ?? Math.Max(1, text.Length / 4);
        return new SpeedProbe(Math.Round(first ?? total, 2), Math.Round(outputTokens / generation, 1), tokens is null);
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
                text.AppendLine(CultureInfo.InvariantCulture, $"| {r.Model} | {r.Error} | | | | | | ✗ |");
                continue;
            }

            var cells = Roles.Select(role => Cell(r, role.Role));
            var speed = r.Speed is { } s ? $"{s.FirstTokenSeconds.ToString("0.0", PtBr)} s | {s.TokensPerSecond.ToString("0.0", PtBr)}{(s.EstimatedTokens ? " (est.)" : "")}" : "— | —";
            text.AppendLine(CultureInfo.InvariantCulture, $"| {r.Model} | {string.Join(" | ", cells)} | {speed} | {(r.MeetsS4 ? "✓" : "✗")} |");
        }

        text.AppendLine();
        text.AppendLine("Critérios do S4: personalizador aprovado de primeira em 80% dos contextos ou mais, primeiro token em até 3 s e 15 tokens/s ou mais.");
        var best = reports.Where(r => r.MeetsS4).OrderByDescending(r => r.Cases.Count(c => c.Passed)).FirstOrDefault();
        text.AppendLine(best is null
            ? "Nenhum modelo passou nos critérios: o app continua funcionando sem IA (modo none)."
            : $"Sugestão de modelo padrão: **{best.Model}** (use em `Ai:Ollama:Model`).");

        foreach (var r in reports.Where(r => r.Error is null))
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"## {r.Model}");
            foreach (var (role, title) in Roles)
            {
                var cases = r.For(role);
                if (cases.Count == 0) continue;
                text.AppendLine();
                text.AppendLine(CultureInfo.InvariantCulture, $"### {title}");
                var flags = cases.SelectMany(c => c.Flags.Keys).Distinct(StringComparer.Ordinal).ToList();
                if (flags.Count > 1)
                {
                    text.AppendLine();
                    text.AppendLine(string.Join(" · ", flags.Select(f =>
                        $"{f}: {cases.Count(c => c.Flags.TryGetValue(f, out var ok) && ok)}/{cases.Count(c => c.Flags.ContainsKey(f))}")));
                }

                text.AppendLine();
                text.AppendLine("| Caso | Resultado | Tempo | Detalhe |");
                text.AppendLine("|---|---|---|---|");
                foreach (var c in cases)
                    text.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(c.Case)} | {(c.Passed ? "✓" : "✗")} | {c.Seconds.ToString("0.0", PtBr)} s | {Escape(c.Detail)} |");
            }
        }

        return text.ToString();
    }

    private static string Cell(ModelEvalReport report, string role)
    {
        var cases = report.For(role);
        if (cases.Count == 0) return "—";
        var passed = cases.Count(c => c.Passed);
        var seconds = cases.Average(c => c.Seconds);
        return $"{passed}/{cases.Count} ({(report.PassRate(role) * 100).ToString("0", PtBr)}%) · {seconds.ToString("0.0", PtBr)} s";
    }

    private static string Escape(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}

public static class AiEvalCommand
{
    public static async Task<int> RunAsync(string contentRoot, Args args, TextWriter output)
    {
        var content = ContentRepository.Load(contentRoot);
        var prompts = PromptLibrary.Load(Path.Combine(content.Root, "prompts"));
        var endpoint = args.Option("endpoint") ?? "http://localhost:11434";
        var models = (args.Option("models") ?? "qwen2.5:3b").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
                Ollama = new OllamaOptions { Endpoint = endpoint, Model = model, TimeoutSeconds = 300 },
            }, http, TimeProvider.System);
            var report = await runner.RunAsync(runtime, roles, limit, CancellationToken.None);
            output.WriteLine(report.Error ?? $"  {report.Cases.Count(c => c.Passed)}/{report.Cases.Count} casos aprovados.");
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
