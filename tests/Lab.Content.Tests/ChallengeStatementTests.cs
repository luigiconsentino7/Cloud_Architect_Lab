using System.Globalization;
using Lab.Ai;
using Lab.Content;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Pipeline.Seeds;

namespace Lab.Content.Tests;

/// <summary>O enunciado que o entrevistador recebe: só a parte pública, em frases que o modelo pequeno consegue ler.</summary>
public sealed class ChallengeStatementTests
{
    private static readonly ContentBundle Content = ContentRepository.LoadDiscovered();

    [Fact]
    public void O_enunciado_de_toda_semente_nao_tem_item_pendurado_e_nao_vaza_a_referencia_nem_os_cenarios_ocultos()
    {
        var problems = new List<string>();
        foreach (var seed in Content.Seeds)
        {
            foreach (var difficulty in seed.Difficulties)
            {
                var spec = SeedInstantiator.Instantiate(new InstantiationRequest { Seed = seed, Provider = CloudProvider.Aws, Difficulty = difficulty }, Content.Catalog).Challenge;
                var statement = ChallengeStatement.Write(spec);
                var where = $"{seed.Id} {difficulty}";

                // Item de lista que acaba em ":" ou traz ": ." é descrição que faltou (o modelo lia isso como "não definido").
                foreach (var line in statement.Split('\n').Select(l => l.TrimEnd()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)))
                {
                    if (line.EndsWith(':') || line.Contains(": .", StringComparison.Ordinal) || line.Contains(":  ", StringComparison.Ordinal))
                        problems.Add($"{where}: item pendurado \"{line}\"");
                }

                var hidden = spec.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Hidden).ToList();
                if (hidden.Count > 0 && !statement.Contains($"Outros {hidden.Count} cenários ficam ocultos", StringComparison.Ordinal))
                    problems.Add($"{where}: o enunciado não diz quantos cenários ocultos existem");
                foreach (var scenario in hidden)
                {
                    if (statement.Contains(scenario.Name, StringComparison.OrdinalIgnoreCase)) problems.Add($"{where}: vaza o cenário oculto \"{scenario.Name}\"");
                }

                // O enunciado não lê a referência. Os rótulos dela repetem palavras do próprio enunciado ("aportes", "auditoria"),
                // então o que se confere são os serviços do catálogo que ela escolheu (ex.: "aws.dynamodb").
                foreach (var node in spec.Reference?.Nodes ?? [])
                {
                    if (statement.Contains(node.CatalogId, StringComparison.OrdinalIgnoreCase)) problems.Add($"{where}: vaza o serviço da referência \"{node.CatalogId}\"");
                }
            }
        }

        problems.ShouldBeEmpty(string.Join("\n", problems));
    }

    [Fact]
    public void O_enunciado_traz_o_provedor_a_regiao_e_a_carga_como_dados()
    {
        var seed = Content.FindSeed("capitais/cotacoes")!;
        var spec = SeedInstantiator.Instantiate(new InstantiationRequest { Seed = seed, Provider = CloudProvider.Aws, Difficulty = Difficulty.Senior }, Content.Catalog).Challenge;

        var statement = ChallengeStatement.Write(spec);

        statement.ShouldContain($"Provedor: {CloudProvider.Aws.DisplayName()}; região: {spec.Region};");
        statement.ShouldContain($"Carga: {spec.Workload.BaselineRps.ToString("#,0.##", CultureInfo.GetCultureInfo("pt-BR"))} req/s no horário normal");
    }
}
