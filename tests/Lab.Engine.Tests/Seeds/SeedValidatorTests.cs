using Lab.Engine.Tests.Support;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Pipeline.Seeds;

namespace Lab.Engine.Tests.Seeds;

/// <summary>O validador da fábrica de desafios: a referência de cada semente tira ≥ 85 nos cantos do espaço de variação.</summary>
public sealed class SeedValidatorTests(ITestOutputHelper output)
{
    /// <summary>
    /// Pleno nos dois cantos + a pegadinha de orçamento, com orçamento de eventos menor (janelas comprimidas) para o teste
    /// ser rápido. A validação completa (todos os provedores, dificuldades e pegadinhas) roda no <c>lab challenge validate</c>.
    /// </summary>
    [Fact]
    public void Pix_seed_reference_passes_at_the_corners_on_aws()
    {
        var content = TestContent.Get();
        var seed = content.Seeds.Single(s => s.Id == "pix/envio-pix");

        var report = SeedValidator.Validate(seed, content.Catalog, content.RuleTexts, new SeedValidationOptions
        {
            Providers = [CloudProvider.Aws],
            Difficulties = [Difficulty.Pleno],
            EventBudget = 4_000_000,
        }, TestContext.Current.CancellationToken);

        foreach (var variation in report.Variations)
        {
            output.WriteLine($"{variation.Name}: {variation.ReferenceScore} ({variation.Rank}) custo {variation.ReferenceCostUsd:N0}/{variation.BudgetUsd:N0} em {variation.WallTimeMs:N0} ms");
            foreach (var failed in variation.FailedChecks) output.WriteLine($"   ✗ {failed}");
        }

        foreach (var issue in report.Issues) output.WriteLine($"{issue.Severity} [{issue.Variation}] {issue.Message}");
        report.Ok.ShouldBeTrue();
        report.Variations.Count.ShouldBe(3); // mínimo, máximo e máximo + orçamento apertado
        report.Variations.ShouldAllBe(v => v.ReferenceScore >= 85);
    }

    [Fact]
    public void Pix_seed_reference_passes_on_every_provider()
    {
        var content = TestContent.Get();
        var seed = content.Seeds.Single(s => s.Id == "pix/envio-pix");

        var report = SeedValidator.Validate(seed, content.Catalog, content.RuleTexts, new SeedValidationOptions
        {
            Providers = [CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp],
            Difficulties = [Difficulty.Senior],
            Corners = false,
            Twists = false,
            EventBudget = 4_000_000,
        }, TestContext.Current.CancellationToken);

        foreach (var variation in report.Variations)
            output.WriteLine($"{variation.Name}: {variation.ReferenceScore} ({variation.Rank}) custo {variation.ReferenceCostUsd:N0}/{variation.BudgetUsd:N0}");
        foreach (var issue in report.Issues) output.WriteLine($"{issue.Severity} [{issue.Variation}] {issue.Message}");
        report.Ok.ShouldBeTrue();
        report.Variations.Select(v => v.Provider).ShouldBe([CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp]);
        report.Variations.ShouldAllBe(v => v.ReferenceScore >= 85);
    }
}
