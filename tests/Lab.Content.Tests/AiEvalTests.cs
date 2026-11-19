using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lab.Ai;
using Lab.Cli;
using Lab.Content;
using Microsoft.Extensions.AI;

namespace Lab.Content.Tests;

/// <summary>O runner dos evals (spike S4) com um modelo de mentira que acerta tudo: valida a pontuação de cada papel.</summary>
public sealed partial class AiEvalTests
{
    private static readonly ContentBundle Content = ContentRepository.LoadDiscovered();
    private static readonly PromptLibrary Prompts = PromptLibrary.Load(Path.Combine(Content.Root, "prompts"));

    private static AiRuntime Runtime(IChatClient client) =>
        new(new AiOptions { Mode = "ollama", Ollama = new OllamaOptions { Endpoint = "http://127.0.0.1:9", Model = "modelo-certo" } }, new HttpClient(), TimeProvider.System)
        {
            ClientFactory = () => client,
        };

    [Fact]
    public async Task Modelo_que_acerta_passa_em_todos_os_casos_e_no_criterio_do_s4()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);

        var report = await runner.RunAsync(Runtime(new RightModel()), [PromptRoles.Personalizer, PromptRoles.Interviewer, PromptRoles.Mapper], 3, TestContext.Current.CancellationToken);

        report.Error.ShouldBeNull();
        report.For(PromptRoles.Personalizer).ShouldAllBe(c => c.Passed, string.Join("; ", report.For(PromptRoles.Personalizer).Select(c => c.Detail)));
        report.For(PromptRoles.Interviewer).ShouldAllBe(c => c.Passed, string.Join("; ", report.For(PromptRoles.Interviewer).Select(c => c.Detail)));
        report.For(PromptRoles.Mapper).ShouldAllBe(c => c.Passed, string.Join("; ", report.For(PromptRoles.Mapper).Select(c => c.Detail)));
        report.Speed.ShouldNotBeNull();
        report.MeetsS4.ShouldBeTrue();

        var markdown = AiEvalReport.Markdown([report], "http://127.0.0.1:9", new DateTime(2026, 11, 16, 9, 0, 0));
        markdown.ShouldContain("| modelo-certo | 3/3 (100%)");
        markdown.ShouldContain("Sugestão de modelo padrão: **modelo-certo**");
    }

    [Theory]
    [InlineData("eval --models qwen2.5:3b,gemma3:4b")]
    [InlineData("eval --models qwen2.5:3b gemma3:4b")] // PowerShell: a,b sem aspas vira dois argumentos
    [InlineData("eval --models qwen2.5:3b --models gemma3:4b")]
    public void Aceita_os_modelos_do_jeito_que_cada_terminal_manda(string line)
    {
        AiEvalCommand.Models(Args.Parse(line.Split(' '))).ShouldBe(["qwen2.5:3b", "gemma3:4b"]);
    }

    [Fact]
    public void Sem_modelos_usa_o_padrao_do_app()
    {
        AiEvalCommand.Models(Args.Parse(["eval"])).ShouldBe([new OllamaOptions().Model]);
        new OllamaOptions().Model.ShouldBe("gemma3:4b"); // o mais preciso no S4; o qwen2.5:3b fica como opção rápida
    }

    [Fact]
    public async Task Falha_do_ollama_vira_erro_e_nao_reprova_o_modelo()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);

        var report = await runner.RunAsync(Runtime(new DeadModel()), [PromptRoles.Interviewer, PromptRoles.Mapper], 2, TestContext.Current.CancellationToken);

        report.Cases.Count.ShouldBe(4);
        report.Cases.ShouldAllBe(c => c.Error && !c.Passed, string.Join("; ", report.Cases.Select(c => c.Detail)));
        report.Cases[0].Detail.ShouldContain("erro ao falar com o Ollama");
        report.Cases[0].Detail.ShouldContain("conexão recusada");
        report.PassRate(PromptRoles.Interviewer).ShouldBe(0);
        report.Speed.ShouldBeNull();
        report.SpeedError.ShouldNotBeNull().ShouldContain("HttpRequestException");
        report.Complete.ShouldBeFalse();
        report.MeetsS4.ShouldBeFalse();

        var markdown = AiEvalReport.Markdown([report], "http://127.0.0.1:9", new DateTime(2026, 11, 16, 9, 0, 0));
        markdown.ShouldContain("só erros");
        markdown.ShouldContain("| ? |");
        markdown.ShouldContain("Dados incompletos (?)");
        markdown.ShouldContain("Velocidade não medida");
        markdown.ShouldNotContain("Sugestão de modelo padrão");
    }

    [Fact]
    public async Task Erro_de_conexao_fica_fora_da_porcentagem_do_que_foi_respondido()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);

        // chamada 1 = aquecimento; 2 = "eventos" (ok); 3 = "PDF" (o Ollama cai); 4 = "regras" (ok)
        var report = await runner.RunAsync(Runtime(new RightModel { FailOnCall = 3 }), [PromptRoles.Mapper], 3, TestContext.Current.CancellationToken);

        report.Errors.ShouldBe(1);
        report.For(PromptRoles.Mapper).Where(c => !c.Error).ShouldAllBe(c => c.Passed);
        report.PassRate(PromptRoles.Mapper).ShouldBe(1.0); // 2 respondidos, 2 certos: o erro não conta contra o modelo
        var markdown = AiEvalReport.Markdown([report], "http://127.0.0.1:9", DateTime.Now);
        markdown.ShouldContain("2/2 (100%)");
        markdown.ShouldContain("1 com erro");
    }

    [Fact]
    public async Task Narrativa_rejeitada_explica_o_motivo_em_vez_de_virar_erro()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null) { NarrationEventBudget = 1_000_000 };

        var report = await runner.RunAsync(Runtime(new RightModel { InventNumbers = true }), [PromptRoles.Narrator], 1, TestContext.Current.CancellationToken);

        var narration = report.For(PromptRoles.Narrator).Single();
        narration.Error.ShouldBeFalse();
        narration.Passed.ShouldBeFalse();
        narration.Detail.ShouldContain("duas tentativas");
        narration.Detail.ShouldContain("cita números que não estão na revisão");
        narration.Detail.ShouldContain("98.765");
    }

    [Fact]
    public async Task Entrevistador_que_nega_que_os_cenarios_ocultos_existem_e_reprovado_mesmo_sem_vazar()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);

        var report = await runner.RunAsync(Runtime(new RightModel { DenyHiddenScenarios = true }), [PromptRoles.Interviewer], null, TestContext.Current.CancellationToken);

        var hidden = report.For(PromptRoles.Interviewer).Single(c => c.Case.Contains("ocultos", StringComparison.Ordinal));
        hidden.Passed.ShouldBeFalse();
        hidden.Detail.ShouldContain("os cenários ocultos existem");
        report.For(PromptRoles.Interviewer).Where(c => c != hidden).ShouldAllBe(c => c.Passed, "só a negação reprova");
    }

    [Fact]
    public async Task Entrevistador_que_trata_o_pedido_da_referencia_como_dado_nao_definido_e_reprovado()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);

        var report = await runner.RunAsync(Runtime(new RightModel { CannedAssumption = true }), [PromptRoles.Interviewer], null, TestContext.Current.CancellationToken);

        var reference = report.For(PromptRoles.Interviewer).Single(c => c.Case.Contains("solução de referência", StringComparison.Ordinal));
        reference.Passed.ShouldBeFalse();
        reference.Detail.ShouldContain("a solução de referência existe e não é entregue");
        report.For(PromptRoles.Interviewer).Where(c => c != reference).ShouldAllBe(c => c.Passed, "só o pedido da referência reprova");
    }

    [Fact]
    public async Task Relatorio_traz_o_que_a_ia_escreveu_e_a_narracao_que_so_passou_na_segunda_tentativa()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null) { NarrationEventBudget = 1_000_000 };

        var report = await runner.RunAsync(Runtime(new RightModel { FlakyNarrator = true }),
            [PromptRoles.Personalizer, PromptRoles.Interviewer, PromptRoles.Narrator], 1, TestContext.Current.CancellationToken);

        report.For(PromptRoles.Personalizer).Single().Sample.ShouldNotBeNull().ShouldContain("Um banco digital quer crescer.");
        report.For(PromptRoles.Interviewer).Single().Sample.ShouldNotBeNull();
        var narration = report.For(PromptRoles.Narrator).Single();
        narration.Passed.ShouldBeTrue(narration.Detail);
        narration.Detail.ShouldContain("só passou na 2ª tentativa");
        narration.Sample.ShouldNotBeNull().ShouldStartWith("Nota ");

        var markdown = AiEvalReport.Markdown([report], "http://127.0.0.1:9", DateTime.Now);
        markdown.ShouldContain("### O que a IA escreveu");
        markdown.ShouldContain("**Comentário do mentor (narrador)**");
        markdown.ShouldContain("> Um banco digital quer crescer.");
    }

    private static ModelEvalReport Synthetic(string model, double quality, double seconds, double tokensPerSecond)
    {
        var cases = new List<EvalCaseResult>();
        foreach (var role in new[] { PromptRoles.Personalizer, PromptRoles.Interviewer })
        {
            for (var i = 0; i < 10; i++)
                cases.Add(new EvalCaseResult(role, $"caso {i}", i < quality * 10, seconds, "d", new Dictionary<string, bool>()));
        }

        return new ModelEvalReport(model, cases, new SpeedProbe(0.4, tokensPerSecond, false), null);
    }

    [Fact]
    public void Recomendacao_mostra_a_troca_quando_o_mais_preciso_nao_e_o_mais_rapido()
    {
        var slowAccurate = Synthetic("gemma", quality: 1.0, seconds: 8, tokensPerSecond: 18);
        var fastLoose = Synthetic("qwen", quality: 0.9, seconds: 2, tokensPerSecond: 70);

        var markdown = AiEvalReport.Markdown([fastLoose, slowAccurate], "x", DateTime.Now);

        markdown.ShouldContain("Mais preciso: **gemma** (100% de acerto médio nos papéis e 8,0 s por chamada)");
        markdown.ShouldContain("Mais rápido: **qwen** (90% de acerto médio nos papéis e 2,0 s por chamada; 4,0× mais rápido)");
        markdown.ShouldNotContain("Sugestão de modelo padrão");
    }

    [Fact]
    public void Recomendacao_e_direta_quando_o_mesmo_modelo_ganha_nos_dois()
    {
        var best = Synthetic("qwen", quality: 1.0, seconds: 2, tokensPerSecond: 70);
        var worse = Synthetic("gemma", quality: 0.9, seconds: 8, tokensPerSecond: 18);

        var markdown = AiEvalReport.Markdown([worse, best], "x", DateTime.Now);

        markdown.ShouldContain("Sugestão de modelo padrão: **qwen**");
        markdown.ShouldContain("o mais preciso e o mais rápido");
    }

    [Fact]
    public void Relatorio_diz_com_que_prompts_e_datasets_foi_gerado()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);

        var limited = runner.Fingerprint([PromptRoles.Interviewer, PromptRoles.Mapper], 3);
        var everything = runner.Fingerprint([PromptRoles.Interviewer], null);

        limited.ShouldMatch(@"^entrevistador/v\d+ \(3 casos\) · mapeador/v\d+ \(3 casos\)$"); // --limit conta só o que roda
        everything.ShouldMatch(@"^entrevistador/v\d+ \(\d+ casos\)$");
        var markdown = AiEvalReport.Markdown([Synthetic("modelo", 1.0, 1, 50)], "x", DateTime.Now, limited);
        markdown.ShouldContain($"Prompts e casos desta rodada: {limited}"); // rodada com cópia desatualizada do repositório se entrega aqui
        AiEvalReport.Markdown([Synthetic("modelo", 1.0, 1, 50)], "x", DateTime.Now).ShouldNotContain("Prompts e casos");
    }

    [Fact]
    public async Task Narrador_comenta_a_revisao_da_referencia_degradada()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null) { NarrationEventBudget = 1_000_000 };

        var report = await runner.RunAsync(Runtime(new RightModel()), [PromptRoles.Narrator], 1, TestContext.Current.CancellationToken);

        var narration = report.For(PromptRoles.Narrator).Single();
        narration.Passed.ShouldBeTrue(narration.Detail);
        narration.Flags["cita-o-principal"].ShouldBeTrue(narration.Detail);
        narration.Case.ShouldContain("(nota "); // a referência degradada perde pontos: há o que comentar
    }

    [Fact]
    public async Task Modelo_que_erra_reprova_e_entrevistador_que_vaza_e_pego()
    {
        var runner = new AiEvalRunner(Content, Prompts, TextWriter.Null);
        var leaky = new RightModel { LeakReference = true, AlwaysNone = true };

        var report = await runner.RunAsync(Runtime(leaky), [PromptRoles.Interviewer, PromptRoles.Mapper], null, TestContext.Current.CancellationToken);

        report.Errors.ShouldBe(0); // resposta errada é reprovação, não erro de conexão
        report.For(PromptRoles.Interviewer).Where(c => c.Flags.ContainsKey("no-spoiler")).ShouldAllBe(c => !c.Passed);
        report.For(PromptRoles.Mapper).Count(c => c.Passed).ShouldBe(4); // só as quatro anotações ("nenhum")
        report.MeetsS4.ShouldBeTrue(); // sem personalizador no relatório, o S4 olha só a velocidade
    }

    /// <summary>Ollama que caiu: toda chamada falha.</summary>
    private sealed class DeadModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("conexão recusada");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("conexão recusada");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Responde como um modelo bom responderia, lendo o que o prompt manda.</summary>
    private sealed partial class RightModel : IChatClient
    {
        private int _calls;

        public bool LeakReference { get; init; }
        public bool AlwaysNone { get; init; }

        /// <summary>A chamada de número N (1 é o aquecimento do modelo) falha como se o Ollama tivesse caído.</summary>
        public int FailOnCall { get; init; }

        public bool InventNumbers { get; init; }

        /// <summary>Diz que não há cenários ocultos (mentira: eles existem) sem vazar nenhum nome.</summary>
        public bool DenyHiddenScenarios { get; init; }

        /// <summary>Responde ao pedido da referência com a frase de "dado não definido" (o que o qwen2.5:3b fez no S4).</summary>
        public bool CannedAssumption { get; init; }

        /// <summary>Na primeira tentativa de cada narração cita um número que a revisão não tem; na segunda acerta.</summary>
        public bool FlakyNarrator { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (++_calls == FailOnCall) throw new HttpRequestException("conexão recusada");
            var list = messages.ToList();
            var system = list[0].Text;
            var user = list.Last(m => m.Role == ChatRole.User).Text;
            var schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema;
            string text;
            if (system.StartsWith("Você monta desafios", StringComparison.Ordinal))
            {
                var seeds = schema!.Value.GetProperty("anyOf").EnumerateArray().Select(b => b.GetProperty("properties").GetProperty("seedId").GetProperty("const").GetString()!).ToList();
                text = JsonSerializer.Serialize(new { seedId = seeds[0], narrative = "Um banco digital quer crescer.", variables = Array.Empty<object>(), twists = Array.Empty<string>(), reason = "casa com o contexto" });
            }
            else if (system.StartsWith("Você ajuda a tipar", StringComparison.Ordinal))
            {
                var label = user.Split('\n')[1];
                var id = AlwaysNone ? "nenhum"
                    : label.Contains("eventos") ? "aws.eventbridge" : label.Contains("PDF") ? "aws.s3" : label.Contains("regras") ? "aws.lambda" : "nenhum";
                text = JsonSerializer.Serialize(new { funcao = "pelo rótulo", componente = id != "nenhum", catalogId = id });
            }
            else if (system.StartsWith("Você é um mentor", StringComparison.Ordinal))
            {
                // Na retentativa o último "user" é a correção; a revisão está na primeira mensagem.
                var request = list.First(m => m.Role == ChatRole.User).Text;
                var review = JsonNode.Parse(request[(request.IndexOf("<revisao>", StringComparison.Ordinal) + 9)..request.IndexOf("</revisao>", StringComparison.Ordinal)])!;
                var score = Regex.Match(request, @"Nota final: (\d+)").Groups[1].Value;
                var top = review["melhorias"]?[0]?["titulo"]?.GetValue<string>() ?? "";
                var firstTry = FlakyNarrator && list.Count(m => m.Role == ChatRole.User) == 1;
                text = InventNumbers || firstTry
                    ? JsonSerializer.Serialize(new { paragraphs = new[] { $"Nota {score}, mas o p99 chegou a 98.765 ms." } })
                    : JsonSerializer.Serialize(new { paragraphs = new[] { $"Nota {score}: o ponto que mais pesou foi {Regex.Replace(top, @"\d", "")}." } });
            }
            else if (user.Contains("por segundo", StringComparison.Ordinal) || user.Contains("volume", StringComparison.Ordinal))
            {
                text = $"No horário normal são {Load().Match(system).Groups[1].Value} req/s.";
            }
            else if (user.Contains("solução", StringComparison.Ordinal) || user.Contains("ocultos", StringComparison.Ordinal))
            {
                text = LeakReference ? $"Os cenários ocultos são {string.Join(", ", HiddenNames())} e a referência usa cache-chaves."
                    : DenyHiddenScenarios && user.Contains("ocultos", StringComparison.Ordinal) ? "Não há cenários de teste ocultos nesta avaliação."
                    : CannedAssumption && user.Contains("solução", StringComparison.Ordinal) ? "Não está definido. Pode assumir, mas deixe a premissa explícita no desenho."
                    : "Não tenho essa informação: pense no que o enunciado pede.";
            }
            else
            {
                text = "Isso não está definido no enunciado: pode assumir, deixando a premissa explícita.";
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var text = (await GetResponseAsync(messages, options, cancellationToken)).Text;
            foreach (var word in text.Split(' ')) yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ");
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private static IEnumerable<string> HiddenNames() => ["Queda de uma zona", "DICT lento", "SPI fora do ar"];

        [GeneratedRegex(@"Carga: ([\d.,]+) req/s")]
        private static partial Regex Load();
    }
}
