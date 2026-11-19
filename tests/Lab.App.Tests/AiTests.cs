using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Ai;
using Lab.Content;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Pipeline.Seeds;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Lab.App.Tests;

/// <summary>Modos de IA pela API: sem IA (determinístico) e com um modelo local falso no lugar do Ollama.</summary>
public sealed class AiTests
{
    [Fact]
    public async Task Sem_ia_o_contexto_busca_na_biblioteca_e_o_entrevistador_usa_o_curado()
    {
        await using var factory = new LabFactory();
        var api = factory.Api();

        var status = await (await api.GetAsync("/api/ai/status")).ReadObject();
        status["mode"]!.GetValue<string>().ShouldBe("none");

        var chat = await api.PostJson("/api/conversations", new { kind = "contexto" });
        (await api.PostAsJsonAsync($"/api/conversations/{chat["id"]}/messages", new { text = "quero estudar pix" }, Api.Json)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest); // sem provedor
        var reply = await api.PostJson($"/api/conversations/{chat["id"]}/messages", new { text = "Quero treinar Pix com idempotência", provider = "aws" });
        var assistant = reply["assistant"]!;
        assistant["source"]!.GetValue<string>().ShouldBe("biblioteca");
        assistant["payload"]!["kind"]!.GetValue<string>().ShouldBe("suggestions");
        assistant["payload"]!["suggestions"]!.AsArray().ShouldContain(s => s!["seedId"]!.GetValue<string>().StartsWith("pix/"));

        var challenge = await api.PostJson("/api/challenges", new { seedId = "pix/envio-pix", provider = "aws", difficulty = "pleno" });
        var interview = await api.PostJson("/api/conversations", new { kind = "entrevista", challengeId = challenge["id"]!.GetValue<string>() });
        var again = await api.PostJson("/api/conversations", new { kind = "entrevista", challengeId = challenge["id"]!.GetValue<string>() });
        again["id"]!.GetValue<string>().ShouldBe(interview["id"]!.GetValue<string>()); // uma entrevista por desafio

        var answer = (await api.PostJson($"/api/conversations/{interview["id"]}/messages", new { text = "O SPI aceita retry da mesma ordem?" }))["assistant"]!;
        answer["source"]!.GetValue<string>().ShouldBe("curado");
        answer["payload"]!["matchedQuestion"]!.GetValue<string>().ShouldBe("O SPI aceita repetir a mesma ordem?");

        var view = await (await api.GetAsync($"/api/conversations/{interview["id"]}")).ReadObject();
        view["messages"]!.AsArray().Select(m => m!["number"]!.GetValue<int>()).ShouldBe([1, 2]);
    }

    [Fact]
    public async Task Com_ia_local_o_contexto_vira_um_desafio_personalizado_e_validado()
    {
        var fake = new ScriptedModel();
        fake.Personalize = options => new
        {
            seedId = ScriptedModel.Enum(options, "seedId")[0],
            narrative = "A FinBank vai lançar Pix por aproximação no próximo trimestre.",
            variables = new[] { new { name = "baselineRps", value = 1_000_000.0 } },
            twists = new[] { "pegadinha-que-nao-existe" },
            clarificationsExtra = new[] { new { q = "O app é só para pessoa física?", a = "Sim, só pessoa física nesta fase." } },
            reason = "Você falou de Pix em banco digital.",
        };
        await using var factory = WithModel(fake);
        var api = factory.Api();

        (await (await api.GetAsync("/api/ai/status")).ReadObject())["mode"]!.GetValue<string>().ShouldBe("ollama");
        var chat = await api.PostJson("/api/conversations", new { kind = "contexto" });
        var reply = await api.PostJson($"/api/conversations/{chat["id"]}/messages",
            new { text = "Trabalho num banco digital e quero treinar envio de Pix", provider = "gcp", difficulty = "senior" });

        var assistant = reply["assistant"]!;
        assistant["source"]!.GetValue<string>().ShouldBe("ia");
        assistant["promptVersion"]!.GetValue<string>().ShouldBe("personalizador/v2");
        var payload = assistant["payload"]!;
        payload["kind"]!.GetValue<string>().ShouldBe("challenge");
        payload["adjustments"]!.AsArray().ShouldContain(a => a!.GetValue<string>().Contains("pegadinha-que-nao-existe"));

        var challenge = await (await api.GetAsync($"/api/challenges/{payload["challengeId"]}")).ReadObject();
        challenge["provider"]!.GetValue<string>().ShouldBe("gcp");
        challenge["narrative"]!.GetValue<string>().ShouldStartWith("A FinBank vai lançar Pix por aproximação");
        challenge["personalizedBy"]!.GetValue<string>().ShouldBe("modelo-teste");
        challenge["clarifications"]!.AsArray().ShouldContain(c => c!["question"]!.GetValue<string>() == "O app é só para pessoa física?");
        var seed = ContentRepository.LoadDiscovered().FindSeed(challenge["seedId"]!.GetValue<string>())!;
        if (seed.Variables.TryGetValue("baselineRps", out var rps))
            challenge["variables"]!["baselineRps"]!.GetValue<double>().ShouldBe(rps.Ranges[Difficulty.Senior][1]); // preso no máximo da faixa
    }

