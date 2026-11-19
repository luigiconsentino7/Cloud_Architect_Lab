using System.Text.Json;
using System.Text.Json.Serialization;
using Lab.Modules;
using Lab.Modules.Shared;
using Lab.Modules.Shared.Http;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Executável publicado: o wwwroot e o content/ ficam ao lado dele, não no diretório de onde ele foi aberto.
var published = !args.Any(a => a.StartsWith("--contentRoot", StringComparison.OrdinalIgnoreCase))
    && File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
// No executável, um erro na abertura não pode fechar a janela antes de a mensagem ser lida.
if (published)
{
    AppDomain.CurrentDomain.UnhandledException += (_, e) =>
    {
        Console.Error.WriteLine($"\nO Cloud Architect Lab não conseguiu abrir: {(e.ExceptionObject as Exception)?.Message}");
        Desktop.WaitForEnter();
    };
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = published ? AppContext.BaseDirectory : null,
});

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

// CSP restritiva (spec 09): só scripts do próprio app (o Excalidraw usa WebAssembly para as fontes na exportação),
// estilos inline (componentes Angular e o Excalidraw), WebSocket do SignalR no próprio host e nada de iframes.
var contentSecurityPolicy = string.Join("; ",
    "default-src 'self'",
    "script-src 'self' 'wasm-unsafe-eval'",
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    $"connect-src 'self' ws://localhost:{port} ws://127.0.0.1:{port}",
    "worker-src 'self' blob:",
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
    "frame-ancestors 'none'");
app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = contentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    });
    await next();
});
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

// No desktop, o executável abre o app no navegador padrão (desligado em desenvolvimento, testes e CI).
var openBrowser = builder.Configuration.GetValue("Lab:OpenBrowser", false);
var url = $"http://localhost:{port}";
app.Lifetime.ApplicationStarted.Register(() =>
{
    app.Logger.LogInformation("Cloud Architect Lab {Version} aberto em {Url}. Deixe esta janela aberta enquanto usa o app; para encerrar, feche-a.",
        AppVersion.Display, url);
    if (openBrowser) Desktop.OpenBrowser(url, app.Logger);
});

// A porta já está ocupada: quase sempre é o próprio Lab, aberto antes (dois cliques no executável).
if (published && !Desktop.PortIsFree(port)) return await Desktop.PortInUseAsync(port, openBrowser, waitForEnter: true, app.Logger);

try
{
    await app.StartAsync();
}
catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
{
    // Ocupada entre a checagem e a subida, ou fora do executável (dotnet run duas vezes).
    return await Desktop.PortInUseAsync(port, openBrowser, waitForEnter: published, app.Logger);
}

await app.WaitForShutdownAsync();
return 0;

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
