using Lab.Content;
using Lab.Ir;

namespace Lab.Content.Tests;

public sealed class CatalogTests
{
    private static readonly Lazy<ContentBundle> Content = new(() => ContentRepository.LoadDiscovered());

    private static readonly string[] ArchetypesWithProviderServices =
    [
        Archetypes.ApiGateway, Archetypes.LoadBalancer, Archetypes.Service, Archetypes.Function, Archetypes.Relational,
        Archetypes.KeyValue, Archetypes.Cache, Archetypes.ObjectStorage, Archetypes.Queue, Archetypes.Topic, Archetypes.Stream,
        Archetypes.Waf, Archetypes.Secrets, Archetypes.Observability,
    ];

    [Fact]
    public void Catalog_loads_with_all_providers()
    {
        var catalog = Content.Value.Catalog;

        foreach (var provider in CloudProviders.All)
            catalog.ForProvider(provider).Count(c => !c.Shared).ShouldBeGreaterThan(15, $"provedor {provider}");
    }

    [Theory]
    [InlineData(CloudProvider.Aws)]
    [InlineData(CloudProvider.Azure)]
    [InlineData(CloudProvider.Gcp)]
    [InlineData(CloudProvider.Generic)]
    public void Every_core_archetype_has_a_default_service_per_provider(CloudProvider provider)
    {
        var catalog = Content.Value.Catalog;

        foreach (var archetype in ArchetypesWithProviderServices)
        {
            var component = catalog.DefaultFor(provider, archetype);
            component.ShouldNotBeNull($"{provider} sem serviço para {archetype}");
            component.Provider.ShouldBe(provider, $"{provider}/{archetype}");
        }
    }

    [Fact]
    public void Every_provider_service_cites_a_source()
    {
        var missing = Content.Value.Catalog.Components.Values
            .Where(c => c.Provider != CloudProvider.Generic && c.Source is null)
            .Select(c => c.Id)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void Components_inherit_archetype_defaults_and_overrides()
    {
        var catalog = Content.Value.Catalog;
        var fifo = catalog.Get("aws.sqs-fifo");
        var lambda = catalog.Get("aws.lambda");

        fifo.Capability("ordering").ShouldBe("per-key");
        fifo.Performance.MessagesPerSecondPerGroup.ShouldBe(300);
        fifo.Performance.DeliveryLatency.ShouldNotBeNull();
        lambda.Performance.AccountConcurrencyLimit.ShouldBe(1000);
        lambda.ConfigSchema["properties"]!["observability"].ShouldNotBeNull();
    }

    [Fact]
    public void Shared_clients_appear_in_every_palette()
    {
        var catalog = Content.Value.Catalog;

        foreach (var provider in CloudProviders.All)
            catalog.ForProvider(provider).ShouldContain(c => c.Id == "client.mobile");
    }
}
