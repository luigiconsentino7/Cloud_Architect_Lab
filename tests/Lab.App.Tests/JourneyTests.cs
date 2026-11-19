using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Challenges;

namespace Lab.App.Tests;

/// <summary>A jornada principal de ponta a ponta pela API: desafio → desenho → submissão → nota → XP.</summary>
public sealed class JourneyTests : IAsyncLifetime
{
    private readonly LabFactory _factory = new();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<(string ChallengeId, string AttemptId, JsonObject Challenge)> Start(HttpClient api, string seedId, string difficulty, double baselineRps)
    {
        var challenge = await api.PostJson("/api/challenges", new { seedId, provider = "aws", difficulty, variables = new Dictionary<string, double> { ["baselineRps"] = baselineRps } });
        var challengeId = challenge["id"]!.GetValue<string>();
        var attempt = await api.PostJson($"/api/challenges/{challengeId}/attempts", new { });
        return (challengeId, attempt["id"]!.GetValue<string>(), challenge);
    }

    private static async Task Draw(HttpClient api, string attemptId, string seedId, Difficulty difficulty, double baselineRps)
    {
        var (scene, flows) = Api.ReferenceDesign(seedId, CloudProvider.Aws, difficulty, new Dictionary<string, double> { ["baselineRps"] = baselineRps });
        var draft = new HttpRequestMessage(HttpMethod.Put, $"/api/attempts/{attemptId}/draft") { Content = JsonContent.Create(new { scene }, options: Api.Json) };
        draft.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await (await api.SendAsync(draft)).ReadObject())["draftVersion"]!.GetValue<int>().ShouldBe(1);
        (await api.PutAsJsonAsync($"/api/attempts/{attemptId}/flows", new { flows }, Api.Json)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Nova_tentativa_comeca_com_o_desenho_e_os_fluxos_da_anterior_mas_sem_as_dicas()
    {
        var api = _factory.Api();
        var (challengeId, attemptId, _) = await Start(api, "pix/envio-pix", "pleno", 300);
        await Draw(api, attemptId, "pix/envio-pix", Difficulty.Pleno, 300);
        (await api.PostAsync($"/api/attempts/{attemptId}/hints", null)).EnsureSuccessStatusCode();
        var first = await (await api.GetAsync($"/api/attempts/{attemptId}")).ReadObject();

        var second = await api.PostJson($"/api/challenges/{challengeId}/attempts", new { });

        second["number"]!.GetValue<int>().ShouldBe(2);
        second["status"]!.GetValue<string>().ShouldBe("draft");
        second["scene"]!["elements"]!.AsArray().Count.ShouldBeGreaterThan(0);
        second["scene"]!.ToJsonString().ShouldBe(first["scene"]!.ToJsonString()); // corrige em vez de redesenhar (spec 01)
        second["flows"]!.ToJsonString().ShouldBe(first["flows"]!.ToJsonString());
        first["hintLevelsUsed"]!.AsArray().Count.ShouldBe(1);
        second["hintLevelsUsed"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Submission_is_evaluated_scored_and_rewarded()
    {
        var api = _factory.Api();
        var (challengeId, attemptId, challenge) = await Start(api, "pix/envio-pix", "pleno", 300);

        challenge["scenarios"]!.AsArray().ShouldAllBe(s => s!["visibility"]!.GetValue<string>() == "public");
        challenge["hiddenScenarios"]!.GetValue<int>().ShouldBe(3);
        challenge.ToJsonString().ShouldNotContain("reference");

        await Draw(api, attemptId, "pix/envio-pix", Difficulty.Pleno, 300);

        // Autosave com versão velha: conflito.
        var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/attempts/{attemptId}/draft") { Content = JsonContent.Create(new { scene = new { elements = Array.Empty<object>() } }) };
        stale.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await api.SendAsync(stale)).StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);

        var validation = await api.PostJson($"/api/attempts/{attemptId}/validate", new { });
        validation["diagnostics"]!.AsArray().Where(d => d!["severity"]!.GetValue<string>() == "error").ShouldBeEmpty();
        validation["functional"]!.AsArray().ShouldAllBe(f => f!["status"]!.GetValue<string>() == "passed");

        (await api.PostAsync($"/api/attempts/{attemptId}/submit", null)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var first = await Submit(api, attemptId, "chave-1");
        var again = await Submit(api, attemptId, "chave-1");
        again.ShouldBe(first);
        var other = new HttpRequestMessage(HttpMethod.Post, $"/api/attempts/{attemptId}/submit");
        other.Headers.Add("Idempotency-Key", "chave-2");
        (await api.SendAsync(other)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var evaluation = await api.WaitForEvaluation(first);
        evaluation["status"]!.GetValue<string>().ShouldBe("completed", evaluation["error"]?.ToString());
        evaluation["finalScore"]!.GetValue<int>().ShouldBeGreaterThanOrEqualTo(85);
        evaluation["result"]!["review"].ShouldNotBeNull();

        var progress = await Api.Eventually(
            async () => await (await api.GetAsync("/api/me/progress")).ReadObject(),
            p => p["summary"]!["totalXp"]!.GetValue<int>() > 0);
        progress["summary"]!["totalXp"]!.GetValue<int>().ShouldBe(evaluation["result"]!["xp"]!["attemptXp"]!.GetValue<int>());

        var achievements = JsonNode.Parse(await api.GetStringAsync("/api/me/achievements"))!.AsArray();
        achievements.Single(a => a!["code"]!.GetValue<string>() == "primeiro-deploy")!["unlocked"]!.GetValue<bool>().ShouldBeTrue();
        JsonNode.Parse(await api.GetStringAsync("/api/me/competencies"))!.AsArray().Count.ShouldBeGreaterThan(0);

        var reference = await (await api.GetAsync($"/api/evaluations/{first}/reference")).ReadObject();
        reference["graph"]!["nodes"]!.AsArray().Count.ShouldBeGreaterThan(5);
        reference["scene"]!["elements"]!.AsArray().Count.ShouldBeGreaterThan(5);

        (await api.GetAsync($"/api/evaluations/{first}/scenarios/baseline/series")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var export = await (await api.GetAsync($"/api/attempts/{attemptId}/export")).ReadObject();
        export["ir"]!["nodes"].ShouldNotBeNull();
        JsonNode.Parse(await api.GetStringAsync("/api/evaluations"))!.AsArray().ShouldContain(h => h!["evaluationId"]!.GetValue<string>() == first);
        JsonNode.Parse(await api.GetStringAsync($"/api/challenges/{challengeId}/attempts"))!.AsArray().Single()!["status"]!.GetValue<string>().ShouldBe("evaluated");
    }

    [Fact]
    public async Task Draft_simulation_runs_only_public_scenarios_and_gives_no_xp()
    {
        var api = _factory.Api();
        var (_, attemptId, _) = await Start(api, "pix/consulta-dict", "junior", 200);
        await Draw(api, attemptId, "pix/consulta-dict", Difficulty.Junior, 200);

        var ticket = await (await api.PostAsync($"/api/attempts/{attemptId}/simulate", null)).ReadObject();
        var evaluation = await api.WaitForEvaluation(ticket["evaluationId"]!.GetValue<string>());

        evaluation["status"]!.GetValue<string>().ShouldBe("completed", evaluation["error"]?.ToString());
        evaluation["kind"]!.GetValue<string>().ShouldBe("draft");
        evaluation["result"]!["simulation"]!["scenarios"]!.AsArray().Select(s => s!["scenarioId"]!.GetValue<string>()).ShouldBe(["baseline", "black-friday"]);
        (await api.GetAsync($"/api/evaluations/{ticket["evaluationId"]}/reference")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        await Task.Delay(1500); // dá tempo ao outbox: rascunho não credita XP
        (await (await api.GetAsync("/api/me/progress")).ReadObject())["summary"]!["totalXp"]!.GetValue<int>().ShouldBe(0);
    }

    [Fact]
    public async Task Hints_are_revealed_in_order_and_run_out()
    {
        var api = _factory.Api();
        var (_, attemptId, _) = await Start(api, "pix/consulta-dict", "junior", 200);

        var levels = new List<int>();
        for (var i = 0; i < 3; i++) levels.Add((await api.PostJson($"/api/attempts/{attemptId}/hints", new { }))["level"]!.GetValue<int>());

        levels.ShouldBe([1, 2, 3]);
        (await api.PostAsync($"/api/attempts/{attemptId}/hints", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    private static async Task<string> Submit(HttpClient api, string attemptId, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/attempts/{attemptId}/submit");
        request.Headers.Add("Idempotency-Key", key);
        var response = await api.SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        return (await response.ReadObject())["evaluationId"]!.GetValue<string>();
    }
}
