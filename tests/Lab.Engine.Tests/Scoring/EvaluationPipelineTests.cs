using System.Globalization;
using System.Text.RegularExpressions;
using Lab.Engine.Tests.Support;
using Lab.Ir.Challenges;
using Lab.Pipeline;
using Lab.Rules;
using Lab.Scoring;

namespace Lab.Engine.Tests.Scoring;

/// <summary>O pipeline inteiro nos dois desenhos do exemplo da spec: nota, travas, revisão e XP.</summary>
public sealed class EvaluationPipelineTests(ITestOutputHelper output)
{
    private static readonly ChallengeSpec Challenge = Pix.WithReference(Pix.Challenge());
    private static readonly Lazy<double?> ReferenceCostUsd = new(() => ReferenceCost.Estimate(Challenge, TestContent.Get().Catalog));
    private static readonly Lazy<EvaluationResult> GoodResult = new(() => Evaluate(Pix.Good()));
    private static readonly Lazy<EvaluationResult> BadResult = new(() => Evaluate(Pix.Bad()));

    private static EvaluationResult Evaluate(Design design, ChallengeSpec? challenge = null, XpContext? xp = null) =>
        EvaluationPipeline.Evaluate(new EvaluationRequest
        {
            Challenge = challenge ?? Challenge,
            Catalog = TestContent.Get().Catalog,
            RuleTexts = TestContent.Get().RuleTexts,
            Scene = design.Scene,
            Flows = design.Flows,
            IncludeSeries = false,
            ReferenceCostUsd = ReferenceCostUsd.Value,
            Xp = xp ?? new XpContext(),
        });

    private EvaluationResult Show(EvaluationResult result, string name)
    {
        var score = result.Score;
        output.WriteLine($"== {name}: {score.FinalScore} ({score.Rank}) base {score.BaseScore}" +
            (score.Cap is { } cap ? $" trava {cap.Value} aplicada={cap.Applied}: {cap.Reason}" : ""));
        foreach (var d in score.Dimensions)
        {
            output.WriteLine($"  {d.Dimension,-12} {d.Score,5} (base {d.BaseScore}, −{d.Penalty}) peso {d.Weight} → {d.Contribution} | {d.Basis}");
            foreach (var rule in d.Rules)
                output.WriteLine($"      {rule.RuleId} {rule.Severity} ×{rule.Findings} confirmados {rule.ConfirmedFindings} −{rule.Points} [{string.Join(",", rule.ConfirmedIn)}]");
        }

        foreach (var check in score.Checks)
            output.WriteLine($"  {(check.Passed ? "✓" : "✗")} {check.Id,-24} {check.Score,6:0.###} {check.Description}");
        output.WriteLine($"  custo {result.Cost.MonthlyUsd:N0}/mês (orçamento {result.Cost.BudgetUsd:N0}; referência {result.ReferenceCostUsd:N0})");
        foreach (var line in result.Cost.Lines) output.WriteLine($"      {line.Label,-28} {line.MonthlyUsd,10:N2}  {line.Basis}");
        output.WriteLine("  resumo:");
        foreach (var line in result.Review.Summary) output.WriteLine($"    - [{line.Impact}] {line.Text}");
        output.WriteLine("  melhorias:");
        foreach (var item in result.Review.Improvements)
        {
            output.WriteLine($"    - [{item.Impact}{(item.CausesCap ? ", trava" : "")}] {item.Id}: {item.Title}");
            foreach (var evidence in item.Evidence) output.WriteLine($"        · {evidence}");
        }

        output.WriteLine("  pontos fortes: " + string.Join(" | ", result.Review.Strengths.Select(s => s.Title)));
        output.WriteLine("  follow-ups: " + string.Join(" | ", result.Review.FollowUps));
        output.WriteLine($"  XP: {result.Xp.AttemptXp} (creditado {result.Xp.Credited}); tempos: {string.Join(", ", result.Timings.Select(t => $"{t.Stage} {t.Ms:N0} ms"))}");
        return result;
    }

