using Lab.Rules.Rules;

namespace Lab.Rules;

public static class RuleEngine
{
    public static IReadOnlyList<IArchitectureRule> All { get; } =
    [
        new SinglePointOfFailureRule(),
        new MissingTimeoutRule(),
        new RetryPolicyRule(),
        new MissingDlqRule(),
        new ExternalDependencyRule(),
        new DeepSyncChainRule(),
        new CrossRegionSyncRule(),
        new StreamPartitionsRule(),
        new FunctionConnectionPoolRule(),
        new HotPartitionRule(),
        new SharedDatabaseRule(),
        new DualWriteRule(),
        new IdempotentConsumerRule(),
        new IdempotencyKeyRule(),
        new OrderingRule(),
        new AtomicBalanceRule(),
        new CacheAsSourceOfTruthRule(),
        new RpoRule(),
        new PublicDataRule(),
        new EdgeAuthRule(),
        new WafRule(),
        new EncryptionAtRestRule(),
        new DataResidencyRule(),
        new AuditTrailRule(),
        new SecretsRule(),
        new ObservabilityRule(),
        new QueueAlarmRule(),
    ];

    public static IArchitectureRule? Find(string id) => All.FirstOrDefault(r => r.Id == id);

    /// <summary>Executa as regras aplicáveis ao desafio, em ordem estável de ID.</summary>
    public static IReadOnlyList<Finding> Run(RuleContext context) =>
        All.Where(r => r.AppliesTo(context.Challenge))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .SelectMany(r => r.Evaluate(context))
            .ToList();
}
