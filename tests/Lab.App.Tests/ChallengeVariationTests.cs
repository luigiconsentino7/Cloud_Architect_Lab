using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Lab.App.Tests;

/// <summary>"Outro" e "Ajustar": novas variações da mesma semente, validadas pelo instanciador.</summary>
public sealed class ChallengeVariationTests : IAsyncLifetime
{
    private readonly LabFactory _factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Outro_mantem_semente_nivel_nuvem_e_pegadinhas()
    {
        var api = _factory.Api();
        var original = await api.PostJson("/api/challenges", new { seedId = "pix/envio-pix", provider = "azure", difficulty = "senior", twists = new[] { "spi-degradado" } });

        var other = await api.PostJson($"/api/challenges/{original["id"]}/variations", new { });

        other["id"]!.GetValue<string>().ShouldNotBe(original["id"]!.GetValue<string>());
        other["seedId"]!.GetValue<string>().ShouldBe("pix/envio-pix");
        other["provider"]!.GetValue<string>().ShouldBe("azure");
        other["difficulty"]!.GetValue<string>().ShouldBe("senior");
        var options = await (await api.GetAsync($"/api/challenges/{other["id"]}/options")).ReadObject();
        options["currentTwists"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["spi-degradado"]);
    }

    [Fact]
    public async Task Opcoes_trazem_as_faixas_e_as_pegadinhas_da_semente()
    {
        var api = _factory.Api();
        var challenge = await api.PostJson("/api/challenges", new { seedId = "pix/envio-pix", provider = "aws", difficulty = "senior" });

        var options = await (await api.GetAsync($"/api/challenges/{challenge["id"]}/options")).ReadObject();

        options["difficulties"]!.AsArray().Select(d => d!.GetValue<string>()).ShouldBe(["pleno", "senior", "staff"]);
        options["variables"]!["baselineRps"]!["ranges"]!["pleno"]!.AsArray().Select(v => v!.GetValue<double>()).ShouldBe([300, 600]);
        options["twists"]!.AsArray().ShouldContain(t => t!["id"]!.GetValue<string>() == "spi-degradado" && t["minDifficulty"]!.GetValue<string>() == "senior");
        options["currentVariables"]!["baselineRps"]!.GetValue<double>().ShouldBeInRange(700, 1400);
        options["aiAvailable"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task Ajustar_muda_nivel_numeros_e_pegadinhas_dentro_das_faixas()
    {
        var api = _factory.Api();
        var original = await api.PostJson("/api/challenges", new { seedId = "pix/envio-pix", provider = "aws", difficulty = "senior", twists = new[] { "spi-degradado" } });
        var url = $"/api/challenges/{original["id"]}/variations";

        var easier = await api.PostJson(url, new { difficulty = "pleno", provider = "gcp" });
        easier["difficulty"]!.GetValue<string>().ShouldBe("pleno");
        easier["provider"]!.GetValue<string>().ShouldBe("gcp");
        easier["variables"]!["baselineRps"]!.GetValue<double>().ShouldBeInRange(300, 600);
        var easierOptions = await (await api.GetAsync($"/api/challenges/{easier["id"]}/options")).ReadObject();
        easierOptions["currentTwists"]!.AsArray().ShouldBeEmpty(); // a pegadinha exige sênior: cai sozinha

        var adjusted = await api.PostJson(url, new { variables = new Dictionary<string, double> { ["baselineRps"] = 1400 }, twists = new[] { "orcamento-apertado" } });
        adjusted["variables"]!["baselineRps"]!.GetValue<double>().ShouldBe(1400);
        var adjustedOptions = await (await api.GetAsync($"/api/challenges/{adjusted["id"]}/options")).ReadObject();
        adjustedOptions["currentTwists"]!.AsArray().Select(t => t!.GetValue<string>()).ShouldBe(["orcamento-apertado"]);

        var outOfRange = await api.PostAsJsonAsync(url, new { variables = new Dictionary<string, double> { ["baselineRps"] = 99_999 } }, Api.Json);
        outOfRange.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await outOfRange.Content.ReadAsStringAsync()).ShouldContain("fora da faixa");

        var tooHard = await api.PostAsJsonAsync(url, new { difficulty = "pleno", twists = new[] { "spi-degradado" } }, Api.Json);
        tooHard.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var withoutAi = await api.PostAsJsonAsync(url, new { instruction = "mais volume e com auditoria" }, Api.Json);
        withoutAi.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await api.PostAsJsonAsync($"/api/challenges/{Guid.NewGuid()}/variations", new { }, Api.Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
