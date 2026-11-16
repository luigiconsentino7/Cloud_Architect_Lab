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
        AiEvalCommand.Models(Args.Parse(["eval"])).ShouldBe(["qwen2.5:3b"]);
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

        report.For(PromptRoles.Interviewer).Where(c => c.Flags.ContainsKey("no-spoiler")).ShouldAllBe(c => !c.Passed);
        report.For(PromptRoles.Mapper).Count(c => c.Passed).ShouldBe(2); // só as duas anotações ("nenhum")
        report.MeetsS4.ShouldBeTrue(); // sem personalizador no relatório, o S4 olha só a velocidade
    }

    /// <summary>Responde como um modelo bom responderia, lendo o que o prompt manda.</summary>
    private sealed partial class RightModel : IChatClient
    {
        public bool LeakReference { get; init; }
        public bool AlwaysNone { get; init; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            var system = list[0].Text;
            var user = list.Last(m => m.Role == ChatRole.User).Text;
            var schema = (options?.ResponseFormat as ChatResponseFormatJson)?.Schema;
            string text;
            if (system.StartsWith("Você monta desafios", StringComparison.Ordinal))
            {
                var seeds = schema!.Value.GetProperty("properties").GetProperty("seedId").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).ToList();
                text = JsonSerializer.Serialize(new { seedId = seeds[0], narrative = "Um banco digital quer crescer.", variables = Array.Empty<object>(), twists = Array.Empty<string>(), reason = "casa com o contexto" });
            }
            else if (system.StartsWith("Você ajuda a tipar", StringComparison.Ordinal))
            {
                var label = user.Split('\n')[1];
                var id = AlwaysNone ? "nenhum"
                    : label.Contains("eventos") ? "aws.eventbridge" : label.Contains("PDF") ? "aws.s3" : label.Contains("regras") ? "aws.lambda" : "nenhum";
                text = JsonSerializer.Serialize(new { catalogId = id, reason = "pelo rótulo" });
            }
            else if (system.StartsWith("Você é um mentor", StringComparison.Ordinal))
            {
                var review = JsonNode.Parse(user[(user.IndexOf("<revisao>", StringComparison.Ordinal) + 9)..user.IndexOf("</revisao>", StringComparison.Ordinal)])!;
                var score = Regex.Match(user, @"Nota final: (\d+)").Groups[1].Value;
                var top = review["melhorias"]?[0]?["titulo"]?.GetValue<string>() ?? "";
                text = JsonSerializer.Serialize(new { paragraphs = new[] { $"Nota {score}: o ponto que mais pesou foi {Regex.Replace(top, @"\d", "")}." } });
            }
            else if (user.Contains("Quantas", StringComparison.Ordinal) || user.Contains("volume", StringComparison.Ordinal))
            {
                text = $"No horário normal são {Load().Match(system).Groups[1].Value} req/s.";
            }
            else if (user.Contains("solução", StringComparison.Ordinal) || user.Contains("ocultos", StringComparison.Ordinal))
            {
                text = LeakReference ? $"Os cenários ocultos são {string.Join(", ", HiddenNames())} e a referência usa cache-chaves." : "Não tenho essa informação: pense no que o enunciado pede.";
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
