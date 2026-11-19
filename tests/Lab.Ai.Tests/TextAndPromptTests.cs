namespace Lab.Ai.Tests;

public sealed class TextAndPromptTests
{
    [Theory]
    [InlineData("1.277", 1277)]
    [InlineData("99,95", 99.95)]
    [InlineData("3.000,5", 3000.5)]
    [InlineData("1277.5", 1277.5)]
    [InlineData("0.9995", 0.9995)]
    [InlineData("12.345.678", 12345678)]
    public void Le_numeros_em_pt_BR_e_em_JSON(string token, double expected)
    {
        AiText.TryParseNumber(token, out var value).ShouldBeTrue();
        value.ShouldBe(expected, 1e-9);
    }

    [Fact]
    public void Acha_numeros_citados_que_nao_estao_nos_permitidos()
    {
        var unknown = AiText.UnknownNumbers("A nota foi 72, com p99 de 1.277 ms em 3 cenários e custo de US$ 4.100", [72, 1277]);

        unknown.ShouldBe([4100]); // 3 é contagem pequena e passa
    }

    [Fact]
    public void Extrai_o_json_mesmo_com_cerca_de_markdown()
    {
        AiText.ParseObject("```json\n{\"a\": 1}\n```")!["a"]!.GetValue<int>().ShouldBe(1);
        AiText.ParseObject("sem json").ShouldBeNull();
    }

    [Fact]
    public void Carrega_a_versao_mais_nova_de_cada_papel_sem_o_front_matter()
    {
        var prompts = TestAi.Prompts();

        prompts.Get(PromptRoles.Personalizer).Id.ShouldBe("personalizador/v2");
        prompts.Get(PromptRoles.Interviewer).Text.ShouldStartWith("Você é o entrevistador");
        prompts.Get(PromptRoles.Narrator).Text.ShouldNotContain("changelog");
    }
}