    [Fact]
    public void Solid_design_scores_high()
    {
        var result = Show(GoodResult.Value, "bom");

        result.Score.FinalScore.ShouldBeGreaterThanOrEqualTo(80);
        result.Score.Rank.ShouldBeOneOf("S", "A");
        result.Score.Cap.ShouldBeNull();
        result.Score.Checks.Where(c => c.SloId != "HEADROOM").ShouldAllBe(c => c.Passed);
    }

    [Fact]
    public void Flawed_design_scores_in_the_c_range_without_a_cap()
    {
        var result = Show(BadResult.Value, "ruim");

        result.Score.FinalScore.ShouldBeInRange(50, 72);
        result.Score.Cap.ShouldBeNull(); // as duplicidades são de notificação, não de dinheiro
        result.Score.For(Dimension.Performance)!.Score.ShouldBeLessThan(60);
        result.Score.For(Dimension.Functional)!.Score.ShouldBeLessThan(100); // falta o caminho de falha do SPI
    }

    [Fact]
    public void Confirmed_rules_are_not_penalized_twice()
    {
        var result = BadResult.Value;

        var confirmed = result.Score.Findings.Where(f => f.Confirmed).ToList();
        confirmed.ShouldNotBeEmpty();
        confirmed.ShouldAllBe(f => f.Points == 0);
        result.Score.RulePenalties.ShouldAllBe(p => p.Points <= 2 * Scorer.SeverityPoints(p.Severity));
    }

    [Fact]
    public void Review_leads_with_the_concurrency_cliff_and_cites_the_simulation()
    {
        var review = BadResult.Value.Review;

        review.Summary.Count.ShouldBe(3);
        review.Improvements.ShouldContain(i => i.Evidence.Any(e => e.Contains("lei de Little")));
        review.Improvements.ShouldContain(i => i.NodeIds.Contains("sender"));
        review.Improvements.Select(i => i.Impact).ShouldBeInOrder(SortDirection.Descending);
        review.FollowUps.ShouldNotBeEmpty();
    }

    [Fact]
    public void Good_design_strengths_name_the_patterns_it_uses()
    {
        var strengths = GoodResult.Value.Review.Strengths.Select(s => s.Title).ToList();

        strengths.ShouldContain(s => s.StartsWith("Todos os 4 cenários aprovados"));
        strengths.ShouldContain("Outbox transacional / CDC");
        strengths.ShouldContain("Circuit breaker");
        strengths.ShouldContain("Consumidor idempotente");
    }

    [Fact]
    public void Use_case_without_a_flow_caps_the_score_at_50()
    {
        var design = Pix.Good() with { Flows = [Pix.Good().Flows[0]] };

        var result = Show(Evaluate(design), "sem fluxo do UC-2");

        result.Score.Cap.ShouldNotBeNull();
        result.Score.Cap!.Value.ShouldBe(50);
        result.Score.FinalScore.ShouldBeLessThanOrEqualTo(50);
        result.Review.Improvements[0].CausesCap.ShouldBeTrue();
    }

    [Fact]
    public void Same_input_same_result_hash()
    {
        var again = Evaluate(Pix.Bad());

        again.ResultHash.ShouldBe(BadResult.Value.ResultHash);
        again.Manifest.Hash().ShouldBe(BadResult.Value.Manifest.Hash());
    }

    [Fact]
    public void Result_does_not_depend_on_the_machine_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var english = Evaluate(Pix.Bad());

            english.ResultHash.ShouldBe(BadResult.Value.ResultHash);
            english.Review.Improvements.SelectMany(i => i.Evidence)
                .Where(e => e.Contains("lei de Little"))
                .ShouldAllBe(e => !Regex.IsMatch(e, @"\d,\d{3}")); // milhar com ponto, não com vírgula
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Retry_only_credits_the_improvement()
    {
        var first = BadResult.Value.Xp;
        var second = Evaluate(Pix.Good(), xp: new XpContext { FirstAttempt = false, PreviousBestXp = first.AttemptXp }).Xp;

        second.Credited.ShouldBe(second.AttemptXp - first.AttemptXp);
        second.Credited.ShouldBeGreaterThan(0);
    }
}
