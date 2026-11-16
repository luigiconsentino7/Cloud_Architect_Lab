using Lab.Engine.Tests.Support;
using Lab.Ir;
using Lab.Ir.Scene;
using Lab.Pipeline.Seeds;
using Lab.Rules;

namespace Lab.Engine.Tests.Seeds;

/// <summary>
/// Checagens rápidas (sem simular) de todo o conteúdo do repositório. A validação com a referência rodando no motor
/// fica no <c>lab challenge validate</c>, que o CI executa.
/// </summary>
public sealed class ContentLibraryTests
{
    public static TheoryData<string> SeedIds()
    {
        var data = new TheoryData<string>();
        foreach (var seed in TestContent.Get().Seeds) data.Add(seed.Id);
        return data;
    }

    [Fact]
    public void Library_has_at_least_twelve_seeds_covering_every_track()
    {
        var seeds = TestContent.Get().Seeds;

        seeds.Count.ShouldBeGreaterThanOrEqualTo(12);
        seeds.Select(s => s.Track).Distinct().Order().ShouldBe(["capitais", "cartoes", "investimentos", "ledger", "openfinance", "pix", "risco"]);
    }

    [Theory]
    [MemberData(nameof(SeedIds))]
    public void Seed_instantiates_cleanly_in_every_provider_and_difficulty(string seedId)
    {
        var content = TestContent.Get();
        var seed = content.FindSeed(seedId)!;

        foreach (var difficulty in seed.Difficulties)
        {
            foreach (var provider in new[] { CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp })
            {
                var instance = SeedInstantiator.Instantiate(new InstantiationRequest { Seed = seed, Provider = provider, Difficulty = difficulty }, content.Catalog);
                var where = $"{seedId} {provider} {difficulty}";

                instance.ReferenceDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
                    .Select(d => $"{where}: {d.Code} {d.Message}").ShouldBeEmpty();
                ChallengeChecks.Check(instance.Challenge, content.Catalog).Where(i => i.Severity == IssueSeverity.Error)
                    .Select(i => $"{where}: {i.Message}").ShouldBeEmpty();
                instance.Challenge.Hints.Count.ShouldBe(3, where);
            }
        }
    }

    [Fact]
    public void Every_rule_has_a_curated_text_and_every_text_a_rule()
    {
        var texts = TestContent.Get().RuleTexts;
        var ruleIds = RuleEngine.All.Select(r => r.Id).ToHashSet();

        RuleEngine.All.Where(r => texts.Find(r.Id) is null).Select(r => r.Id).ShouldBeEmpty();
        texts.All.Where(t => !ruleIds.Contains(t.Id)).Select(t => t.Id).ShouldBeEmpty();
        texts.All.ShouldAllBe(t => t.FollowUps.Count > 0 && t.Links.Count > 0 && t.Links.All(l => l.Url.StartsWith("https://")));
    }
}
