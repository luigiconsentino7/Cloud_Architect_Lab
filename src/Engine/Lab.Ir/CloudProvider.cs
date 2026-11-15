namespace Lab.Ir;

public enum CloudProvider
{
    Generic,
    Aws,
    Azure,
    Gcp,
}

public static class CloudProviders
{
    public static IReadOnlyList<CloudProvider> All { get; } = [CloudProvider.Aws, CloudProvider.Azure, CloudProvider.Gcp, CloudProvider.Generic];

    public static string Code(this CloudProvider provider) => provider switch
    {
        CloudProvider.Aws => "aws",
        CloudProvider.Azure => "azure",
        CloudProvider.Gcp => "gcp",
        _ => "generic",
    };

    public static CloudProvider Parse(string code) => code.Trim().ToLowerInvariant() switch
    {
        "aws" => CloudProvider.Aws,
        "azure" => CloudProvider.Azure,
        "gcp" => CloudProvider.Gcp,
        "generic" or "generico" or "genérico" => CloudProvider.Generic,
        _ => throw new ArgumentException($"Provedor desconhecido: '{code}'.", nameof(code)),
    };

    public static string DisplayName(this CloudProvider provider) => provider switch
    {
        CloudProvider.Aws => "AWS",
        CloudProvider.Azure => "Azure",
        CloudProvider.Gcp => "GCP",
        _ => "Genérico",
    };

    /// <summary>Região brasileira de cada provedor (onde os desafios com residência de dados devem ficar).</summary>
    public static string BrazilRegion(this CloudProvider provider) => provider switch
    {
        CloudProvider.Aws => "sa-east-1",
        CloudProvider.Azure => "brazilsouth",
        CloudProvider.Gcp => "southamerica-east1",
        _ => "br-1",
    };
}
