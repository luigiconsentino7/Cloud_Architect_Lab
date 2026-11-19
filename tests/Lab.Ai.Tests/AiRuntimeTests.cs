using System.Net;
using System.Text;

namespace Lab.Ai.Tests;

/// <summary>A checagem de disponibilidade do Ollama: paciente, tolerante a engasgos e rápida em notar que ele abriu.</summary>
public sealed class AiRuntimeTests
{
    private const string Tags = """{"models":[{"name":"gemma3:4b"},{"name":"qwen2.5:3b"}]}""";

    private enum Behavior
    {
        Ok,
        Slow,
        Refused,
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Ollama de mentira: cada chamada segue o roteiro (a última se repete).</summary>
    private sealed class ScriptedOllama(params Behavior[] script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var behavior = script[Math.Min(Calls++, script.Length - 1)];
            switch (behavior)
            {
                case Behavior.Ok:
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Tags, Encoding.UTF8, "application/json") };
                case Behavior.Slow:
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                    throw new InvalidOperationException("inalcançável");
                default:
                    throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
            }
        }
    }

    private static AiRuntime Runtime(ScriptedOllama ollama, ManualClock clock, string model = "gemma3:4b") =>
        new(new AiOptions { Ollama = new OllamaOptions { Endpoint = "http://127.0.0.1:11434", Model = model, ProbeTimeoutMs = 150 } }, new HttpClient(ollama), clock);

    [Fact]
    public async Task Primeira_checagem_lenta_e_refeita_e_o_ollama_e_detectado()
    {
        var ollama = new ScriptedOllama(Behavior.Slow, Behavior.Ok);
        using var runtime = Runtime(ollama, new ManualClock());

        var status = await runtime.StatusAsync(TestContext.Current.CancellationToken);

        status.Mode.ShouldBe(AiMode.Ollama);
        status.InstalledModels.ShouldBe(["gemma3:4b", "qwen2.5:3b"]);
        ollama.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Conexao_recusada_nao_gasta_retentativa_e_diz_o_motivo()
    {
        var ollama = new ScriptedOllama(Behavior.Refused);
        using var runtime = Runtime(ollama, new ManualClock());

        var status = await runtime.StatusAsync(TestContext.Current.CancellationToken);

        status.Mode.ShouldBe(AiMode.None);
        status.Detail.ShouldContain("Não consegui conectar");
        status.Detail.ShouldContain("Abra o Ollama");
        ollama.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Duas_checagens_lentas_sem_nunca_ter_funcionado_explicam_que_demorou()
    {
        var ollama = new ScriptedOllama(Behavior.Slow);
        using var runtime = Runtime(ollama, new ManualClock());

        var status = await runtime.StatusAsync(TestContext.Current.CancellationToken);

        status.Mode.ShouldBe(AiMode.None);
        status.Detail.ShouldContain("não respondeu a tempo");
        ollama.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task Engasgo_depois_de_um_status_bom_nao_derruba_a_ia_mas_conexao_recusada_derruba()
    {
        var clock = new ManualClock();
        var ollama = new ScriptedOllama(Behavior.Ok, Behavior.Slow, Behavior.Slow, Behavior.Refused);
        using var runtime = Runtime(ollama, clock);
        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.Ollama);

        clock.Now += TimeSpan.FromSeconds(31); // o status bom venceu: a nova checagem engasga (as duas tentativas)
        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.Ollama);

        clock.Now += TimeSpan.FromSeconds(31); // agora o servidor recusa: o Ollama foi fechado de verdade
        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.None);
    }

    [Fact]
    public async Task Engasgo_que_dura_mais_que_a_tolerancia_derruba_a_ia()
    {
        var clock = new ManualClock();
        var ollama = new ScriptedOllama(Behavior.Ok, Behavior.Slow);
        using var runtime = Runtime(ollama, clock);
        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.Ollama);

        clock.Now += TimeSpan.FromMinutes(3);

        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.None);
    }

    [Fact]
    public async Task Falha_e_lembrada_so_por_alguns_segundos()
    {
        var clock = new ManualClock();
        var ollama = new ScriptedOllama(Behavior.Refused, Behavior.Ok);
        using var runtime = Runtime(ollama, clock);

        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.None);
        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.None);
        ollama.Calls.ShouldBe(1); // dentro dos 5 s não consulta de novo

        clock.Now += TimeSpan.FromSeconds(6); // o usuário abriu o Ollama
        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.Ollama);
    }

    [Fact]
    public async Task Status_fixado_nao_e_rechecado_nem_invalidado()
    {
        var clock = new ManualClock();
        var ollama = new ScriptedOllama(Behavior.Ok, Behavior.Refused);
        using var runtime = Runtime(ollama, clock);

        (await runtime.PinAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.Ollama);
        clock.Now += TimeSpan.FromMinutes(10);
        runtime.Invalidate();

        (await runtime.StatusAsync(TestContext.Current.CancellationToken)).Mode.ShouldBe(AiMode.Ollama);
        ollama.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Modelo_que_nao_esta_instalado_e_explicado()
    {
        using var runtime = Runtime(new ScriptedOllama(Behavior.Ok), new ManualClock(), model: "llama3:8b");

        var status = await runtime.StatusAsync(TestContext.Current.CancellationToken);

        status.Mode.ShouldBe(AiMode.None);
        status.OllamaReachable.ShouldBeTrue();
        status.Detail.ShouldContain("ollama pull llama3:8b");
    }

    [Fact]
    public void O_endereco_padrao_e_ipv4_para_nao_perder_tempo_no_windows()
    {
        new OllamaOptions().Endpoint.ShouldBe("http://127.0.0.1:11434");
        new OllamaOptions().ProbeTimeoutMs.ShouldBeGreaterThanOrEqualTo(3000);
    }
}
