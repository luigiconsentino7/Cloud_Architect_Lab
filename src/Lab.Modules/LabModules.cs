using Lab.Ai;
using Lab.Content;
using Lab.Modules.Challenges;
using Lab.Modules.Challenges.Contracts;
using Lab.Modules.Conversations;
using Lab.Modules.Design;
using Lab.Modules.Design.Contracts;
using Lab.Modules.Evaluation;
using Lab.Modules.Evaluation.Contracts;
using Lab.Modules.Gamification;
using Lab.Modules.Gamification.Contracts;
using Lab.Modules.Profile;
using Lab.Modules.Profile.Contracts;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Jobs;
using Lab.Modules.Shared.Outbox;
using Lab.Modules.Shared.Persistence;
using Lab.Modules.Shared.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lab.Modules;

/// <summary>Registro dos módulos (sem MediatR: handlers explícitos na injeção de dependência).</summary>
public static class LabModules
{
    public static IServiceCollection AddLabModules(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Lab").Get<LabOptions>() ?? new LabOptions();
        options.DataDirectory ??= LabOptions.DefaultDataDirectory();
        Directory.CreateDirectory(options.DataDirectory);
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(_ => new LabContent(ContentRepository.Load(options.ContentDirectory
            ?? ContentRepository.Discover(AppContext.BaseDirectory)
            ?? ContentRepository.Discover(Directory.GetCurrentDirectory())
            ?? throw new ContentException("Pasta content/ não encontrada ao lado do executável."))));

        services.AddDbContext<LabDbContext>(o => o.UseSqlite($"Data Source={DatabasePath(options)}"));

        services.AddSingleton<JobSignal>();
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton(new JobWorkerOptions());
        services.AddHostedService<JobWorker>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddSignalR();
        services.AddSingleton<ILabNotifier, HubNotifier>();

        services.AddScoped<ProfileService>();
        services.AddScoped<IProfileQueries>(sp => sp.GetRequiredService<ProfileService>());
        services.AddScoped<ChallengeService>();
        services.AddScoped<IChallengeQueries>(sp => sp.GetRequiredService<ChallengeService>());
        services.AddScoped<AttemptService>();
        services.AddScoped<IAttemptQueries>(sp => sp.GetRequiredService<AttemptService>());
        services.AddScoped<IEvaluationScheduler, EvaluationScheduler>();
        services.AddScoped<IJobHandler, EvaluationJobHandler>();
        services.AddScoped<IGamificationQueries, GamificationQueries>();
        services.AddScoped<IOutboxHandler, EvaluationCompletedHandler>();
        services.AddScoped<IChallengeLibrary>(sp => sp.GetRequiredService<ChallengeService>());
        services.AddScoped<ConversationService>();
        services.AddScoped<IJobHandler, NarrativeJobHandler>();

        // IA opcional (spec 06): sem Ollama tudo continua funcionando no modo determinístico.
        var ai = configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton(ai);
        services.AddSingleton(sp => new AiRuntime(ai, new HttpClient(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => PromptLibrary.Load(Path.Combine(sp.GetRequiredService<LabContent>().Bundle.Root, "prompts")));
        services.AddSingleton<IChallengePersonalizer, ChallengePersonalizer>();
        services.AddSingleton<IInterviewer, Interviewer>();
        services.AddSingleton<IReviewNarrator, ReviewNarrator>();
        services.AddSingleton<ILabelMapper, LabelMapper>();
        services.AddScoped<LabelMappingService>();
        services.AddScoped<BackupService>();
        return services;
    }

    public static IEndpointRouteBuilder MapLabEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapProfile();
        app.MapChallenges();
        app.MapDesign();
        app.MapEvaluation();
        app.MapGamification();
        app.MapConversations();
        app.MapSystem();
        app.MapHub<LabHub>("/hubs/lab");
        return app;
    }

    public static string DatabasePath(LabOptions options) => Path.Combine(options.DataDirectory!, "lab.db");

    /// <summary>Aplica as migrations na inicialização — antes, copia o banco (backup automático) se houver migration pendente.</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<LabOptions>();
        var db = scope.ServiceProvider.GetRequiredService<LabDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Lab.Database");
        var path = DatabasePath(options);
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count > 0 && File.Exists(path) && new FileInfo(path).Length > 0)
        {
            var backups = Path.Combine(options.DataDirectory!, "backups");
            Directory.CreateDirectory(backups);
            var target = Path.Combine(backups, $"lab-{DateTime.UtcNow:yyyyMMdd-HHmmss}-antes-de-{pending[0]}.db");
            File.Copy(path, target, overwrite: false);
            logger.LogInformation("Backup antes das migrations: {Backup}", target);
        }

        await db.Database.MigrateAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await scope.ServiceProvider.GetRequiredService<ProfileService>().CurrentAsync(cancellationToken);
    }

    /// <summary>Cópia diária do banco com rotação de 7 (spec 09).</summary>
    public static void RotateDailyBackup(LabOptions options, DateTime nowUtc)
    {
        var path = DatabasePath(options);
        if (!File.Exists(path)) return;
        var backups = Path.Combine(options.DataDirectory!, "backups");
        Directory.CreateDirectory(backups);
        var today = Path.Combine(backups, $"lab-diario-{nowUtc:yyyyMMdd}.db");
        if (!File.Exists(today))
        {
            using var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
            using var destination = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={today}");
            source.Open();
            destination.Open();
            source.BackupDatabase(destination);
        }

        foreach (var old in Directory.GetFiles(backups, "lab-diario-*.db").Order(StringComparer.Ordinal).SkipLast(7))
            File.Delete(old);
    }
}

/// <summary>Usado só pelo <c>dotnet ef</c> para gerar migrations.</summary>
public sealed class DesignTimeDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<LabDbContext>
{
    public LabDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<LabDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
