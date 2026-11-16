using System.Text.Json;
using System.Text.Json.Nodes;
using Lab.Ir;
using Lab.Ir.Catalog;

namespace Lab.Content;

/// <summary>Carrega <c>content/catalog</c>: arquétipos (padrões) + um arquivo por provedor (sobrescritas).</summary>
public static class CatalogLoader
{
    private sealed record ArchetypeFile(string Version, IReadOnlyList<ArchetypeDefinition> Archetypes);

    private sealed record ComponentEntry
    {
        public required string Id { get; init; }
        public required string Archetype { get; init; }
        public string DisplayName { get; init; } = "";
        public string? ShortName { get; init; }
        public IReadOnlyList<string> Aliases { get; init; } = [];
        public ComponentScope? Scope { get; init; }
        public bool IsDefault { get; init; }
        public bool Shared { get; init; }
        public Dictionary<string, string> Capabilities { get; init; } = [];
        public JsonObject? ConfigSchema { get; init; }
        public PerformanceProfile? Performance { get; init; }
        public PricingModel? Pricing { get; init; }
        public string? Docs { get; init; }
        public SourceRef? Source { get; init; }
    }

    private sealed record ProviderFile(CloudProvider Provider, string? ReviewedAt, IReadOnlyList<ComponentEntry> Components);

    public static ComponentCatalog Load(string catalogDirectory)
    {
        var archetypesPath = Path.Combine(catalogDirectory, "archetypes.yaml");
        var archetypeFile = Read<ArchetypeFile>(archetypesPath);
        var archetypes = archetypeFile.Archetypes
            .Select(a => a with { ConfigSchema = SchemaDefaults.Inject(a.Id, a.ConfigSchema) })
            .ToDictionary(a => a.Id, StringComparer.Ordinal);

        var components = new List<ComponentDefinition>();
        foreach (var file in Directory.GetFiles(catalogDirectory, "*.yaml").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) == "archetypes.yaml") continue;
            var providerFile = Read<ProviderFile>(file);
            foreach (var entry in providerFile.Components)
            {
                if (!archetypes.TryGetValue(entry.Archetype, out var archetype))
                    throw new ContentException($"{Path.GetFileName(file)}: '{entry.Id}' usa o arquétipo desconhecido '{entry.Archetype}'.");
                var withSource = entry.Source is null && entry.Docs is not null
                    ? entry with { Source = new SourceRef(entry.Docs, providerFile.ReviewedAt ?? "") }
                    : entry;
                components.Add(Merge(providerFile.Provider, withSource, archetype));
            }
        }

        var duplicates = components.GroupBy(c => c.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (duplicates.Count > 0)
            throw new ContentException($"Componentes duplicados no catálogo: {string.Join(", ", duplicates)}.");

        return new ComponentCatalog(archetypeFile.Version, archetypes.Values, components);
    }

    private static ComponentDefinition Merge(CloudProvider provider, ComponentEntry entry, ArchetypeDefinition archetype)
    {
        var capabilities = new Dictionary<string, string>(archetype.Capabilities, StringComparer.Ordinal);
        foreach (var (key, value) in entry.Capabilities) capabilities[key] = value;

        var schema = (archetype.ConfigSchema?.DeepClone() as JsonObject) ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        if (entry.ConfigSchema is not null) DeepMerge(schema, entry.ConfigSchema);

        return new ComponentDefinition
        {
            Id = entry.Id,
            Provider = provider,
            Archetype = entry.Archetype,
            DisplayName = entry.DisplayName,
            ShortName = entry.ShortName,
            Category = archetype.Category,
            Aliases = entry.Aliases,
            Scope = entry.Scope ?? archetype.Scope,
            IsDefault = entry.IsDefault,
            Shared = entry.Shared,
            Capabilities = capabilities,
            ConfigSchema = schema,
            Performance = archetype.Performance.Merge(entry.Performance),
            Pricing = archetype.Pricing.Merge(entry.Pricing),
            Docs = entry.Docs,
            Source = entry.Source,
        };
    }

    /// <summary>Capacidades são texto; aceita <c>true</c>/<c>false</c> sem aspas no YAML.</summary>
    private static void StringifyCapabilities(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj.ToList())
                {
                    if (key == "capabilities" && value is JsonObject capabilities)
                    {
                        foreach (var (name, capability) in capabilities.ToList())
                        {
                            if (capability is JsonValue v && !v.TryGetValue<string>(out _))
                                capabilities[name] = v.ToJsonString().Trim('"');
                        }
                    }
                    else
                    {
                        StringifyCapabilities(value);
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array) StringifyCapabilities(item);
                break;
        }
    }

    internal static void DeepMerge(JsonObject target, JsonObject overrides)
    {
        foreach (var (key, value) in overrides)
        {
            if (value is JsonObject overrideObject && target[key] is JsonObject targetObject)
                DeepMerge(targetObject, overrideObject);
            else
                target[key] = value?.DeepClone();
        }
    }

    private static T Read<T>(string path)
    {
        var node = YamlJson.Parse(File.ReadAllText(path), Path.GetFileName(path));
        StringifyCapabilities(node);
        try
        {
            return node.Deserialize<T>(LabJson.Options) ?? throw new ContentException($"{path}: arquivo vazio.");
        }
        catch (JsonException ex)
        {
            throw new ContentException($"{Path.GetFileName(path)}: {ex.Message}", ex);
        }
    }
}

