using Lab.Ir;

namespace Lab.Scoring;

/// <summary>Padrão de arquitetura reconhecido no desenho, com os elementos que o aplicam.</summary>
public sealed record PatternUse(string Id, string Name, string Detail, IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);

/// <summary>Reconhece padrões na IR (outbox, idempotência, circuit breaker…) para pontos fortes e para a comparação com a referência.</summary>
public static class PatternDetector
{
    public static IReadOnlyList<PatternUse> Detect(ArchitectureGraph graph)
    {
        var found = new List<PatternUse>();

        void Edges(string id, string name, string detail, Func<Edge, bool> predicate)
        {
            var edges = graph.Edges.Where(predicate).Select(e => e.Id).Order(StringComparer.Ordinal).ToList();
            if (edges.Count > 0) found.Add(new PatternUse(id, name, detail, [], edges));
        }

        void Nodes(string id, string name, string detail, Func<Node, bool> predicate)
        {
            var nodes = graph.Nodes.Where(predicate).Select(n => n.Id).Order(StringComparer.Ordinal).ToList();
            if (nodes.Count > 0) found.Add(new PatternUse(id, name, detail, nodes, []));
        }

        Edges("outbox", "Outbox transacional / CDC",
            "o evento sai na mesma transação da escrita, então não se perde se o processo cair no meio",
            e => e.Config.Delivery == DeliveryMode.Outbox || e.Interaction == Interaction.Cdc);
        Nodes("idempotent-consumer", "Consumidor idempotente",
            "reentregas da fila não viram efeitos duplicados", n => n.Cfg.Bool("idempotency.enabled"));
        Edges("idempotency-key", "Chave de idempotência na requisição",
            "o cliente pode repetir a chamada sem repetir o efeito", e => e.Config.IdempotencyKey);
        Edges("atomic-write", "Escrita atômica",
            "transação ou escrita condicional protege o saldo de corridas e duplicidade", e => e.Config.HasAtomicWrite);
        Edges("circuit-breaker", "Circuit breaker",
            "a dependência lenta é isolada em vez de prender as threads do serviço", e => e.Config.CircuitBreaker?.Enabled == true);
        Edges("retry-jitter", "Retry com backoff exponencial e jitter",
            "as novas tentativas não sincronizam nem amplificam a carga", e => e.Config.Retries is { Max: > 0, Backoff: BackoffKind.ExponentialJitter });
        Edges("fallback", "Fallback em caso de falha",
            "há um caminho definido quando a dependência falha", e => e.Config.Trigger == TriggerKind.OnFailure);
        Edges("cache-aside", "Cache-aside",
            "leituras quentes saem do cache e só o miss vai ao banco", e => e.Config.Trigger == TriggerKind.OnCacheMiss);
        Nodes("dlq", "Fila de mensagens mortas (DLQ)",
            "mensagens envenenadas são isoladas sem travar o consumo", n => Archetypes.IsMessaging(n.Archetype) && n.Cfg.Bool("dlq.enabled"));
        Nodes("multi-az", "Multi-AZ",
            "a perda de uma zona não derruba o componente",
            n => Archetypes.IsZonal(n.Archetype) && (n.Placement.Zones.Count >= 2 || n.Cfg.Bool("multiAz")));
        Nodes("autoscaling", "Autoscaling",
            "a capacidade acompanha a carga",
            n => n.Archetype == Archetypes.Service && n.Cfg.Bool("autoscaling.enabled") && n.Cfg.Int("instances.max", 0) > n.Cfg.Int("instances.min", 0));
        Nodes("async-messaging", "Mensageria assíncrona",
            "trabalho que não precisa estar na resposta sai do caminho síncrono", n => Archetypes.IsMessaging(n.Archetype));
        Nodes("cache", "Cache", "reduz latência e carga no banco", n => n.Archetype == Archetypes.Cache);
        Nodes("waf", "WAF na borda", "filtra ataques comuns antes de chegarem à API",
            n => n.Archetype == Archetypes.Waf || (n.Archetype == Archetypes.ApiGateway && n.Cfg.Bool("waf")));
        Nodes("tracing", "Tracing distribuído", "dá para seguir uma transação de ponta a ponta",
            n => n.Cfg.Bool("observability.tracing"));
        return found;
    }

    /// <summary>Padrões que contam como ponto forte quando aparecem (os demais só entram na comparação).</summary>
    public static bool IsStrength(string patternId) =>
        patternId is "outbox" or "idempotent-consumer" or "idempotency-key" or "atomic-write" or "circuit-breaker"
            or "retry-jitter" or "fallback" or "cache-aside" or "dlq" or "multi-az";
}