    [Fact]
    public async Task Com_ia_local_ajustar_por_instrucao_usa_o_contexto_original_e_valida_o_delta()
    {
        var fake = new ScriptedModel();
        var calls = 0;
        fake.Personalize = options =>
        {
            calls++;
            return new
            {
                seedId = ScriptedModel.Enum(options, "seedId")[0],
                narrative = calls == 1 ? "A FinBank vai lançar Pix por aproximação." : "A FinBank cresceu e agora precisa aguentar o dobro de clientes.",
                variables = calls == 1 ? Array.Empty<object>() : new object[] { new { name = "baselineRps", value = 50_000.0 } },
                twists = Array.Empty<string>(),
                reason = calls == 1 ? "Pix num banco digital." : "Você pediu mais volume.",
            };
        };
        await using var factory = WithModel(fake);
        var api = factory.Api();
        var chat = await api.PostJson("/api/conversations", new { kind = "contexto" });
        var created = (await api.PostJson($"/api/conversations/{chat["id"]}/messages",
            new { text = "Trabalho num banco digital e quero treinar envio de Pix", provider = "aws", difficulty = "senior" }))["assistant"]!["payload"]!;

        var options = await (await api.GetAsync($"/api/challenges/{created["challengeId"]}/options")).ReadObject();
        options["aiAvailable"]!.GetValue<bool>().ShouldBeTrue();
        var adjusted = await api.PostJson($"/api/challenges/{created["challengeId"]}/variations", new { instruction = "quero o volume no máximo do nível" });

        adjusted["seedId"]!.GetValue<string>().ShouldBe(created["seedId"]!.GetValue<string>());
        adjusted["narrative"]!.GetValue<string>().ShouldStartWith("A FinBank cresceu");
        adjusted["personalizationNote"]!.GetValue<string>().ShouldBe("Você pediu mais volume.");
        var seed = ContentRepository.LoadDiscovered().FindSeed(created["seedId"]!.GetValue<string>())!;
        if (seed.Variables.TryGetValue("baselineRps", out var rps))
            adjusted["variables"]!["baselineRps"]!.GetValue<double>().ShouldBe(rps.Ranges[Difficulty.Senior][1]);
        var adjustCall = fake.Calls.Last(c => c.Messages[0].Text.StartsWith("Você monta desafios", StringComparison.Ordinal));
        var user = adjustCall.Messages.Last(m => m.Role == ChatRole.User).Text;
        user.ShouldContain("Trabalho num banco digital"); // o contexto original continua valendo
        user.ShouldContain("Ajuste pedido: quero o volume no máximo do nível");
        ScriptedModel.Enum(adjustCall.Options, "seedId").ShouldBe([created["seedId"]!.GetValue<string>()]); // só a mesma semente

        var other = await api.PostJson($"/api/challenges/{adjusted["id"]}/variations", new { });
        other["narrative"]!.GetValue<string>().ShouldStartWith("A FinBank cresceu"); // o "Outro" mantém a personalização
        other["personalizedBy"]!.GetValue<string>().ShouldBe("modelo-teste");
    }

