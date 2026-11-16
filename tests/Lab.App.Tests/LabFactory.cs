using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Content;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Pipeline.Seeds;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lab.App.Tests;

/// <summary>App inteiro em memória, com SQLite num arquivo temporário e janelas de simulação menores.</summary>
public sealed class LabFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "lab-tests-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Lab:DataDirectory", DataDirectory);
        builder.UseSetting("Lab:EventBudget", "3000000");
        builder.UseSetting("Lab:MaxParallelism", "2");
        builder.UseSetting("Lab:OpenBrowser", "false");

        // Ninguém escuta aqui: sem um cliente falso injetado, os testes rodam no modo sem IA.
        builder.UseSetting("Ai:Ollama:Endpoint", "http://127.0.0.1:9");
        builder.UseSetting("Ai:Ollama:Model", "modelo-teste");
    }

    /// <summary>Cliente como o frontend: com o header que as requisições de escrita exigem.</summary>
    public HttpClient Api()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Lab-Client", "tests");
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        try
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public static class Api
{
    public static readonly JsonSerializerOptions Json = LabJson.Options;

    private static readonly Lazy<ContentBundle> Content = new(() => ContentRepository.LoadDiscovered());

    public static async Task<JsonObject> ReadObject(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{(int)response.StatusCode}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    public static async Task<JsonObject> PostJson(this HttpClient client, string url, object body) =>
        await (await client.PostAsJsonAsync(url, body, Json)).ReadObject();

    /// <summary>A referência da semente, desenhada, com as mesmas variáveis do desafio — o "desenho do usuário" dos testes.</summary>
    public static (JsonObject Scene, JsonNode Flows) ReferenceDesign(string seedId, CloudProvider provider, Difficulty difficulty, IReadOnlyDictionary<string, double> variables)
    {
        var content = Content.Value;
        var instance = SeedInstantiator.Instantiate(new InstantiationRequest
        {
            Seed = content.FindSeed(seedId)!,
            Provider = provider,
            Difficulty = difficulty,
            Variables = variables,
        }, content.Catalog);
        return (instance.ReferenceScene!, JsonSerializer.SerializeToNode(instance.ReferenceFlows, Json)!);
    }

    public static async Task<JsonObject> WaitForEvaluation(this HttpClient client, string evaluationId, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromMinutes(3));
        while (true)
        {
            var evaluation = await (await client.GetAsync($"/api/evaluations/{evaluationId}")).ReadObject();
            var status = evaluation["status"]!.GetValue<string>();
            if (status is "completed" or "failed") return evaluation;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Avaliação {evaluationId} ainda em {status}: {evaluation["detail"]}");
            await Task.Delay(250);
        }
    }

    public static async Task<T> Eventually<T>(Func<Task<T>> probe, Func<T, bool> done, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (true)
        {
            var value = await probe();
            if (done(value) || DateTime.UtcNow > deadline) return value;
            await Task.Delay(200);
        }
    }
}
