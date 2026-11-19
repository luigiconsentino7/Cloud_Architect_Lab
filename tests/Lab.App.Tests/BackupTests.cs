using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Challenges;

namespace Lab.App.Tests;

/// <summary>Backup completo: exporta de um app com histórico e importa num app vazio, sem perder nada.</summary>
public sealed class BackupTests
{
    [Fact]
    public async Task Exportar_e_importar_devolve_avaliacoes_xp_e_conversas()
    {
        JsonNode backup;
        string evaluationId;
        int score;
        int totalXp;
        string conversationId;
        await using (var source = new LabFactory())
        {
            var api = source.Api();
            var challenge = await api.PostJson("/api/challenges", new { seedId = "pix/consulta-dict", provider = "aws", difficulty = "junior", variables = new Dictionary<string, double> { ["baselineRps"] = 200 } });
            var attempt = await api.PostJson($"/api/challenges/{challenge["id"]}/attempts", new { });
            var attemptId = attempt["id"]!.GetValue<string>();
            var (scene, flows) = Api.ReferenceDesign("pix/consulta-dict", CloudProvider.Aws, Difficulty.Junior, new Dictionary<string, double> { ["baselineRps"] = 200 });
            var draft = new HttpRequestMessage(HttpMethod.Put, $"/api/attempts/{attemptId}/draft") { Content = JsonContent.Create(new { scene }, options: Api.Json) };
            draft.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
            (await api.SendAsync(draft)).EnsureSuccessStatusCode();
            (await api.PutAsJsonAsync($"/api/attempts/{attemptId}/flows", new { flows }, Api.Json)).EnsureSuccessStatusCode();
            var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/attempts/{attemptId}/submit");
            submit.Headers.Add("Idempotency-Key", "backup-1");
            evaluationId = (await (await api.SendAsync(submit)).ReadObject())["evaluationId"]!.GetValue<string>();
            var evaluation = await api.WaitForEvaluation(evaluationId);
            score = evaluation["finalScore"]!.GetValue<int>();
            totalXp = (await Api.Eventually(async () => await (await api.GetAsync("/api/me/progress")).ReadObject(),
                p => p["summary"]!["totalXp"]!.GetValue<int>() > 0))["summary"]!["totalXp"]!.GetValue<int>();
            var chat = await api.PostJson("/api/conversations", new { kind = "contexto" });
            conversationId = chat["id"]!.GetValue<string>();
            await api.PostJson($"/api/conversations/{conversationId}/messages", new { text = "Quero estudar ledger", provider = "aws" });

            backup = JsonNode.Parse(await (await api.PostAsync("/api/backup/export", null)).Content.ReadAsStringAsync())!;
        }

        backup["format"]!.GetValue<string>().ShouldBe("cloud-architect-lab-backup");
        backup["schema"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        backup["tables"]!["scenarioResults"]!.AsArray().Count.ShouldBeGreaterThan(0);

        await using var target = new LabFactory();
        var fresh = target.Api();
        (await (await fresh.GetAsync("/api/me/progress")).ReadObject())["summary"]!["totalXp"]!.GetValue<int>().ShouldBe(0);

        var result = await (await fresh.PostAsJsonAsync("/api/backup/import", backup, Api.Json)).ReadObject();

        result["evaluations"]!.GetValue<int>().ShouldBe(1);
        Directory.GetFiles(Path.Combine(target.DataDirectory, "backups"), "*antes-da-importacao.db").ShouldNotBeEmpty();
        var restored = await (await fresh.GetAsync($"/api/evaluations/{evaluationId}")).ReadObject();
        restored["finalScore"]!.GetValue<int>().ShouldBe(score);
        restored["result"]!["review"].ShouldNotBeNull();
        (await fresh.GetAsync($"/api/evaluations/{evaluationId}/scenarios/baseline/series")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await fresh.GetAsync("/api/me/progress")).ReadObject())["summary"]!["totalXp"]!.GetValue<int>().ShouldBe(totalXp);
        (await (await fresh.GetAsync($"/api/conversations/{conversationId}")).ReadObject())["messages"]!.AsArray().Count.ShouldBe(2);
        JsonNode.Parse(await fresh.GetStringAsync("/api/me/achievements"))!.AsArray()
            .Single(a => a!["code"]!.GetValue<string>() == "primeiro-deploy")!["unlocked"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Arquivo_que_nao_e_backup_ou_de_versao_futura_e_recusado()
    {
        await using var factory = new LabFactory();
        var api = factory.Api();
        var current = JsonNode.Parse(await (await api.PostAsync("/api/backup/export", null)).Content.ReadAsStringAsync())!.AsObject();

        var wrong = current.DeepClone().AsObject();
        wrong["format"] = "outra-coisa";
        var future = current.DeepClone().AsObject();
        future["version"] = 99;

        (await api.PostAsJsonAsync("/api/backup/import", wrong, Api.Json)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var response = await api.PostAsJsonAsync("/api/backup/import", future, Api.Json);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Atualize o app");
        (await (await api.GetAsync("/api/me")).ReadObject())["id"].ShouldNotBeNull(); // nada foi apagado
    }
}
