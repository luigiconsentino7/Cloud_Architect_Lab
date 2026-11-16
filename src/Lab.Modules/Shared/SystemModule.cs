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

        // Backup completo em JSON (perfil, desafios, tentativas, avaliações com séries, XP, conquistas e conversas).
        app.MapPost("/api/backup/export", async (BackupService backup, CancellationToken ct) => Results.Ok(await backup.ExportAsync(ct)))
            .WithTags("Sistema");

        // Substitui todos os dados pelos do backup; antes, copia o banco atual para backups/.
        app.MapPost("/api/backup/import", async (BackupFile file, BackupService backup, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await backup.ImportAsync(file, ct));
            }
            catch (BackupException ex)
            {
                return Results.Problem(detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Backup inválido");
            }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(512L * 1024 * 1024)).WithTags("Sistema");

        app.MapGet("/api/health", (LabContent content) => Results.Ok(new { status = "ok", catalogVersion = content.Catalog.Version, contentHash = content.ContentHash, seeds = content.Seeds.Count }))
            .WithTags("Sistema");
    }
}