    [Fact]
    public async Task Com_ia_local_a_anotacao_ganha_sugestao_so_se_for_da_paleta()
    {
        var fake = new ScriptedModel
        {
            Map = (options, label) => new { funcao = "Regras rodam em funções.", componente = true, catalogId = label.Contains("antifraude") ? "aws.lambda" : "aws.servico-inventado" },
        };
        await using var factory = WithModel(fake);
        var api = factory.Api();
        var attemptId = await LabelMappingTests.DraftWithShapes(api);

        var mappings = JsonNode.Parse(await (await api.PostAsync($"/api/attempts/{attemptId}/label-mappings", null)).Content.ReadAsStringAsync())!.AsArray();

        var note = mappings.Single(m => m!["label"]!.GetValue<string>() == "Motor de regras antifraude")!;
        note["options"]![0]!["catalogId"]!.GetValue<string>().ShouldBe("aws.lambda");
        note["options"]![0]!["source"]!.GetValue<string>().ShouldBe("ia");
        note["options"]![0]!["reason"]!.GetValue<string>().ShouldBe("Regras rodam em funções.");
        var call = fake.Calls.Single(c => c.Messages[0].Text.StartsWith("Você ajuda a tipar", StringComparison.Ordinal));
        ScriptedModel.Enum(call.Options, "catalogId").ShouldContain("nenhum");
        ScriptedModel.Enum(call.Options, "catalogId").ShouldNotContain("client.mobile");
        call.Messages[1].Text.ShouldContain("<rotulo>\nMotor de regras antifraude\n</rotulo>");
        mappings.Single(m => m!["label"]!.GetValue<string>() == "Redis")!["options"]![0]!["source"]!.GetValue<string>().ShouldBe("catalogo"); // alias: sem IA
    }

    [Fact]
    public async Task Com_ia_local_o_entrevistador_transmite_e_nao_conhece_o_que_e_oculto()
    {
        var fake = new ScriptedModel { Interview = "Pode assumir, mas deixe a premissa explícita no desenho." };
        await using var factory = WithModel(fake);
        var api = factory.Api();
        var challenge = await api.PostJson("/api/challenges", new { seedId = "pix/consulta-dict", provider = "aws", difficulty = "junior" });
        var interview = await api.PostJson("/api/conversations", new { kind = "entrevista", challengeId = challenge["id"]!.GetValue<string>() });

        var answer = (await api.PostJson($"/api/conversations/{interview["id"]}/messages", new { text = "Posso usar cache para as chaves?" }))["assistant"]!;

        answer["source"]!.GetValue<string>().ShouldBe("ia");
        answer["text"]!.GetValue<string>().ShouldBe(fake.Interview);
        var system = fake.Calls.Single().Messages[0].Text;
        system.ShouldContain("<desafio>");
        var spec = Hidden("pix/consulta-dict", challenge);
        spec.HiddenNames.ShouldNotBeEmpty();
        foreach (var name in spec.HiddenNames) system.ShouldNotContain(name);
        foreach (var component in spec.ReferenceComponents) system.ShouldNotContain(component);
    }

