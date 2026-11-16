using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OllamaSharp.Models;

namespace Lab.Ai;

public sealed class AiOptions
{
    /// <summary><c>auto</c> (Ollama se estiver rodando com o modelo, senão sem IA), <c>none</c> ou <c>ollama</c>.</summary>
    public string Mode { get; set; } = "auto";

    public OllamaOptions Ollama { get; set; } = new();
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";

    /// <summary>Modelo de 3–4B que cabe inteiro em 4 GB de VRAM (spec 06); trocável a qualquer momento.</summary>
    public string Model { get; set; } = "qwen2.5:3b";

    /// <summary>Janela de contexto: o cache de atenção também ocupa VRAM, então os prompts levam só o necessário.</summary>
    public int ContextTokens { get; set; } = 8192;

    /// <summary>Tempo máximo de uma resposta; depois disso o app segue sem IA.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Decide o modo efetivo e entrega o cliente do Ollama. A disponibilidade é checada com timeout curto e guardada por
/// 30 s, para o app nunca travar esperando um Ollama que não está rodando.
/// </summary>
public sealed class AiRuntime(AiOptions options, HttpClient http, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private AiStatus? _cached;
    private DateTimeOffset _checkedAt;
    private OllamaApiClient? _ollama;

    /// <summary>Substitui o cliente do Ollama (os testes injetam um cliente falso e o modo fica "ollama").</summary>
    public Func<IChatClient>? ClientFactory { get; set; }

    public AiOptions Options => options;

    public async Task<AiStatus> StatusAsync(CancellationToken cancellationToken = default)
    {
        if (Fresh(out var cached)) return cached;
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (Fresh(out cached)) return cached;
            var status = await ProbeAsync(cancellationToken);
            _cached = status;
            _checkedAt = clock.GetUtcNow();
            return status;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Esquece o status (ex.: o Ollama caiu no meio de uma resposta); a próxima chamada checa de novo.</summary>
    public void Invalidate() => _cached = null;

    /// <summary>O cliente do modelo, ou nulo no modo sem IA.</summary>
    public async Task<IChatClient?> ClientAsync(CancellationToken cancellationToken = default)
    {
        var status = await StatusAsync(cancellationToken);
        if (status.Mode != AiMode.Ollama) return null;
        if (ClientFactory is { } factory) return factory();
        return _ollama ??= new OllamaApiClient(new Uri(options.Ollama.Endpoint), options.Ollama.Model);
    }

    /// <summary>Opções comuns de uma chamada: modelo configurado, janela de contexto e limite de saída.</summary>
    public ChatOptions ChatOptions(float temperature, int maxOutputTokens)
    {
        var chat = new ChatOptions { ModelId = options.Ollama.Model, Temperature = temperature, MaxOutputTokens = maxOutputTokens };
        chat.AddOllamaOption(OllamaOption.NumCtx, options.Ollama.ContextTokens);
        return chat;
    }

    /// <summary>Token que expira no timeout configurado (ou quando o chamador cancela).</summary>
    public CancellationTokenSource Deadline(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, options.Ollama.TimeoutSeconds)));
        return source;
    }

    public void Dispose()
    {
        _ollama?.Dispose();
        _lock.Dispose();
    }

    private bool Fresh(out AiStatus status)
    {
        status = _cached!;
        return _cached is not null && clock.GetUtcNow() - _checkedAt < CacheFor;
    }

    private async Task<AiStatus> ProbeAsync(CancellationToken cancellationToken)
    {
        var configured = options.Mode.Trim().ToLowerInvariant();
        var endpoint = options.Ollama.Endpoint;
        var model = options.Ollama.Model;
        if (configured == "none")
            return new AiStatus(AiMode.None, configured, false, null, endpoint, [], "IA desligada na configuração: o app usa só conteúdo curado.");
        if (ClientFactory is not null)
            return new AiStatus(AiMode.Ollama, configured, true, model, endpoint, [model], $"IA local com {model}.");

        IReadOnlyList<string> installed;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
            var tags = await http.GetFromJsonAsync<OllamaTags>(new Uri(new Uri(endpoint), "/api/tags"), timeout.Token);
            installed = tags?.Models?.Select(m => m.Name).Order(StringComparer.Ordinal).ToList() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or UriFormatException)
        {
            return new AiStatus(AiMode.None, configured, false, null, endpoint, [],
                $"O Ollama não respondeu em {endpoint}: o app segue sem IA (busca guiada e respostas curadas).");
        }

        var hasModel = installed.Any(m => m == model || m == $"{model}:latest");
        if (!hasModel)
        {
            return new AiStatus(AiMode.None, configured, true, null, endpoint, installed,
                $"O Ollama está rodando, mas o modelo {model} não está instalado (rode: ollama pull {model}).");
        }

        return new AiStatus(AiMode.Ollama, configured, true, model, endpoint, installed, $"IA local com {model} no Ollama: nada sai da sua máquina.");
    }

    private sealed record OllamaTags([property: JsonPropertyName("models")] IReadOnlyList<OllamaModel>? Models);

    private sealed record OllamaModel([property: JsonPropertyName("name")] string Name);
}
