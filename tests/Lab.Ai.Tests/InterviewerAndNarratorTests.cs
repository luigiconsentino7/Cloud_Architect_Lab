using Microsoft.Extensions.AI;

namespace Lab.Ai.Tests;

public sealed class InterviewerAndNarratorTests
{
    private static readonly InterviewContext Context = new(
        "Envio de Pix",
        "Enunciado: o app envia Pix. Carga: 2.000 req/s.",
        [
            new CuratedAnswer("Existe limite de valor por transação?", ["limite", "valor maximo"], "Sim: R$ 5.000 por transação no período noturno."),
            new CuratedAnswer("O DICT pode ficar fora do ar?", ["dict", "indisponibilidade"], "Pode: trate a indisponibilidade com timeout e retentativa."),
        ]);

    [Fact]
    public void Sem_ia_casa_a_pergunta_com_o_esclarecimento_curado()
    {
        var answer = CuratedInterviewer.Answer(Context, "Tem algum limite de valor nas transações?");

        answer.Source.ShouldBe(AnswerSource.Curated);
        answer.MatchedQuestion.ShouldBe("Existe limite de valor por transação?");
    }

    [Fact]
    public void Sem_ia_admite_que_nao_sabe_e_lista_as_perguntas_curadas()
    {
        var answer = CuratedInterviewer.Answer(Context, "Qual a cor do botão?");

        answer.MatchedQuestion.ShouldBeNull();
        answer.Text.ShouldContain("Não tenho uma resposta pronta");
        answer.Text.ShouldContain("O DICT pode ficar fora do ar?");
    }

    [Fact]
    public async Task Com_ia_transmite_a_resposta_e_so_envia_a_parte_publica()
    {
        var client = new FakeChatClient((_, _) => "Pode assumir, mas deixe a premissa explícita no desenho.");
        var interviewer = new Interviewer(TestAi.Runtime(client), TestAi.Prompts(), TestAi.Log<Interviewer>());
        var deltas = new List<string>();

        var answer = await interviewer.AnswerAsync(new InterviewRequest(Context, [new ChatTurn("user", "oi"), new ChatTurn("assistant", "olá")], "Posso usar fila?"),
            d => { deltas.Add(d); return ValueTask.CompletedTask; }, TestContext.Current.CancellationToken);

        answer.Source.ShouldBe(AnswerSource.Ai);
        answer.PromptVersion.ShouldBe("entrevistador/v1");
        string.Concat(deltas).ShouldBe(answer.Text);
        deltas.Count.ShouldBeGreaterThan(1);
        var messages = client.Calls[0].Messages;
        messages[0].Role.ShouldBe(ChatRole.System);
        messages[0].Text.ShouldContain("<desafio>");
        messages[0].Text.ShouldContain("O DICT pode ficar fora do ar?");
        messages.Select(m => m.Role).ShouldBe([ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.User]);
    }

    [Fact]
    public async Task Se_o_modelo_cai_responde_com_o_conteudo_curado()
    {
        var client = new FakeChatClient((_, _) => "") { Failure = new HttpRequestException("conexão recusada") };
        var interviewer = new Interviewer(TestAi.Runtime(client), TestAi.Prompts(), TestAi.Log<Interviewer>());

        var answer = await interviewer.AnswerAsync(new InterviewRequest(Context, [], "E se o DICT cair?"), null, TestContext.Current.CancellationToken);

        answer.Source.ShouldBe(AnswerSource.Curated);
        answer.Text.ShouldContain("A IA local não respondeu");
        answer.MatchedQuestion.ShouldBe("O DICT pode ficar fora do ar?");
    }

    [Fact]
    public async Task A_narrativa_que_inventa_numero_ganha_uma_chance_e_depois_entra()
    {
        var replies = new Queue<string>([
            TestAi.Json(new { paragraphs = new[] { "Nota 72, mas o p99 chegou a 2.500 ms no pico." } }),
            TestAi.Json(new { paragraphs = new[] { "Nota 72: o p99 de 1.277 ms no pico pesou mais.", "Bom uso de fila com DLQ." } }),
        ]);
        var client = new FakeChatClient((_, _) => replies.Dequeue());
        var narrator = new ReviewNarrator(TestAi.Runtime(client), TestAi.Prompts(), TestAi.Log<ReviewNarrator>());
        var request = new NarrationRequest("Envio de Pix", 72, "B", """{"melhorias":[{"evidencias":["p99 de 1.277 ms no cenário Pico"]}]}""");

        var narrative = await narrator.NarrateAsync(request, TestContext.Current.CancellationToken);

        narrative.ShouldNotBeNull();
        narrative.Paragraphs.Count.ShouldBe(2);
        client.Calls.Count.ShouldBe(2);
        client.Calls[1].Messages[^1].Text.ShouldContain("2.500");
    }

    [Fact]
    public async Task Falha_do_modelo_na_narrativa_sobe_para_o_job_tentar_de_novo()
    {
        var client = new FakeChatClient((_, _) => "") { Failure = new HttpRequestException("conexão recusada") };
        var narrator = new ReviewNarrator(TestAi.Runtime(client), TestAi.Prompts(), TestAi.Log<ReviewNarrator>());

        await Should.ThrowAsync<HttpRequestException>(() => narrator.NarrateAsync(new NarrationRequest("Envio de Pix", 72, "B", "{}"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_narrativa_que_insiste_no_erro_e_descartada()
    {
        var client = new FakeChatClient((_, _) => TestAi.Json(new { paragraphs = new[] { "Sua nota deveria ser 95." } }));
        var narrator = new ReviewNarrator(TestAi.Runtime(client), TestAi.Prompts(), TestAi.Log<ReviewNarrator>());

        var narrative = await narrator.NarrateAsync(new NarrationRequest("Envio de Pix", 72, "B", "{}"), TestContext.Current.CancellationToken);

        narrative.ShouldBeNull();
        client.Calls.Count.ShouldBe(2);
    }
}
