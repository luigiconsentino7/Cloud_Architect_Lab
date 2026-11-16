using System.Text.Json.Nodes;
using Lab.Modules.Gamification.Contracts;
using Lab.Modules.Profile.Contracts;
using Lab.Modules.Shared.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lab.Modules.Profile;

public sealed class ProfileService(LabDbContext db, TimeProvider clock) : IProfileQueries
{
    private static readonly SemaphoreSlim CreateLock = new(1, 1);

    public async Task<Guid> CurrentProfileIdAsync(CancellationToken cancellationToken = default) =>
        (await CurrentAsync(cancellationToken)).Id;

    public async Task<ProfileEntity> CurrentAsync(CancellationToken cancellationToken = default)
    {
        var existing = await db.Profiles.OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;

        await CreateLock.WaitAsync(cancellationToken);
        try
        {
            existing = await db.Profiles.OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(cancellationToken);
            if (existing is not null) return existing;
            var profile = new ProfileEntity { Id = Guid.CreateVersion7(), CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.Profiles.Add(profile);
            await db.SaveChangesAsync(cancellationToken);
            return profile;
        }
        finally
        {
            CreateLock.Release();
        }
    }
}

public sealed record MeResponse(Guid Id, string DisplayName, string Locale, string DefaultProvider, JsonObject Preferences, ProgressSummary Progress);

public sealed record PreferencesRequest(string? DisplayName, string? DefaultProvider, string? Locale, JsonObject? Preferences);

public static class ProfileEndpoints
{
    private static readonly string[] Providers = ["aws", "azure", "gcp"];

    public static void MapProfile(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/me").WithTags("Perfil");

        group.MapGet("", async (ProfileService profiles, IGamificationQueries gamification, CancellationToken ct) =>
        {
            var profile = await profiles.CurrentAsync(ct);
            return Results.Ok(await Me(profile, gamification, ct));
        });

        group.MapPatch("/preferences", async (PreferencesRequest request, ProfileService profiles, LabDbContext db, IGamificationQueries gamification, CancellationToken ct) =>
        {
            var profile = await profiles.CurrentAsync(ct);
            if (request.DefaultProvider is { } provider)
            {
                if (!Providers.Contains(provider)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["defaultProvider"] = ["Use aws, azure ou gcp."] });
                profile.DefaultProvider = provider;
            }

            if (!string.IsNullOrWhiteSpace(request.DisplayName)) profile.DisplayName = request.DisplayName.Trim()[..Math.Min(80, request.DisplayName.Trim().Length)];
            if (!string.IsNullOrWhiteSpace(request.Locale)) profile.Locale = request.Locale;
            if (request.Preferences is not null) profile.PreferencesJson = request.Preferences.ToJsonString();
            await db.SaveChangesAsync(ct);
            return Results.Ok(await Me(profile, gamification, ct));
        });
    }

    private static async Task<MeResponse> Me(ProfileEntity profile, IGamificationQueries gamification, CancellationToken ct) => new(
        profile.Id, profile.DisplayName, profile.Locale, profile.DefaultProvider,
        JsonNode.Parse(profile.PreferencesJson) as JsonObject ?? [],
        await gamification.ProgressAsync(profile.Id, ct));
}