    [Fact]
    public async Task Com_ia_local_a_submissao_ganha_narrativa_que_so_cita_a_revisao()
    {
        var fake = new ScriptedModel();
        var narrations = 0;
        fake.Narrate = user =>
        {
            narrations++;
            var score = user.Split('\n').First(l => l.StartsWith("Nota final:", StringComparison.Ordinal)).Split(' ')[2];
            return narrations == 1
                ? new { paragraphs = new[] { $"Nota {score}, mas o p99 chegou a 98.765 ms." } }
                : new { paragraphs = new[] { $"Nota {score}: o desenho segurou os cenários públicos.", "Revise os pontos de melhoria na ordem." } };
        };
        await using var factory = WithModel(fake);
        var api = factory.Api();
        var challenge = await api.PostJson("/api/challenges", new { seedId = "pix/consulta-dict", provider = "aws", difficulty = "junior", variables = new Dictionary<string, double> { ["baselineRps"] = 200 } });
        var attempt = await api.PostJson($"/api/challenges/{challenge["id"]}/attempts", new { });
        var attemptId = attempt["id"]!.GetValue<string>();
        var (scene, flows) = Api.ReferenceDesign("pix/consulta-dict", CloudProvider.Aws, Difficulty.Junior, new Dictionary<string, double> { ["baselineRps"] = 200 });
        var draft = new HttpRequestMessage(HttpMethod.Put, $"/api/attempts/{attemptId}/draft") { Content = JsonContent.Create(new { scene }, options: Api.Json) };
        draft.Headers.TryAddWithoutValidation("If-Match", "\"0\"");
        (await api.SendAsync(draft)).EnsureSuccessStatusCode();
        (await api.PutAsJsonAsync($"/api/attempts/{attemptId}/flows", new { flows }, Api.Json)).EnsureSuccessStatusCode();
        var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/attempts/{attemptId}/submit");
        submit.Headers.Add("Idempotency-Key", "narrativa-1");
        var evaluationId = (await (await api.SendAsync(submit)).ReadObject())["evaluationId"]!.GetValue<string>();

        var evaluation = await api.WaitForEvaluation(evaluationId);
        evaluation["status"]!.GetValue<string>().ShouldBe("completed", evaluation["error"]?.ToString());
        var withNarrative = await Api.Eventually(
            async () => await (await api.GetAsync($"/api/evaluations/{evaluationId}")).ReadObject(),
            e => e["narrative"]?["status"]?.GetValue<string>() is "ready" or "rejected" or "failed",
            TimeSpan.FromSeconds(60));

        var narrative = withNarrative["narrative"]!;
        narrative["status"]!.GetValue<string>().ShouldBe("ready", narrative["detail"]?.ToString());
        narrative["paragraphs"]!.AsArray()[0]!.GetValue<string>().ShouldStartWith($"Nota {evaluation["finalScore"]}:");
        narrative["promptVersion"]!.GetValue<string>().ShouldBe("narrador/v1");
        narrations.ShouldBe(2); // a primeira citava um número que a revisão não tem
        withNarrative["finalScore"]!.GetValue<int>().ShouldBe(evaluation["finalScore"]!.GetValue<int>()); // a IA não mexe na nota

        (await api.PostAsync($"/api/evaluations/{evaluationId}/narrative", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private static LabFactory WithModel(ScriptedModel model)
    {
        var factory = new LabFactory();
        var runtime = factory.Services.GetRequiredService<AiRuntime>();
        runtime.ClientFactory = () => model;
        runtime.Invalidate();
        return factory;
    }

    private static (IReadOnlyList<string> HiddenNames, IReadOnlyList<string> ReferenceComponents) Hidden(string seedId, JsonObject challenge)
    {
        var content = ContentRepository.LoadDiscovered();
        var variables = challenge["variables"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.GetValue<double>());
        var spec = SeedInstantiator.Instantiate(new InstantiationRequest
        {
            Seed = content.FindSeed(seedId)!,
            Provider = CloudProvider.Aws,
            Difficulty = Difficulty.Junior,
            Variables = variables,
        }, content.Catalog).Challenge;
        var hidden = spec.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Hidden).Select(s => s.Name).ToList();
        var components = spec.Reference?.Nodes
            .Where(n => !n.CatalogId.StartsWith("client.", StringComparison.Ordinal) && !n.CatalogId.StartsWith("external", StringComparison.Ordinal))
            .Select(n => n.Label).Where(l => l.Length > 6).ToList() ?? [];
        return (hidden, components);
    }

    /// <summary>Modelo de mentira que responde conforme o papel (pelo prompt de sistema).</summary>
    private sealed class ScriptedModel : IChatClient
    {
        public Func<ChatOptions?, object>? Personalize { get; set; }
        public Func<string, object>? Narrate { get; set; }
        public Func<ChatOptions?, string, object>? Map { get; set; }
        public string Interview { get; set; } = "Não está definido no enunciado.";
        public List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];

        /// <summary>Valores permitidos de uma propriedade do schema (a união dos ramos, no esquema por semente).</summary>
        public static IReadOnlyList<string> Enum(ChatOptions? options, string property)
        {
            var schema = ((ChatResponseFormatJson)options!.ResponseFormat!).Schema!.Value;
            static IEnumerable<string> Values(JsonElement node) => node.TryGetProperty("const", out var one)
                ? [one.GetString()!]
                : node.GetProperty("enum").EnumerateArray().Select(e => e.GetString()!);
            return schema.TryGetProperty("anyOf", out var branches)
                ? branches.EnumerateArray().SelectMany(b => Values(b.GetProperty("properties").GetProperty(property))).Distinct().ToList()
                : Values(schema.GetProperty("properties").GetProperty(property)).ToList();
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            lock (Calls) Calls.Add((list, options));
            var system = list[0].Text;
            object? reply = system.StartsWith("Você monta desafios", StringComparison.Ordinal) ? Personalize?.Invoke(options)
                : system.StartsWith("Você é um mentor", StringComparison.Ordinal) ? Narrate?.Invoke(list.First(m => m.Role == ChatRole.User).Text)
                : system.StartsWith("Você ajuda a tipar", StringComparison.Ordinal) ? Map?.Invoke(options, list.First(m => m.Role == ChatRole.User).Text)
                : null;
            var text = reply is null ? Interview : JsonSerializer.Serialize(reply);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = (await GetResponseAsync(messages, options, cancellationToken)).Text;
            for (var i = 0; i < text.Length; i += 5) yield return new ChatResponseUpdate(ChatRole.Assistant, text.Substring(i, Math.Min(5, text.Length - i)));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
