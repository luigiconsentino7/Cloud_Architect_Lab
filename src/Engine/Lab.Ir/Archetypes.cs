namespace Lab.Ir;

/// <summary>Arquétipos agnósticos de provedor. O arquétipo define o modelo de simulação; o catálogo, os parâmetros.</summary>
public static class Archetypes
{
    public const string Client = "client";
    public const string Dns = "edge.dns";
    public const string Cdn = "edge.cdn";
    public const string Waf = "edge.waf";
    public const string ApiGateway = "edge.api-gateway";
    public const string LoadBalancer = "network.load-balancer";
    public const string Service = "compute.service";
    public const string Function = "compute.function";
    public const string StreamProcessor = "compute.stream-processor";
    public const string Orchestrator = "workflow.orchestrator";
    public const string Relational = "data.relational";
    public const string RelationalDistributed = "data.relational-distributed";
    public const string KeyValue = "data.key-value";
    public const string Document = "data.document";
    public const string Cache = "data.cache";
    public const string ObjectStorage = "data.object-storage";
    public const string Queue = "messaging.queue";
    public const string Topic = "messaging.topic";
    public const string Stream = "messaging.stream";
    public const string EventBus = "messaging.event-bus";
    public const string Identity = "security.identity";
    public const string Secrets = "security.secrets";
    public const string Observability = "observability";
    public const string External = "external.system";

    public static string Family(string archetype)
    {
        var dot = archetype.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? archetype : archetype[..dot];
    }

    public static bool IsClient(string archetype) => archetype == Client;
    public static bool IsCompute(string archetype) => archetype.StartsWith("compute.", StringComparison.Ordinal) || archetype == Orchestrator;
    public static bool IsData(string archetype) => archetype.StartsWith("data.", StringComparison.Ordinal);
    public static bool IsDurableData(string archetype) => IsData(archetype) && archetype != Cache;
    public static bool IsMessaging(string archetype) => archetype.StartsWith("messaging.", StringComparison.Ordinal);
    public static bool IsEdge(string archetype) => archetype.StartsWith("edge.", StringComparison.Ordinal) || archetype == LoadBalancer;
    public static bool IsExternal(string archetype) => archetype == External;

    /// <summary>Componentes que não participam de fluxos: só satisfazem configurações (cofre, observabilidade, identidade, DNS).</summary>
    public static bool IsPassive(string archetype) => archetype is Identity or Secrets or Observability or Dns;

    /// <summary>Componentes cujo posicionamento em AZs importa para falhas e pontos únicos de falha.</summary>
    public static bool IsZonal(string archetype) => archetype is Service or Relational or Cache or StreamProcessor;
}
