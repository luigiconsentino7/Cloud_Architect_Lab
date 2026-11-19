using Lab.Content;
using Lab.Ir.Catalog;
using Lab.Ir.Content;
using Lab.Ir.Seeds;

namespace Lab.Modules.Shared;

/// <summary>Conteúdo versionado (catálogo, sementes, textos das regras), carregado uma vez na inicialização.</summary>
public sealed class LabContent(ContentBundle bundle)
{
    public ContentBundle Bundle { get; } = bundle;

    public ComponentCatalog Catalog => Bundle.Catalog;

    public RuleTextLibrary RuleTexts => Bundle.RuleTexts;

    public IReadOnlyList<Seed> Seeds => Bundle.Seeds;

    public string ContentHash => Bundle.ContentHash;

    public Seed? FindSeed(string id) => Bundle.FindSeed(id);
}

public sealed class LabOptions
{
    /// <summary>Pasta de dados (banco, backups). Padrão: pasta do usuário de cada sistema.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Pasta content/ (padrão: procurada a partir do executável).</summary>
    public string? ContentDirectory { get; set; }

    /// <summary>Origens aceitas em requisições que alteram estado (além da própria origem do app).</summary>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>Orçamento de eventos por cenário (reduzido nos testes de integração).</summary>
    public long EventBudget { get; set; } = 20_000_000;

    public int? MaxParallelism { get; set; }

    public static string DefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CloudArchitectLab");
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "CloudArchitectLab");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = string.IsNullOrWhiteSpace(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share") : xdg;
        return Path.Combine(root, "cloud-architect-lab");
    }
}
