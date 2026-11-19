using Lab.Content;
using Lab.Ir;
using Lab.Rules;

namespace Lab.Cli;

/// <summary><c>lab rules validate</c> e <c>lab catalog validate</c>.</summary>
public static class ContentCommands
{
    /// <summary>Domínios aceitos em links de estudo e na documentação do catálogo: fontes oficiais.</summary>
    public static IReadOnlyList<string> OfficialDomains { get; } =
    [
        "aws.amazon.com", "docs.aws.amazon.com", "learn.microsoft.com", "cloud.google.com", "sre.google",
        "owasp.org", "opentelemetry.io", "kafka.apache.org", "www.planalto.gov.br", "www.bcb.gov.br",
        "redis.io", "valkey.io", "www.postgresql.org", "kubernetes.io",
    ];

    public static bool IsOfficial(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && OfficialDomains.Any(d => uri.Host == d || uri.Host.EndsWith("." + d, StringComparison.Ordinal));

    public static int ValidateRules(string contentRoot)
    {
        var content = ContentRepository.Load(contentRoot);
        var errors = new List<string>();
        var ruleIds = RuleEngine.All.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var rule in RuleEngine.All.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (content.RuleTexts.Find(rule.Id) is not { } text)
            {
                errors.Add($"{rule.Id}: sem texto em content/rules/.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(text.Title) || text.Title.Length > 70) errors.Add($"{rule.Id}: título vazio ou com mais de 70 caracteres.");
            if (string.IsNullOrWhiteSpace(text.Why)) errors.Add($"{rule.Id}: falta 'why' (por quê).");
            if (string.IsNullOrWhiteSpace(text.How)) errors.Add($"{rule.Id}: falta 'how' (como corrigir).");
            if (text.FollowUps.Count == 0) errors.Add($"{rule.Id}: falta ao menos uma pergunta de follow-up.");
            if (text.Links.Count == 0) errors.Add($"{rule.Id}: falta ao menos um link oficial.");
            foreach (var link in text.Links.Where(l => !IsOfficial(l.Url)))
                errors.Add($"{rule.Id}: link fora dos domínios oficiais: {link.Url}");
        }

        foreach (var text in content.RuleTexts.All.Where(t => !ruleIds.Contains(t.Id)))
            errors.Add($"{text.Id}: texto para uma regra que não existe no motor.");

        return Report($"{ruleIds.Count} regras, {content.RuleTexts.All.Count} textos", errors, []);
    }

    public static int ValidateCatalog(string contentRoot)
    {
        var content = ContentRepository.Load(contentRoot);
        var catalog = content.Catalog;
        var errors = new List<string>();
        var warnings = new List<string>();

        foreach (var component in catalog.Components.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            // Clientes, sistemas externos e a paleta genérica herdam os valores do arquétipo: não descrevem um serviço real.
            if (component.Shared || component.Provider == CloudProvider.Generic) continue;
            if (component.Source is not { } source || string.IsNullOrWhiteSpace(source.Url))
                errors.Add($"{component.Id}: sem fonte (docs ou source.url).");
            else if (!IsOfficial(source.Url))
                errors.Add($"{component.Id}: fonte fora dos domínios oficiais: {source.Url}");
            if (component.Source is { } s && !DateOnly.TryParse(s.ReviewedAt, out _))
                errors.Add($"{component.Id}: sem data de revisão (reviewedAt).");
            if (string.IsNullOrWhiteSpace(component.DisplayName)) errors.Add($"{component.Id}: sem displayName.");
        }

        foreach (var provider in new[] { CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp })
        {
            foreach (var archetype in catalog.Archetypes.Keys.Where(a => !Archetypes.IsClient(a) && !Archetypes.IsExternal(a)).Order(StringComparer.Ordinal))
            {
                var options = catalog.Components.Values.Where(c => c.Provider == provider && c.Archetype == archetype).ToList();
                if (options.Count == 0) warnings.Add($"{provider.DisplayName()}: nenhum componente para {archetype}.");
                else if (options.Count(c => c.IsDefault) != 1) errors.Add($"{provider.DisplayName()}: {archetype} precisa de exatamente um componente padrão (isDefault).");
            }
        }

        return Report($"catálogo {catalog.Version}: {catalog.Components.Count} componentes, {catalog.Archetypes.Count} arquétipos", errors, warnings);
    }

    private static int Report(string summary, IReadOnlyList<string> errors, IReadOnlyList<string> warnings)
    {
        foreach (var warning in warnings) Console.WriteLine($"  aviso {warning}");
        foreach (var error in errors) Console.WriteLine($"  ERRO  {error}");
        Console.WriteLine(errors.Count == 0 ? $"✓ {summary}" : $"✗ {summary}: {errors.Count} erro(s)");
        return errors.Count == 0 ? 0 : 1;
    }
}
