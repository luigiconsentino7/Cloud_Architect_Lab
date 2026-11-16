using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Lab.Content;
using Lab.Ir;
using Lab.Ir.Challenges;
using Lab.Ir.Seeds;
using Lab.Pipeline.Seeds;

namespace Lab.Cli;

public static class ChallengeCommands
{
    public static int List(string contentRoot)
    {
        var content = ContentRepository.Load(contentRoot);
        foreach (var track in content.Seeds.GroupBy(s => s.Track).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"{track.Key}:");
            foreach (var seed in track.OrderBy(s => s.Id, StringComparer.Ordinal))
            {
                var difficulties = string.Join("/", seed.Difficulties.Select(d => d.ToString().ToLowerInvariant()));
                Console.WriteLine($"  {seed.Id,-34} v{seed.Version}  {difficulties,-22} {seed.Title}");
            }
        }

        Console.WriteLine($"{content.Seeds.Count} semente(s).");
        return 0;
    }

    public static int Validate(string contentRoot, Args args)
    {
        var content = ContentRepository.Load(contentRoot);
        var seeds = SelectSeeds(content, args.Positionals.Skip(1).ToList());
        var quick = args.Flag("quick");
        var providers = args.Options("provider").Select(p => CloudProviders.Parse(p)).ToList();
        var difficulties = args.Options("difficulty").Select(d => SeedLoader.ParseDifficulty(d)).ToList();
        var options = new SeedValidationOptions
        {
            Providers = providers.Count > 0 ? providers : quick ? [CloudProvider.Aws] : [CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp],
            Difficulties = difficulties.Count > 0 ? difficulties : null,
            Corners = !quick,
            Twists = !quick && !args.Flag("no-twists"),
            EventBudget = quick ? 4_000_000 : 20_000_000,
            Progress = new Progress<string>(name => Console.Error.Write($"\r  … {name,-60}")),
        };

        var failed = 0;
        foreach (var seed in seeds)
        {
            Console.WriteLine($"▶ {seed.Id} ({seed.SourceFile})");
            var stopwatch = Stopwatch.StartNew();
            var report = SeedValidator.Validate(seed, content.Catalog, content.RuleTexts, options);
            Console.Error.Write("\r" + new string(' ', 66) + "\r");
            foreach (var variation in report.Variations)
            {
                var cost = variation.ReferenceCostUsd is { } c ? $"US$ {c.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))}/{variation.BudgetUsd.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))}" : "";
                Console.WriteLine($"  {variation.Name,-46} {variation.ReferenceScore,3} ({variation.Rank})  {cost}");
            }

            foreach (var group in report.Issues.GroupBy(i => (i.Severity, i.Message)).OrderBy(g => g.Key.Severity))
            {
                var where = string.Join(", ", group.Select(i => i.Variation).OfType<string>().Distinct());
                var label = group.Key.Severity == IssueSeverity.Error ? "ERRO " : "aviso";
                Console.WriteLine($"  {label} {group.Key.Message}{(where.Length > 0 ? $"  [{where}]" : "")}");
            }

            Console.WriteLine(report.Ok ? $"  ✓ ok em {stopwatch.Elapsed.TotalSeconds:N0} s" : "  ✗ reprovada");
            if (!report.Ok) failed++;
        }

        Console.WriteLine(failed == 0 ? $"{seeds.Count} semente(s) válidas." : $"{failed} de {seeds.Count} semente(s) com erro.");
        return failed == 0 ? 0 : 1;
    }

    public static int Show(string contentRoot, Args args)
    {
        var content = ContentRepository.Load(contentRoot);
        var seed = SelectSeeds(content, args.Positionals.Skip(1).Take(1).ToList()).Single();
        var instance = SeedInstantiator.Instantiate(Request(seed, args), content.Catalog);
        Console.WriteLine(JsonSerializer.Serialize(instance.Challenge, new JsonSerializerOptions(LabJson.Options) { WriteIndented = true }));
        foreach (var diagnostic in instance.ReferenceDiagnostics)
            Console.Error.WriteLine($"referência: {diagnostic.Severity} {diagnostic.Code} {diagnostic.Message}");
        return 0;
    }

    public static InstantiationRequest Request(Seed seed, Args args)
    {
        var variables = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var pair in args.Options("var"))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                throw new UsageException($"--var espera nome=valor (ex.: --var baselineRps=1200), recebeu '{pair}'.");
            variables[parts[0]] = value;
        }

        var difficulty = args.Option("difficulty") is { } d ? SeedLoader.ParseDifficulty(d)
            : seed.Difficulties.Contains(Difficulty.Senior) ? Difficulty.Senior : seed.Difficulties.FirstOrDefault(Difficulty.Senior);
        return new InstantiationRequest
        {
            Seed = seed,
            Provider = args.Option("provider") is { } p ? CloudProviders.Parse(p) : CloudProvider.Aws,
            Difficulty = difficulty,
            Mode = args.Option("mode") is { } m ? Enum.Parse<GameMode>(m, ignoreCase: true) : GameMode.Treino,
            Variables = variables.Count > 0 ? variables : null,
            Twists = args.Options("twist"),
            VariationSeed = args.Option("variation") is { } v ? long.Parse(v, CultureInfo.InvariantCulture) : null,
        };
    }

    public static IReadOnlyList<Seed> SelectSeeds(ContentBundle content, IReadOnlyList<string> selectors)
    {
        if (selectors.Count == 0) return content.Seeds;
        var seeds = new List<Seed>();
        foreach (var selector in selectors)
        {
            if (selector.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) && File.Exists(selector))
                seeds.Add(SeedLoader.Load(selector));
            else
                seeds.Add(content.FindSeed(selector) ?? throw new UsageException($"Semente '{selector}' não encontrada (use lab challenge list)."));
        }

        return seeds;
    }
}
