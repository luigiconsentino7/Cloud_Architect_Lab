using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Content;
using Lab.Ir;
using Lab.Ir.Scene;
using Lab.Pipeline;
using Lab.Pipeline.Seeds;
using Lab.Scoring;

namespace Lab.Cli;

/// <summary>
/// <c>lab evaluate</c>: avalia a referência (ou uma cena exportada do app) e grava
/// <c>exports/&lt;desafio&gt;/{challenge,ir,evaluation}.json</c> — a entrada da skill <c>revisar-avaliacao</c>.
/// </summary>
public static class EvaluateCommand
{
    private static readonly JsonSerializerOptions Indented = new(LabJson.Options) { WriteIndented = true };

    public static int Run(string contentRoot, Args args)
    {
        var content = ContentRepository.Load(contentRoot);
        var seed = ChallengeCommands.SelectSeeds(content, args.Positionals.Take(1).ToList()).Single();
        var instance = SeedInstantiator.Instantiate(ChallengeCommands.Request(seed, args), content.Catalog);
        var challenge = instance.Challenge;

        var request = new EvaluationRequest
        {
            Challenge = challenge,
            Catalog = content.Catalog,
            RuleTexts = content.RuleTexts,
            IncludeSeries = false,
            Progress = new Progress<EvaluationProgress>(p => Console.Error.Write($"\r  … {p.Message,-50}")),
        };

        if (args.Option("scene") is { } scenePath)
        {
            // Mesmo formato do snapshot do app: { "scene": <Excalidraw>, "flows": [ { "useCaseId", "steps" } ] }.
            var snapshot = JsonNode.Parse(File.ReadAllText(scenePath)) as JsonObject
                ?? throw new UsageException($"{scenePath}: JSON inválido.");
            request = request with
            {
                Scene = snapshot["scene"] ?? snapshot,
                Flows = snapshot["flows"]?.Deserialize<List<FlowDefinition>>(LabJson.Options) ?? [],
            };
        }
        else if (args.Flag("reference") || args.Option("scene") is null)
        {
            request = request with { Graph = challenge.Reference ?? throw new UsageException($"{seed.Id} não tem solução de referência.") };
        }

        var result = EvaluationPipeline.Evaluate(request);
        Console.Error.Write("\r" + new string(' ', 56) + "\r");
        Print(result);

        var outRoot = args.Option("out") ?? "exports";
        var directory = Path.Combine(outRoot, challenge.Id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "challenge.json"), JsonSerializer.Serialize(challenge with { Reference = null }, Indented));
        File.WriteAllText(Path.Combine(directory, "ir.json"), JsonSerializer.Serialize(result.Graph, Indented));
        File.WriteAllText(Path.Combine(directory, "evaluation.json"), JsonSerializer.Serialize(result, Indented));
        Console.WriteLine($"Exportado em {directory}/ (challenge, ir, evaluation).");
        return 0;
    }

    private static void Print(EvaluationResult result)
    {
        var score = result.Score;
        Console.WriteLine($"Nota {score.FinalScore} ({score.Rank}) — base {Fmt.Points(score.BaseScore)}" +
            (score.Cap is { Applied: true } cap ? $", trava de {cap.Value}: {cap.Reason}" : ""));
        foreach (var dimension in score.Dimensions)
            Console.WriteLine($"  {dimension.Dimension,-12} {Fmt.Points(dimension.Score),5}  (peso {dimension.Weight})  {dimension.Basis}");
        Console.WriteLine($"Custo: {Fmt.Usd(result.Cost.MonthlyUsd)}/mês (orçamento {Fmt.Usd(result.Cost.BudgetUsd)})");
        foreach (var check in score.Checks.Where(c => !c.Passed))
            Console.WriteLine($"  ✗ {check.Id}: {check.Description}");
        if (result.Review.Summary.Count > 0) Console.WriteLine("Maiores perdas:");
        foreach (var line in result.Review.Summary)
            Console.WriteLine($"  − {Fmt.Points(line.Impact)} pts  {line.Text}");
        foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            Console.WriteLine($"  diagnóstico {diagnostic.Code}: {diagnostic.Message}");
        Console.WriteLine($"XP desta tentativa: {result.Xp.AttemptXp}. Manifesto {result.Manifest.Hash()[..19]}…");
    }
}
