using System.Globalization;
using System.Text;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Seeds;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Profile.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Persistence;
using Lab.Pipeline.Seeds;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Challenges;

public sealed record TwistInfo(string Id, string Title, string Description, Difficulty MinDifficulty);

public sealed record LibraryItem(
    string SeedId,
    string Title,
    string Summary,
    string Track,
    string Domain,
    IReadOnlyList<string> Tags,
    IReadOnlyList<Difficulty> Difficulties,
    IReadOnlyList<TwistInfo> Twists,
    IReadOnlyList<string> MatchedTerms,
    double Score);

/// <summary>Parte pública do desafio: sem cenários ocultos, sem referência e sem o texto das dicas.</summary>
public sealed record ChallengePublicView
{
    public required Guid Id { get; init; }
    public required string SeedId { get; init; }
    public int SeedVersion { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public string Narrative { get; init; } = "";
    public string Track { get; init; } = "";
    public string Domain { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public Difficulty Difficulty { get; init; }
    public CloudProvider Provider { get; init; }
    public GameMode Mode { get; init; }
    public string Region { get; init; } = "";
    public IReadOnlyList<UseCaseSpec> UseCases { get; init; } = [];
    public IReadOnlyList<ExternalSystemSpec> ExternalSystems { get; init; } = [];
    public WorkloadSpec Workload { get; init; } = new();
    public IReadOnlyList<ScenarioSpec> Scenarios { get; init; } = [];
    public IReadOnlyList<SloSpec> Slos { get; init; } = [];
    public int HiddenScenarios { get; init; }
    public ConstraintsSpec Constraints { get; init; } = new();
    public IReadOnlyList<string> Competencies { get; init; } = [];
    public RubricWeights Weights { get; init; } = new();
    public int HintCount { get; init; }
    public IReadOnlyList<ClarificationSpec> Clarifications { get; init; } = [];
    public IReadOnlyDictionary<string, double> Variables { get; init; } = new Dictionary<string, double>();
    public string Status { get; init; } = ChallengeStatus.Ready;
    public DateTime CreatedAt { get; init; }

    /// <summary>Modelo de IA local que personalizou o enunciado (nulo quando veio direto da biblioteca).</summary>
    public string? PersonalizedBy { get; init; }

    public string? PersonalizationNote { get; init; }

    public static ChallengePublicView From(Guid id, ChallengeSpec spec, string status, DateTime createdAt)
    {
        var publicScenarios = spec.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Public).ToList();
        var publicIds = publicScenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return new ChallengePublicView
        {
            Id = id,
            SeedId = spec.SeedId,
            SeedVersion = spec.SeedVersion,
            Title = spec.Title,
            Summary = spec.Summary,
            Narrative = spec.Narrative,
            Track = spec.Track,
            Domain = spec.Domain,
            Tags = spec.Tags,
            Difficulty = spec.Difficulty,
            Provider = spec.Provider,
            Mode = spec.Mode,
            Region = spec.Region,
            UseCases = spec.UseCases,
            ExternalSystems = spec.ExternalSystems,
            Workload = spec.Workload,
            Scenarios = publicScenarios,
            Slos = spec.TestPlan.Slos
                .Select(s => s with { Scenarios = s.Scenarios.Where(publicIds.Contains).ToList() })
                .Where(s => s.Scenarios.Count > 0)
                .ToList(),
            HiddenScenarios = spec.TestPlan.Scenarios.Count - publicScenarios.Count,
            Constraints = spec.Constraints,
            Competencies = spec.Competencies,
            Weights = spec.Weights,
            HintCount = spec.Hints.Count,
            Clarifications = spec.Clarifications,
            Variables = spec.Variables,
            Status = status,
            CreatedAt = createdAt,
        };
    }
}

/// <summary>Busca guiada na biblioteca: termos casados no título, nas tags, no domínio e na narrativa, sem acentos.</summary>
public static class LibrarySearch
{
    public static IReadOnlyList<LibraryItem> Search(IEnumerable<Seed> seeds, string? query, Difficulty? difficulty, string? track)
    {
        var terms = Normalize(query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2).Distinct().ToList();
        var results = new List<LibraryItem>();
        foreach (var seed in seeds)
        {
            if (difficulty is { } d && seed.Difficulties.Count > 0 && !seed.Difficulties.Contains(d)) continue;
            if (!string.IsNullOrWhiteSpace(track) && !string.Equals(seed.Track, track, StringComparison.OrdinalIgnoreCase)) continue;

            var title = Normalize(seed.Title);
            var tags = seed.Tags.Select(Normalize).ToList();
            var body = Normalize($"{seed.Summary} {seed.Domain} {seed.Track} {seed.Body["narrative"]}");
            var matched = new List<string>();
            var score = 0.0;
            foreach (var term in terms)
            {
                var hit = 0.0;
                if (title.Contains(term, StringComparison.Ordinal)) hit += 3;
                if (tags.Any(t => t.Contains(term, StringComparison.Ordinal))) hit += 2;
                if (body.Contains(term, StringComparison.Ordinal)) hit += 1;
                if (hit > 0) matched.Add(term);
                score += hit;
            }

            if (terms.Count > 0 && score == 0) continue;
            results.Add(new LibraryItem(seed.Id, seed.Title, seed.Summary, seed.Track, seed.Domain, seed.Tags, seed.Difficulties,
                seed.Twists.Select(t => new TwistInfo(t.Id, t.Title, t.Description, t.MinDifficulty)).ToList(), matched, score));
        }

        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Track, StringComparer.Ordinal).ThenBy(r => r.Title, StringComparer.Ordinal).ToList();
    }

    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return builder.ToString();
    }
}

