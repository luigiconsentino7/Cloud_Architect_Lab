using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Modules.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Shared;

public sealed record PaletteComponent(
    string Id,
    string Archetype,
    string DisplayName,
    string? ShortName,
    string Category,
    IReadOnlyList<string> Aliases,
    ComponentScope Scope,
    bool IsDefault,
    bool Shared,
    IReadOnlyDictionary<string, string> Capabilities,
    JsonObject ConfigSchema,
    string? Docs);

public sealed record PaletteArchetype(string Id, string DisplayName, string Category);

public sealed record PaletteResponse(string CatalogVersion, CloudProvider Provider, IReadOnlyList<PaletteArchetype> Archetypes, IReadOnlyList<PaletteComponent> Components);

/// <summary>Catálogo para a paleta, textos das regras e backup.</summary>
public static class SystemEndpoints
{
    public static void MapSystem(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/catalog/{provider}", (string provider, LabContent content, HttpContext http) =>
        {
            CloudProvider parsed;
            try
            {
                parsed = CloudProviders.Parse(provider);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status404NotFound);
            }

            var etag = $"\"{content.ContentHash[7..23]}-{parsed.Code()}\"";
            if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
            http.Response.Headers.ETag = etag;
            var catalog = content.Catalog;
            return Results.Ok(new PaletteResponse(
                catalog.Version,
                parsed,
                catalog.Archetypes.Values.OrderBy(a => a.Id, StringComparer.Ordinal).Select(a => new PaletteArchetype(a.Id, a.DisplayName, a.Category)).ToList(),
                catalog.ForProvider(parsed).Select(c => new PaletteComponent(c.Id, c.Archetype, c.DisplayName, c.ShortName, c.Category, c.Aliases, c.Scope,
                    c.IsDefault, c.Shared, c.Capabilities, c.ConfigSchema, c.Docs)).ToList()));
        }).WithTags("Conteúdo");

        app.MapGet("/api/rules", (LabContent content) => Results.Ok(content.RuleTexts.All.OrderBy(t => t.Id, StringComparer.Ordinal)))
            .WithTags("Conteúdo");

        // Exportação completa em JSON (a importação fica para depois: por ora, backup = copiar o lab.db).
        app.MapPost("/api/backup/export", async (LabDbContext db, LabContent content, TimeProvider clock, CancellationToken ct) => Results.Ok(new
        {
            exportedAt = clock.GetUtcNow().UtcDateTime,
            contentHash = content.ContentHash,
            profiles = await db.Profiles.AsNoTracking().ToListAsync(ct),
            challenges = await db.Challenges.AsNoTracking().ToListAsync(ct),
            attempts = await db.Attempts.AsNoTracking().ToListAsync(ct),
            evaluations = await db.Evaluations.AsNoTracking().Select(e => new { e.Id, e.AttemptId, e.ChallengeId, e.Kind, e.Status, e.FinalScore, e.Rank, e.ManifestJson, e.CreatedAt, e.CompletedAt }).ToListAsync(ct),
            xp = await db.XpEntries.AsNoTracking().ToListAsync(ct),
            progress = await db.UserProgress.AsNoTracking().ToListAsync(ct),
            competencies = await db.CompetencyRatings.AsNoTracking().ToListAsync(ct),
            achievements = await db.UserAchievements.AsNoTracking().ToListAsync(ct),
        })).WithTags("Sistema");

        app.MapGet("/api/health", (LabContent content) => Results.Ok(new { status = "ok", catalogVersion = content.Catalog.Version, contentHash = content.ContentHash, seeds = content.Seeds.Count }))
            .WithTags("Sistema");
    }
}
