using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Lab.App.Tests;

/// <summary>Proteções do app local (spec 09) e endpoints de conteúdo.</summary>
public sealed class SecurityAndContentTests : IAsyncLifetime
{
    private readonly LabFactory _factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Health_traz_a_versao_do_app_para_relatos_de_problema()
    {
        var health = System.Text.Json.Nodes.JsonNode.Parse(await _factory.CreateClient().GetStringAsync("/api/health", TestContext.Current.CancellationToken))!;

        health["version"]!.GetValue<string>().ShouldStartWith("0.1.0");
        health["catalogVersion"].ShouldNotBeNull(); // é por ele que uma segunda janela reconhece o Lab já aberto
    }

    [Theory]
    [InlineData("0.1.0-teste.12+5f9aeef1234567890abcdef", "0.1.0-teste.12 (5f9aeef)")]
    [InlineData("0.1.0+abc", "0.1.0 (abc)")]
    [InlineData("0.1.0+", "0.1.0")]
    [InlineData("0.1.0", "0.1.0")]
    [InlineData(null, "dev")]
    public void Versao_mostra_o_numero_e_o_commit_curto(string? informationalVersion, string expected)
    {
        Lab.Modules.Shared.AppVersion.Format(informationalVersion).ShouldBe(expected);
    }

    [Fact]
    public async Task Respostas_levam_csp_e_cabecalhos_de_seguranca()
    {
        var response = await _factory.CreateClient().GetAsync("/api/health");

        var csp = string.Join(' ', response.Headers.GetValues("Content-Security-Policy"));
        csp.ShouldContain("default-src 'self'");
        csp.ShouldContain("script-src 'self' 'wasm-unsafe-eval'");
        csp.ShouldNotContain("'unsafe-eval'");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldContain("connect-src 'self' ws://localhost:");
        response.Headers.GetValues("X-Content-Type-Options").ShouldBe(["nosniff"]);
        response.Headers.GetValues("X-Frame-Options").ShouldBe(["DENY"]);
        response.Headers.GetValues("Referrer-Policy").ShouldBe(["no-referrer"]);
    }

    [Fact]
    public async Task Writes_without_the_client_header_are_refused()
    {
        var plain = _factory.CreateClient();

        var response = await plain.PostAsJsonAsync("/api/challenges", new { seedId = "pix/envio-pix", provider = "aws" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).ShouldContain("X-Lab-Client");
    }

    [Fact]
    public async Task Writes_from_another_origin_are_refused()
    {
        var api = _factory.Api();
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/challenges") { Content = JsonContent.Create(new { seedId = "pix/envio-pix", provider = "aws" }) };
        request.Headers.Add("Origin", "https://site-malicioso.example");

        (await api.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Foreign_host_header_is_refused()
    {
        var api = _factory.Api();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Host = "evil.example";

        (await api.SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Reads_need_no_header()
    {
        var plain = _factory.CreateClient();

        (await plain.GetAsync("/api/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await plain.GetAsync("/api/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Library_search_finds_seeds_by_accentless_terms()
    {
        var api = _factory.Api();

        var results = JsonNode.Parse(await api.GetStringAsync("/api/library?q=cotacao"))!.AsArray();

        results.ShouldNotBeEmpty();
        results[0]!["seedId"]!.GetValue<string>().ShouldBe("capitais/cotacoes");
        JsonNode.Parse(await api.GetStringAsync("/api/library?track=ledger"))!.AsArray().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Catalog_palette_has_an_etag_and_honors_if_none_match()
    {
        var api = _factory.Api();

        var first = await api.GetAsync("/api/catalog/azure");
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var etag = first.Headers.ETag!.Tag;
        var palette = JsonNode.Parse(await first.Content.ReadAsStringAsync())!;
        palette["components"]!.AsArray().ShouldContain(c => c!["id"]!.GetValue<string>() == "azure.cosmosdb");
        palette["components"]!.AsArray().ShouldContain(c => c!["id"]!.GetValue<string>() == "client.mobile");

        var cached = new HttpRequestMessage(HttpMethod.Get, "/api/catalog/azure");
        cached.Headers.TryAddWithoutValidation("If-None-Match", etag);
        (await api.SendAsync(cached)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        (await api.GetAsync("/api/catalog/oracle")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Unknown_seed_gives_a_problem_details_error()
    {
        var api = _factory.Api();

        var response = await api.PostAsJsonAsync("/api/challenges", new { seedId = "nao/existe", provider = "aws" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("não existe");
    }
}
