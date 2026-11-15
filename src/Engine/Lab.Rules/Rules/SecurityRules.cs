using Lab.Ir;
using Lab.Ir.Challenges;

namespace Lab.Rules.Rules;

/// <summary>SEC-001 — banco, cache ou fila acessível por rede pública.</summary>
public sealed class PublicDataRule : Rule
{
    public override string Id => "SEC-001";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.High;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var node in context.Graph.Nodes.Where(n => Archetypes.IsData(n.Archetype) || Archetypes.IsMessaging(n.Archetype)))
        {
            if (node.Cfg.String("network") == "public")
                yield return context.Create(this, $"\"{node.Label}\" está exposto em rede pública: dados e filas devem ficar em rede privada.", [node.Id]);
        }
    }
}

/// <summary>SEC-002 — API de cliente sem autenticação na borda.</summary>
public sealed class EdgeAuthRule : Rule
{
    public override string Id => "SEC-002";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.High;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var hasIdentity = context.Graph.Nodes.Any(n => n.Archetype == Archetypes.Identity);
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var useCase in context.Challenge.UseCases)
        {
            var entry = context.PlanFor(useCase)?.Root?.Children.FirstOrDefault();
            if (entry is null || !reported.Add(entry.Target.Id)) continue;
            var target = entry.Target;
            if (target.Archetype == Archetypes.ApiGateway && target.Cfg.String("auth") == "none")
            {
                yield return context.Create(this, $"\"{target.Label}\" recebe tráfego de clientes sem autenticação.", [target.Id], useCase: useCase.Id);
            }
            else if (target.Archetype is not (Archetypes.ApiGateway or Archetypes.Cdn or Archetypes.Waf) && !hasIdentity
                     && !target.Responsibilities.Contains("authenticate"))
            {
                yield return context.Create(this,
                    $"Os clientes chegam direto em \"{target.Label}\" sem gateway nem provedor de identidade: quem valida o token?",
                    [target.Id], useCase: useCase.Id, severity: Severity.Medium);
            }
        }
    }
}

/// <summary>SEC-003 — borda pública sem WAF.</summary>
public sealed class WafRule : Rule
{
    public override string Id => "SEC-003";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        if (context.Graph.Nodes.Any(n => n.Archetype == Archetypes.Waf)) yield break;
        var gateways = context.Graph.Nodes.Where(n => n.Archetype == Archetypes.ApiGateway).ToList();
        if (gateways.Count > 0 && gateways.All(g => g.Cfg.Bool("waf"))) yield break;
        var entry = context.Plans.Values.Select(p => p.Root?.Children.FirstOrDefault()?.Target).OfType<Node>().FirstOrDefault();
        if (entry is not null)
        {
            yield return context.Create(this,
                $"A borda pública (\"{entry.Label}\") não tem WAF: ataques de camada 7 e bots chegam direto à aplicação.", [entry.Id]);
        }
    }
}

/// <summary>SEC-004 — criptografia em repouso desligada quando o desafio exige.</summary>
public sealed class EncryptionAtRestRule : Rule
{
    public override string Id => "SEC-004";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.High;

    public override bool AppliesTo(ChallengeSpec challenge) =>
        challenge.Constraints.RequireEncryptionAtRest || challenge.Constraints.Compliance.Count > 0;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var node in context.Graph.Nodes.Where(n => Archetypes.IsData(n.Archetype) || Archetypes.IsMessaging(n.Archetype)))
        {
            if (node.Cfg.String("encryptionAtRest") == "disabled")
                yield return context.Create(this, $"\"{node.Label}\" guarda dados sem criptografia em repouso, e o desafio exige ({string.Join(", ", context.Challenge.Constraints.Compliance)}).", [node.Id]);
        }
    }
}

/// <summary>SEC-005 — dados fora da região exigida (residência no Brasil).</summary>
public sealed class DataResidencyRule : Rule
{
    public override string Id => "SEC-005";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.High;

    public override bool AppliesTo(ChallengeSpec challenge) => challenge.Constraints.DataResidencyBrazil;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var region = context.Challenge.Region;
        foreach (var node in context.Graph.Nodes.Where(n => Archetypes.IsData(n.Archetype) || Archetypes.IsMessaging(n.Archetype) || Archetypes.IsCompute(n.Archetype)))
        {
            if (node.Placement.Region is { } placed && placed != region)
            {
                yield return context.Create(this, $"\"{node.Label}\" está em {placed}, mas os dados precisam ficar no Brasil ({region}).", [node.Id]);
            }
            else if (context.Graph.Provider != CloudProvider.Azure
                     && (node.Cfg.Bool("globalTables") || node.Cfg.Bool("crossRegionReplica") || node.Cfg.Bool("multiRegion")))
            {
                yield return context.Create(this,
                    $"\"{node.Label}\" replica dados para outra região, e {context.Graph.Provider.DisplayName()} só tem uma região no Brasil: a réplica sai do país. Trade-off entre recuperação regional e residência.",
                    [node.Id], severity: Severity.Medium);
            }
        }
    }
}

/// <summary>SEC-006 — sem trilha de auditoria imutável quando exigida.</summary>
public sealed class AuditTrailRule : Rule
{
    public override string Id => "SEC-006";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.Medium;

    public override bool AppliesTo(ChallengeSpec challenge) => challenge.Constraints.RequireAuditLog;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        var hasImmutableWrite = context.Graph.Edges.Any(e =>
            e.Interaction == Interaction.DataAccess && e.Config.HasWrite
            && context.Graph.FindNode(e.To) is { } store && IsImmutable(context, store));
        if (!hasImmutableWrite)
        {
            yield return context.Create(this,
                "O desafio exige trilha de auditoria imutável, e nenhum fluxo grava num armazenamento append-only/WORM (ex.: objetos com bloqueio).");
        }
    }

    internal static bool IsImmutable(RuleContext context, Node store) =>
        context.Definition(store).Capability("immutability") == "true"
        || (store.Archetype == Archetypes.ObjectStorage && store.Cfg.Bool("objectLock"));
}

/// <summary>SEC-007 — segredos fora de um cofre.</summary>
public sealed class SecretsRule : Rule
{
    public override string Id => "SEC-007";
    public override Dimension Dimension => Dimension.Security;
    public override Severity DefaultSeverity => Severity.Medium;

    public override IEnumerable<Finding> Evaluate(RuleContext context)
    {
        foreach (var node in context.Graph.Nodes.Where(n => Archetypes.IsCompute(n.Archetype) && n.Cfg.String("secrets") == "env"))
            yield return context.Create(this, $"\"{node.Label}\" guarda segredos em variáveis de ambiente: use o cofre do provedor, com rotação.", [node.Id]);
    }
}
