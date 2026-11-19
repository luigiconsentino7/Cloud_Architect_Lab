using Lab.Ir.Challenges;

namespace Lab.Scoring;

public sealed record XpInput
{
    public required Difficulty Difficulty { get; init; }
    public required int Score { get; init; }
    public required string Rank { get; init; }
    public GameMode Mode { get; init; } = GameMode.Treino;
    public bool FirstAttempt { get; init; } = true;

    /// <summary>Níveis de dica abertos nesta tentativa (1, 2, 3).</summary>
    public IReadOnlyList<int> HintLevelsUsed { get; init; } = [];

    /// <summary>Maior XP de tentativa já creditado neste desafio.</summary>
    public int PreviousBestXp { get; init; }
}

public sealed record XpFactor(string Name, double Factor);

/// <summary>Soma de XP detalhada, como aparece na tela de resultado.</summary>
public sealed record XpBreakdown(
    int Base,
    double ScoreFactor,
    IReadOnlyList<XpFactor> Multipliers,
    double HintFactor,
    int AttemptXp,
    int PreviousBestXp,
    int Credited);

/// <summary>Fórmula de XP da spec 07: só a melhoria sobre a melhor tentativa rende XP.</summary>
public static class XpCalculator
{
    public static int BaseFor(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Junior => 100,
        Difficulty.Pleno => 200,
        Difficulty.Senior => 350,
        _ => 500,
    };

    public static double HintDeduction(int level) => level switch
    {
        1 => 0.10,
        2 => 0.20,
        3 => 0.35,
        _ => 0,
    };

    public static XpBreakdown Compute(XpInput input)
    {
        var baseXp = BaseFor(input.Difficulty);
        var scoreFactor = Math.Pow(Math.Clamp(input.Score, 0, 100) / 100.0, 1.5);
        var multipliers = new List<XpFactor>();
        if (input.FirstAttempt) multipliers.Add(new XpFactor("primeira tentativa", 1.2));
        if (input.Mode == GameMode.Entrevista) multipliers.Add(new XpFactor("modo Entrevista", 1.3));
        if (input.Mode == GameMode.Plantao) multipliers.Add(new XpFactor("modo Plantão", 1.2));
        if (input.Rank == "S") multipliers.Add(new XpFactor("rank S", 1.25));
        if (input.Rank == "A") multipliers.Add(new XpFactor("rank A", 1.1));

        var deduction = input.HintLevelsUsed.Distinct().Sum(HintDeduction);
        var hintFactor = Math.Max(0.3, 1 - deduction);
        var raw = baseXp * scoreFactor * multipliers.Aggregate(1.0, (product, m) => product * m.Factor) * hintFactor;
        var attempt = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        var credited = Math.Max(0, attempt - Math.Max(0, input.PreviousBestXp));
        return new XpBreakdown(baseXp, Math.Round(scoreFactor, 4), multipliers, Math.Round(hintFactor, 2), attempt,
            input.PreviousBestXp, credited);
    }
}