public sealed record CreateChallengeRequest(
    string SeedId,
    string Provider,
    string? Difficulty,
    string? Mode,
    IReadOnlyList<string>? Twists,
    IReadOnlyDictionary<string, double>? Variables,
    bool RandomVariation = true);

public sealed record ChallengeSummary(Guid Id, string SeedId, string Title, string Track, CloudProvider Provider, Difficulty Difficulty, GameMode Mode, DateTime CreatedAt);

public sealed class ChallengeService(LabDbContext db, LabContent content, IProfileQueries profiles, TimeProvider clock) : IChallengeQueries, IChallengeLibrary
{
    public Task<ChallengeEntity> CreateAsync(CreateChallengeRequest request, CancellationToken cancellationToken) =>
        CreateEntityAsync(
            new ChallengeOrder(request.SeedId, request.Provider, request.Difficulty, request.Mode) { Variables = request.Variables, Twists = request.Twists ?? [] },
            request.RandomVariation && request.Variables is null ? Random.Shared.NextInt64(1, long.MaxValue) : null,
            cancellationToken);

    /// <summary>Pedido de outro módulo (ex.: o chat): as variáveis que o delta não fixou são sorteadas na faixa.</summary>
    async Task<ChallengeInfo> IChallengeLibrary.CreateAsync(ChallengeOrder order, CancellationToken cancellationToken)
    {
        var c = await CreateEntityAsync(order, Random.Shared.NextInt64(1, long.MaxValue), cancellationToken);
        return new ChallengeInfo(c.Id, c.ProfileId, c.SeedId, c.SeedVersion, c.Title, c.Provider, c.Difficulty, c.Mode);
    }

    public IReadOnlyList<SeedSuggestion> Search(string? query, Difficulty? difficulty, int limit) =>
        LibrarySearch.Search(content.Seeds, query, difficulty, null).Take(limit)
            .Select(i => new SeedSuggestion(i.SeedId, i.Title, i.Summary, i.Track, i.Tags, i.MatchedTerms))
            .ToList();

