using System.Net.Http.Json;
using System.Text.Json;
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
    /// <summary>127.0.0.1 e não "localhost": o Ollama escuta em IPv4 e, no Windows, "localhost" tenta o IPv6 primeiro (perde segundos).</summary>
    public string Endpoint { get; set; } = "http://127.0.0.1:11434";

    /// <summary>
    /// Modelo de 3–4B para uma GPU de 4 GB (spec 06). O padrão é o mais preciso no S4 (gemma3:4b: ≈ 19 tokens/s, 2 s por
    /// resposta do entrevistador); o qwen2.5:3b é ≈ 4× mais rápido e menos confiável com números e na escrita.
    /// </summary>
    public string Model { get; set; } = "gemma3:4b";

    /// <summary>Janela de contexto: o cache de atenção também ocupa VRAM, então os prompts levam só o necessário.</summary>
    public int ContextTokens { get; set; } = 8192;

    /// <summary>Tempo máximo de uma resposta; depois disso o app segue sem IA.</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>Quanto esperar o Ollama responder à checagem de disponibilidade (a primeira conexão pode demorar).</summary>
    public int ProbeTimeoutMs { get; set; } = 4000;
}

/// <summary>
/// Decide o modo efetivo e entrega o cliente do Ollama. A checagem de disponibilidade nunca trava o app, mas também
/// não derruba a IA por um engasgo: espera um tempo razoável, repete quando só demorou, mantém a IA ligada se o Ollama
/// estava bom e demorou a responder, e só lembra de uma falha por poucos segundos (para notar que o Ollama abriu).
/// </summary>
public sealed class AiRuntime(AiOptions options, HttpClient http, TimeProvider clock) : IDisposable
{
    private static readonly TimeSpan PositiveFor = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan NegativeFor = TimeSpan.FromSeconds(5);

    /// <summary>Se o Ollama estava bom e só demorou a responder à checagem, a IA continua ligada por até este tempo.</summary>
    private static readonly TimeSpan GraceFor = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _lock = new(1, 1);
    private AiStatus? _cached;
    private DateTimeOffset _checkedAt;
    private TimeSpan _validFor;
    private AiStatus? _lastGood;
    private DateTimeOffset _lastGoodAt;
    private bool _pinned;
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
            _validFor = status.Mode == AiMode.Ollama ? PositiveFor : NegativeFor;
            return status;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Esquece o status (ex.: o Ollama caiu no meio de uma resposta); a próxima chamada checa de novo.</summary>
    public void Invalidate()
    {
        if (!_pinned) _cached = null;
    }

    /// <summary>
    /// Checa agora e, se o Ollama estiver pronto, fixa o status: não rechecar mais. Usado nos evals, onde uma checagem
    /// lenta no meio da rodada derrubaria a IA e estragaria as medidas.
    /// </summary>
    public async Task<AiStatus> PinAsync(CancellationToken cancellationToken = default)
    {
        _pinned = false;
        _cached = null;
        var status = await StatusAsync(cancellationToken);
        _pinned = status.Mode == AiMode.Ollama;
        return status;
    }

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
        return _cached is not null && (_pinned || clock.GetUtcNow() - _checkedAt < _validFor);
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

        var (installed, failure) = await FetchTagsAsync(cancellationToken);
        if (failure is not null)
        {
            // O Ollama estava bom e só demorou a responder (disco, antivírus, GPU ocupada): não derruba a IA por isso.
            if (failure.Transient && _lastGood is not null && clock.GetUtcNow() - _lastGoodAt < GraceFor) return _lastGood;
            return new AiStatus(AiMode.None, configured, false, null, endpoint, [], failure.Describe(endpoint));
        }

        var hasModel = installed!.Any(m => m == model || m == $"{model}:latest");
        if (!hasModel)
        {
            return new AiStatus(AiMode.None, configured, true, null, endpoint, installed!,
                $"O Ollama está rodando, mas o modelo {model} não está instalado (rode: ollama pull {model}).");
        }

        var ready = new AiStatus(AiMode.Ollama, configured, true, model, endpoint, installed!, $"IA local com {model} no Ollama: nada sai da sua máquina.");
        _lastGood = ready;
        _lastGoodAt = clock.GetUtcNow();
        return ready;
    }

    /// <summary>Lista os modelos do Ollama. Só repete quando a resposta demorou (conexão recusada não melhora com retentativa).</summary>
    private async Task<(IReadOnlyList<string>? Installed, ProbeFailure? Failure)> FetchTagsAsync(CancellationToken cancellationToken)
    {
        var uri = new Uri(new Uri(options.Ollama.Endpoint), "/api/tags");
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(options.Ollama.ProbeTimeoutMs, 100, 120_000));
        ProbeFailure? failure = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                var tags = await http.GetFromJsonAsync<OllamaTags>(uri, limit.Token);
                return (tags?.Models?.Select(m => m.Name).Order(StringComparer.Ordinal).ToList() ?? [], null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                failure = new ProbeFailure(true, $"esperei {timeout.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
            }
            catch (HttpRequestException ex)
            {
                return (null, new ProbeFailure(false, ex.Message));
            }
            catch (Exception ex) when (ex is JsonException or UriFormatException)
            {
                return (null, new ProbeFailure(false, ex.Message));
            }
        }

        return (null, failure);
    }

    private sealed record ProbeFailure(bool Transient, string Message)
    {
        public string Describe(string endpoint) => Transient
            ? $"O Ollama em {endpoint} não respondeu a tempo ({Message}): o app segue sem IA (busca guiada e respostas curadas) e tenta de novo em instantes."
            : $"Não consegui conectar ao Ollama em {endpoint} ({Message}): o app segue sem IA (busca guiada e respostas curadas). Abra o Ollama para ligar a IA local.";
    }

    private sealed record OllamaTags([property: JsonPropertyName("models")] IReadOnlyList<OllamaModel>? Models);

    private sealed record OllamaModel([property: JsonPropertyName("name")] string Name);
}
