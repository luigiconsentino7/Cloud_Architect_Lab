using System.Text.Json;
using System.Text.Json.Serialization;
using Lab.Modules;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Http;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Só na própria máquina: 127.0.0.1 / ::1 (spec 09). O WebApplicationFactory dos testes ignora o Kestrel.
var port = builder.Configuration.GetValue("Lab:Port", 5080);
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenLocalhost(port));

// Contra DNS rebinding: só aceita Host de loopback (o AllowedHosts do appsettings vira a lista explícita).
builder.Services.AddHostFiltering(o =>
{
    o.AllowedHosts = (builder.Configuration["AllowedHosts"] ?? "localhost;127.0.0.1;[::1]")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    o.AllowEmptyHosts = false;
});
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => Configure(o.SerializerOptions));
builder.Services.AddLabModules(builder.Configuration);
builder.Services.AddSignalR().AddJsonProtocol(o => Configure(o.PayloadSerializerOptions));
builder.Services.AddHostedService<DailyBackupService>();

if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("cloud-architect-lab"))
        .WithTracing(t => t.AddAspNetCoreInstrumentation().AddOtlpExporter());
}

var app = builder.Build();
await app.Services.InitializeDatabaseAsync();

app.UseHostFiltering();
app.UseExceptionHandler();
app.UseStatusCodePages();
var allowed = builder.Configuration.GetSection("Lab:AllowedOrigins").Get<string[]>() ?? [];
app.UseMiddleware<LocalhostProtectionMiddleware>((IReadOnlyCollection<string>)allowed);
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapLabEndpoints();

// SPA Angular publicada em wwwroot: rotas do cliente caem no index.html.
if (File.Exists(Path.Combine(app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"), "index.html")))
    app.MapFallbackToFile("index.html");

app.Run();

static void Configure(JsonSerializerOptions options)
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower));
}

/// <summary>Cópia diária do lab.db com rotação de 7 (na inicialização e a cada 6 h).</summary>
internal sealed class DailyBackupService(LabOptions options, TimeProvider clock, ILogger<DailyBackupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), clock);
        do
        {
            try
            {
                LabModules.RotateDailyBackup(options, clock.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                logger.LogWarning(ex, "Backup diário não foi feito.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Ponto de entrada visível para o WebApplicationFactory dos testes de integração.</summary>
public partial class Program;