    public async Task<ChallengeBrief?> BriefAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        var json = await db.Challenges.Where(c => c.Id == challengeId).Select(c => c.PublicJson).FirstOrDefaultAsync(cancellationToken);
        if (json is null) return null;
        var view = Json.Deserialize<ChallengePublicView>(json);
        return new ChallengeBrief(view.Id, view.Title, ChallengeStatement.Write(view), view.Clarifications);
    }

    private async Task<ChallengeEntity> CreateEntityAsync(ChallengeOrder order, long? variationSeed, CancellationToken cancellationToken)
    {
        var seed = content.FindSeed(order.SeedId) ?? throw new SeedException($"Semente '{order.SeedId}' não existe.");
        var difficulty = order.Difficulty is { } d ? Lab.Content.SeedLoader.ParseDifficulty(d)
            : seed.Difficulties.Contains(Difficulty.Senior) ? Difficulty.Senior : seed.Difficulties.FirstOrDefault(Difficulty.Senior);
        var mode = order.Mode is { } m && Enum.TryParse<GameMode>(m, ignoreCase: true, out var parsed) ? parsed : GameMode.Treino;
        CloudProvider provider;
        try
        {
            provider = CloudProviders.Parse(order.Provider);
        }
        catch (ArgumentException ex)
        {
            throw new SeedException(ex.Message);
        }

        var id = Guid.CreateVersion7();
        var instance = SeedInstantiator.Instantiate(new InstantiationRequest
        {
            Seed = seed,
            Provider = provider,
            Difficulty = difficulty,
            Mode = mode,
            Twists = order.Twists,
            Variables = order.Variables,
            VariationSeed = variationSeed,
            ChallengeId = $"chl_{id:N}",
        }, content.Catalog);

        var now = clock.GetUtcNow().UtcDateTime;
        var spec = Personalize(instance.Challenge, order);
        var view = ChallengePublicView.From(id, spec, ChallengeStatus.Ready, now) with
        {
            PersonalizedBy = order.PersonalizedBy,
            PersonalizationNote = order.PersonalizationNote,
        };
        var entity = new ChallengeEntity
        {
            Id = id,
            ProfileId = await profiles.CurrentProfileIdAsync(cancellationToken),
            SeedId = seed.Id,
            SeedVersion = seed.Version,
            Title = spec.Title,
            Provider = provider.Code(),
            Difficulty = difficulty.ToString().ToLowerInvariant(),
            Mode = mode.ToString().ToLowerInvariant(),
            VariationJson = Json.Serialize(new { variables = spec.Variables, twists = spec.Twists }),
            PersonalizationJson = order.PersonalizationJson,
            SpecJson = Json.Serialize(spec),
            PublicJson = Json.Serialize(view),
            MasterSeed = spec.MasterSeed,
            ReferenceSceneJson = instance.ReferenceScene is null ? null : Json.Serialize(new { scene = instance.ReferenceScene, flows = instance.ReferenceFlows }),
            Status = ChallengeStatus.Ready,
            CreatedAt = now,
        };
        db.Challenges.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <summary>A narrativa da IA entra antes do enunciado curado (que mantém os números); os esclarecimentos extras, no fim.</summary>
    private static ChallengeSpec Personalize(ChallengeSpec spec, ChallengeOrder order)
    {
        if (string.IsNullOrWhiteSpace(order.Narrative) && order.ExtraClarifications.Count == 0) return spec;
        return spec with
        {
            Narrative = string.IsNullOrWhiteSpace(order.Narrative) ? spec.Narrative : $"{order.Narrative.Trim()}\n\n{spec.Narrative}",
            Clarifications = [.. spec.Clarifications, .. order.ExtraClarifications],
        };
    }

    public async Task<ChallengeInfo?> FindAsync(Guid challengeId, CancellationToken cancellationToken = default) =>
        await db.Challenges.Where(c => c.Id == challengeId)
            .Select(c => new ChallengeInfo(c.Id, c.ProfileId, c.SeedId, c.SeedVersion, c.Title, c.Provider, c.Difficulty, c.Mode))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<ChallengeSpec?> SpecAsync(Guid challengeId, CancellationToken cancellationToken = default)
    {
        var json = await db.Challenges.Where(c => c.Id == challengeId).Select(c => c.SpecJson).FirstOrDefaultAsync(cancellationToken);
        return json is null ? null : Json.Deserialize<ChallengeSpec>(json);
    }

    public async Task<string?> ReferenceSceneAsync(Guid challengeId, CancellationToken cancellationToken = default) =>
        await db.Challenges.Where(c => c.Id == challengeId).Select(c => c.ReferenceSceneJson).FirstOrDefaultAsync(cancellationToken);

    public async Task<double?> ReferenceCostAsync(Guid challengeId, CancellationToken cancellationToken = default) =>
        await db.Challenges.Where(c => c.Id == challengeId).Select(c => c.ReferenceCostUsd).FirstOrDefaultAsync(cancellationToken);

    public async Task SaveReferenceCostAsync(Guid challengeId, double cost, CancellationToken cancellationToken = default)
    {
        var challenge = await db.Challenges.FirstAsync(c => c.Id == challengeId, cancellationToken);
        challenge.ReferenceCostUsd = cost;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public static class ChallengeEndpoints
{
    public static void MapChallenges(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/library", (string? q, string? difficulty, string? track, LabContent content) =>
        {
            Difficulty? level = null;
            if (!string.IsNullOrWhiteSpace(difficulty))
            {
                try
                {
                    level = Lab.Content.SeedLoader.ParseDifficulty(difficulty);
                }
                catch (Lab.Content.ContentException ex)
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["difficulty"] = [ex.Message] });
                }
            }

            return Results.Ok(LibrarySearch.Search(content.Seeds, q, level, track));
        }).WithTags("Desafios");

        var group = app.MapGroup("/api/challenges").WithTags("Desafios");

        group.MapPost("", async (CreateChallengeRequest request, ChallengeService challenges, CancellationToken ct) =>
        {
            try
            {
                var entity = await challenges.CreateAsync(request, ct);
                return Results.Created($"/api/challenges/{entity.Id}", Json.Deserialize<ChallengePublicView>(entity.PublicJson));
            }
            catch (SeedException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Não foi possível montar o desafio");
            }
        });

        group.MapGet("", async (LabDbContext db, int? limit, CancellationToken ct) =>
        {
            var rows = await db.Challenges.OrderByDescending(c => c.CreatedAt).Take(Math.Clamp(limit ?? 20, 1, 100))
                .Select(c => c.PublicJson).ToListAsync(ct);
            return Results.Ok(rows.Select(Json.Deserialize<ChallengePublicView>)
                .Select(c => new ChallengeSummary(c.Id, c.SeedId, c.Title, c.Track, c.Provider, c.Difficulty, c.Mode, c.CreatedAt)));
        });

        group.MapGet("/{id:guid}", async (Guid id, LabDbContext db, CancellationToken ct) =>
        {
            var json = await db.Challenges.Where(c => c.Id == id).Select(c => c.PublicJson).FirstOrDefaultAsync(ct);
            return json is null ? Results.NotFound() : Results.Ok(Json.Deserialize<ChallengePublicView>(json));
        });

        // "Outro": nova variação da mesma semente, no mesmo provedor, dificuldade e modo.
        group.MapPost("/{id:guid}/variations", async (Guid id, LabDbContext db, ChallengeService challenges, CancellationToken ct) =>
        {
            var current = await db.Challenges.FirstOrDefaultAsync(c => c.Id == id, ct);
            if (current is null) return Results.NotFound();
            var spec = Json.Deserialize<ChallengeSpec>(current.SpecJson);
            var entity = await challenges.CreateAsync(new CreateChallengeRequest(current.SeedId, current.Provider, current.Difficulty, current.Mode, spec.Twists, null), ct);
            return Results.Created($"/api/challenges/{entity.Id}", Json.Deserialize<ChallengePublicView>(entity.PublicJson));
        });
    }
}
