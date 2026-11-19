using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lab.Ai.Tests;

/// <summary>Modelo de mentira: responde com o que o teste mandar e guarda o que recebeu.</summary>
public sealed class FakeChatClient(Func<IReadOnlyList<ChatMessage>, ChatOptions?, string> respond) : IChatClient
{
    public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    /// <summary>As N primeiras chamadas falham (ex.: o Ollama recusou o esquema); as seguintes funcionam.</summary>
    public int FailFirstCalls { get; set; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        lock (Calls) Calls.Add((list, options));
        if (Failure is not null) throw Failure;
        if (Calls.Count <= FailFirstCalls) throw new HttpRequestException("esquema recusado");
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, respond(list, options))));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = (await GetResponseAsync(messages, options, cancellationToken)).Text;
        for (var i = 0; i < text.Length; i += 7) yield return new ChatResponseUpdate(ChatRole.Assistant, text.Substring(i, Math.Min(7, text.Length - i)));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    /// <summary>Os valores permitidos de uma propriedade no JSON Schema da resposta (união dos ramos, quando há <c>anyOf</c>).</summary>
    public static IReadOnlyList<string> SchemaEnum(ChatOptions? options, string property)
    {
        var schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema ?? throw new InvalidOperationException("sem schema");
        if (schema.TryGetProperty("anyOf", out var branches))
            return branches.EnumerateArray().SelectMany(b => Values(b.GetProperty("properties").GetProperty(property))).Distinct().ToList();
        return Values(schema.GetProperty("properties").GetProperty(property)).ToList();
    }

    public static bool HasAnyOf(ChatOptions? options) =>
        (options?.ResponseFormat as ChatResponseFormatJson)?.Schema is { } schema && schema.TryGetProperty("anyOf", out _);

    private static IEnumerable<string> Values(JsonElement node)
    {
        if (node.TryGetProperty("const", out var one)) return [one.GetString()!];
        if (node.TryGetProperty("items", out var items)) node = items;
        if (node.TryGetProperty("properties", out var inner)) node = inner.GetProperty("name");
        return node.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!);
    }
}

public static class TestAi
{
    public static AiRuntime Runtime(FakeChatClient? client) =>
        new(new AiOptions { Ollama = new OllamaOptions { Endpoint = "http://127.0.0.1:9", Model = "modelo-teste" } }, new HttpClient(), TimeProvider.System)
        {
            ClientFactory = client is null ? null : () => client,
        };

    public static PromptLibrary Prompts() => PromptLibrary.Load(PromptsDirectory());

    public static string PromptsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "content", "prompts"))) directory = directory.Parent;
        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("content/prompts"), "content", "prompts");
    }

    public static NullLogger<T> Log<T>() => NullLogger<T>.Instance;

    public static string Json(object value) => JsonSerializer.Serialize(value);
}
