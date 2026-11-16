using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lab.Ir.Scene;

namespace Lab.App.Tests;

/// <summary>Formas desenhadas à mão: sugestões do catálogo pelo rótulo e, sem IA, anotações sem sugestão.</summary>
public sealed class LabelMappingTests : IAsyncLifetime
{
    private readonly LabFactory _factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    /// <summary>Desafio com um rascunho que tem um cliente tipado e duas formas sem tipo.</summary>
    public static async Task<string> DraftWithShapes(HttpClient api)
    {
        var challenge = await api.PostJson("/api/challenges", new { seedId = "pix/consulta-dict", provider = "aws", difficulty = "junior" });
        var attempt = await api.PostJson($"/api/challenges/{challenge["id"]}/attempts", new { });
        var scene = new SceneWriter();
        scene.Component("client.mobile", "App", 0, 0, id: "app");
        scene.Shape("Redis", 300, 0);
        scene.Shape("Motor de regras antifraude", 600, 0);
        var draft = new HttpRequestMessage(HttpMethod.Put, $"/api/attempts/{attempt["id"]}/draft") { Content = JsonContent.Create(new { scene = scene.Build() }, options: Api.Json) };
        draft.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await api.SendAsync(draft)).EnsureSuccessStatusCode();
        return attempt["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task Sem_ia_o_rotulo_que_casa_com_alias_ganha_sugestao_do_catalogo()
    {
        var api = _factory.Api();
        var attemptId = await DraftWithShapes(api);

        var response = await api.PostAsync($"/api/attempts/{attemptId}/label-mappings", null);
        var mappings = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

        var redis = mappings.Single(m => m!["label"]!.GetValue<string>() == "Redis")!;
        redis["kind"]!.GetValue<string>().ShouldBe("sem-tipo");
        redis["options"]![0]!["catalogId"]!.GetValue<string>().ShouldBe("aws.elasticache");
        redis["options"]![0]!["source"]!.GetValue<string>().ShouldBe("catalogo");
        var note = mappings.Single(m => m!["label"]!.GetValue<string>() == "Motor de regras antifraude")!;
        note["kind"]!.GetValue<string>().ShouldBe("anotacao");
        note["options"]!.AsArray().ShouldBeEmpty();
        (await api.PostAsync($"/api/attempts/{Guid.NewGuid()}/label-mappings", null)).StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
    }
}