/// <summary>Propriedades de configuração comuns injetadas em todos os arquétipos (operação, rede, criptografia, zonas).</summary>
public static class SchemaDefaults
{
    public static JsonObject Inject(string archetype, JsonObject? schema)
    {
        var result = (schema?.DeepClone() as JsonObject) ?? [];
        result["type"] ??= "object";
        if (result["properties"] is not JsonObject properties)
        {
            properties = [];
            result["properties"] = properties;
        }

        if (Archetypes.IsClient(archetype) || Archetypes.IsExternal(archetype)) return result;

        properties["observability"] ??= Parse("""
            { "type": "object", "title": "Operação", "properties": {
              "metrics": { "type": "boolean", "default": true, "title": "Métricas" },
              "logs": { "type": "boolean", "default": true, "title": "Logs" },
              "tracing": { "type": "boolean", "default": false, "title": "Tracing distribuído" },
              "alarms": { "type": "boolean", "default": false, "title": "Alarmes" } } }
            """);

        if (Archetypes.IsData(archetype) || Archetypes.IsMessaging(archetype))
        {
            properties["network"] ??= Parse("""{ "type": "string", "enum": ["private", "public"], "default": "private", "title": "Rede" }""");
            properties["encryptionAtRest"] ??= Parse("""
                { "type": "string", "enum": ["provider-managed", "customer-managed", "disabled"], "default": "provider-managed", "title": "Criptografia em repouso" }
                """);
        }

        if (Archetypes.IsCompute(archetype))
        {
            properties["network"] ??= Parse("""{ "type": "string", "enum": ["private", "public"], "default": "private", "title": "Rede" }""");
            properties["responsibilities"] ??= Parse("""{ "type": "array", "items": { "type": "string" }, "title": "Responsabilidades" }""");
            properties["secrets"] ??= Parse("""{ "type": "string", "enum": ["vault", "env"], "default": "vault", "title": "Segredos" }""");
            properties["idempotency"] ??= Parse("""
                { "type": "object", "title": "Idempotência do consumidor", "properties": {
                  "enabled": { "type": "boolean", "default": false, "title": "Deduplicar mensagens" },
                  "storeNodeId": { "type": "string", "title": "Armazenamento usado para dedupe" },
                  "sequenceCheck": { "type": "boolean", "default": false, "title": "Descartar mensagens com sequência mais antiga" } } }
                """);
        }

        if (Archetypes.IsZonal(archetype))
        {
            properties["zones"] ??= Parse("""{ "type": "array", "items": { "type": "string", "enum": ["a", "b", "c"] }, "title": "Zonas (AZs)" }""");
        }

        return result;
    }

    private static JsonNode Parse(string json) => JsonNode.Parse(json)!;
}
