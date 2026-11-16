using System.Text;
using Lab.Cli;
using Lab.Content;
using Lab.Pipeline.Seeds;

Console.OutputEncoding = Encoding.UTF8;

const string Usage = """
    lab — ferramenta de conteúdo do Cloud Architect Lab

    Uso:
      lab challenge list
      lab challenge validate [semente|arquivo ...] [--quick] [--provider aws,azure,gcp] [--difficulty senior] [--no-twists]
      lab challenge show <semente> [--provider aws] [--difficulty senior] [--twist id] [--var nome=valor] [--variation n]
      lab challenge scene <semente> [mesmas opções]   # a referência como cena do canvas + fluxos (JSON)
      lab rules validate
      lab catalog validate
      lab evaluate <semente> [--reference | --scene cena.json] [--provider aws] [--difficulty senior] [--out exports/]

    Opções gerais:
      --content <pasta>   pasta content/ (padrão: procurada a partir do diretório atual)

    --quick valida só a variação padrão, em AWS, sem pegadinhas e com janelas de simulação comprimidas.
    """;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Console.WriteLine(Usage);
        return 0;
    }

    var parsed = Args.Parse(args.Skip(1), "quick", "no-twists", "reference", "json");
    var contentRoot = parsed.Option("content") ?? ContentRepository.Discover(Directory.GetCurrentDirectory())
        ?? throw new UsageException("Pasta content/ não encontrada. Rode dentro do repositório ou use --content.");

    return (args[0], parsed.Positionals.FirstOrDefault()) switch
    {
        ("challenge", "list") => ChallengeCommands.List(contentRoot),
        ("challenge", "validate") => ChallengeCommands.Validate(contentRoot, parsed),
        ("challenge", "show") => ChallengeCommands.Show(contentRoot, parsed),
        ("challenge", "scene") => ChallengeCommands.Scene(contentRoot, parsed),
        ("rules", "validate") => ContentCommands.ValidateRules(contentRoot),
        ("catalog", "validate") => ContentCommands.ValidateCatalog(contentRoot),
        ("evaluate", _) => EvaluateCommand.Run(contentRoot, parsed),
        _ => throw new UsageException($"Comando desconhecido: {string.Join(' ', args.Take(2))}."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(Usage);
    return 2;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}
catch (ContentException ex)
{
    Console.Error.WriteLine($"Conteúdo inválido: {ex.Message}");
    return 1;
}
catch (SeedException ex)
{
    Console.Error.WriteLine($"Semente inválida: {ex.Message}");
    return 1;
}
