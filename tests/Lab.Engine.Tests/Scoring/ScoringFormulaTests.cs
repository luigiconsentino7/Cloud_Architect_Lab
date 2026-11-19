using Lab.Ir.Challenges;
using Lab.Scoring;

namespace Lab.Engine.Tests.Scoring;

/// <summary>As fórmulas da spec 05 (Etapa 7) e da spec 07 (XP), com os números dos exemplos da própria spec.</summary>
public sealed class ScoringFormulaTests
{
    [Theory]
    [InlineData(800, 1000, 1)]
    [InlineData(1000, 1000, 1)]
    [InlineData(1500, 1000, 0.5)]
    [InlineData(2000, 1000, 0)]
    [InlineData(5000, 1000, 0)]
    public void Max_slo_loses_everything_at_twice_the_limit(double observed, double limit, double expected) =>
        SloEvaluator.MaxScore(observed, limit).ShouldBe(expected, 1e-9);

    [Theory]
    [InlineData(0.9999, 1)]
    [InlineData(0.9995, 1)]
    [InlineData(0.995, 0.5)]   // 10× o orçamento de erro
    [InlineData(0.95, 0)]      // 100×
    [InlineData(0.30, 0)]
    public void Success_rate_score_is_logarithmic_in_the_error_budget(double observed, double expected) =>
        SloEvaluator.SuccessScore(observed, 0.9995).ShouldBe(expected, 1e-9);

    [Fact]
    public void Integrity_is_all_or_nothing_for_money()
    {
        SloEvaluator.IntegrityScore(0, 0, monetary: true, requests: 100_000).ShouldBe(1);
        SloEvaluator.IntegrityScore(1, 0, monetary: true, requests: 100_000).ShouldBe(0);
        SloEvaluator.IntegrityScore(37, 0, monetary: false, requests: 100_000).ShouldBe(0.63, 1e-9);
        SloEvaluator.IntegrityScore(500, 0, monetary: false, requests: 100_000).ShouldBe(0);
    }

    [Theory]
    [InlineData(7900, 12000, 7000d, 100)]   // dentro do orçamento e até 1,5× a referência
    [InlineData(14000, 20000, 7000d, 80)]   // 2× a referência: −40 × 0,5
    [InlineData(12000, 10000, null, 60)]   // 20% acima do orçamento
    [InlineData(15000, 10000, null, 0)]    // 50% acima zera
    public void Cost_score_follows_budget_then_reference(double cost, double budget, double? reference, double expected) =>
        Scorer.CostScore(cost, budget, reference).ShouldBe(expected, 1e-9);

    [Theory]
    [InlineData(95, "S")]
    [InlineData(90, "S")]
    [InlineData(89, "A")]
    [InlineData(70, "B")]
    [InlineData(60, "C")]
    [InlineData(59, "D")]
    public void Rank_thresholds(int score, string rank) => Scorer.RankFor(score).ShouldBe(rank);

    [Fact]
    public void Xp_matches_the_examples_in_the_spec()
    {
        // Sênior, nota 78, primeira tentativa, sem dicas → 289 XP.
        XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Senior, Score = 78, Rank = "B" }).AttemptXp.ShouldBe(289);

        // Exemplo do motor: 69 de primeira → 241; depois 88 (rank A) → 318, creditado 77.
        var first = XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Senior, Score = 69, Rank = "C" });
        first.AttemptXp.ShouldBe(241);
        var second = XpCalculator.Compute(new XpInput
        {
            Difficulty = Difficulty.Senior,
            Score = 88,
            Rank = "A",
            FirstAttempt = false,
            PreviousBestXp = first.AttemptXp,
        });
        second.AttemptXp.ShouldBe(318);
        second.Credited.ShouldBe(77);
    }

    [Fact]
    public void Hints_reduce_xp_but_never_below_thirty_percent()
    {
        var none = XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Pleno, Score = 80, Rank = "A" });
        var two = XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Pleno, Score = 80, Rank = "A", HintLevelsUsed = [1, 2] });
        var all = XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Pleno, Score = 80, Rank = "A", HintLevelsUsed = [1, 2, 3, 3] });

        two.HintFactor.ShouldBe(0.7);
        ((double)two.AttemptXp).ShouldBe(none.AttemptXp * 0.7, 1);
        all.HintFactor.ShouldBe(0.35);
        XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Pleno, Score = 80, Rank = "A", HintLevelsUsed = [3, 3, 3] }).HintFactor.ShouldBe(0.65);
    }

    [Fact]
    public void Worse_retry_credits_nothing()
    {
        var xp = XpCalculator.Compute(new XpInput { Difficulty = Difficulty.Staff, Score = 40, Rank = "D", FirstAttempt = false, PreviousBestXp = 400 });

        xp.Credited.ShouldBe(0);
    }
}
