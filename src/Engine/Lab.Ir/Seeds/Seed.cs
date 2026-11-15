using System.Text.Json.Nodes;
using Lab.Ir.Challenges;

namespace Lab.Ir.Seeds;

/// <summary>Variável da semente com faixa [mín, máx] por dificuldade.</summary>
public sealed record VariableSpec
{
    public IReadOnlyDictionary<Difficulty, double[]> Ranges { get; init; } = new Dictionary<Difficulty, double[]>();
    public double Default { get; init; }
    public double Step { get; init; } = 1;
    public string Description { get; init; } = "";
}

/// <summary>Operação de uma pegadinha: <c>merge</c> (mescla objeto), <c>append</c> (adiciona à lista) ou <c>set</c>.</summary>
public sealed record TwistOp
{
    public string Op { get; init; } = "merge";
    public required string Path { get; init; }
    public JsonNode? Value { get; init; }
}

public sealed record TwistSpec
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    public Difficulty MinDifficulty { get; init; } = Difficulty.Junior;
    public IReadOnlyList<TwistOp> Ops { get; init; } = [];
}

/// <summary>
/// Desafio curado (formato de autoria). O <see cref="Body"/> guarda enunciado, casos de uso, workload,
/// plano de testes, restrições, dicas e solução de referência, com templates <c>{{ }}</c>.
/// </summary>
public sealed record Seed
{
    public required string Id { get; init; }
    public int Version { get; init; } = 1;
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public string Track { get; init; } = "";
    public string Domain { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<Difficulty> Difficulties { get; init; } = [];
    public IReadOnlyDictionary<string, VariableSpec> Variables { get; init; } = new Dictionary<string, VariableSpec>();
    public IReadOnlyList<TwistSpec> Twists { get; init; } = [];
    public required JsonObject Body { get; init; }
    public string SourceFile { get; init; } = "";
}
