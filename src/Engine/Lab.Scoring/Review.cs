using Lab.Cost;
using Lab.Ir;
using Lab.Ir.Catalog;
using Lab.Ir.Challenges;
using Lab.Ir.Content;
using Lab.Rules;
using Lab.Rules.Functional;
using Lab.Simulation;

namespace Lab.Scoring;

public enum ReviewItemKind
{
    Rule,
    Scenario,
    Functional,
    Cost,
}

/// <summary>Ponto de melhoria: o problema, a evidência (com números da simulação) e o que fazer.</summary>
public sealed record ReviewItem
{
    public required string Id { get; init; }
    public required ReviewItemKind Kind { get; init; }
    public required string Title { get; init; }
    public Severity Severity { get; init; } = Severity.Medium;
    public Dimension Dimension { get; init; }

    /// <summary>Pontos da nota final perdidos por causa deste item (estimativa para ordenar).</summary>
    public double Impact { get; init; }

    public bool CausesCap { get; init; }
    public IReadOnlyList<string> Details { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public string? Why { get; init; }
    public string? How { get; init; }
    public string? Tradeoffs { get; init; }
    public string? RuleId { get; init; }
    public bool Confirmed { get; init; }
    public IReadOnlyList<string> ScenarioIds { get; init; } = [];
    public IReadOnlyList<string> CheckIds { get; init; } = [];
    public IReadOnlyList<string> NodeIds { get; init; } = [];
    public IReadOnlyList<string> EdgeIds { get; init; } = [];
}

public sealed record Strength(string Title, string Detail, IReadOnlyList<string> NodeIds, IReadOnlyList<string> EdgeIds);

public sealed record SummaryLine(string ItemId, string Text, double Impact);

public sealed record ReferenceComparison(
    IReadOnlyList<string> PatternsMissing,
    IReadOnlyList<string> PatternsExtra,
    IReadOnlyList<string> PatternsShared,
    IReadOnlyList<string> ComponentsMissing,
    IReadOnlyList<string> ComponentsExtra,
    double? ReferenceCostUsd,
    string Rationale);

public sealed record StudyItem(string Topic, IReadOnlyList<LinkRef> Links, IReadOnlyList<string> RuleIds);

/// <summary>Revisão determinística (spec 05, Etapa 6). A narrativa por IA, quando ligada, só pode citar o que está aqui.</summary>
public sealed record Review
{
    public IReadOnlyList<SummaryLine> Summary { get; init; } = [];
    public IReadOnlyList<ReviewItem> Improvements { get; init; } = [];
    public IReadOnlyList<Strength> Strengths { get; init; } = [];
    public IReadOnlyList<string> FollowUps { get; init; } = [];
    public ReferenceComparison? Reference { get; init; }
    public IReadOnlyList<StudyItem> Study { get; init; } = [];
}

public sealed record ReviewInput
{
    public required ChallengeSpec Challenge { get; init; }
    public required ArchitectureGraph Graph { get; init; }
    public required ComponentCatalog Catalog { get; init; }
    public required ScoreResult Score { get; init; }
    public required IReadOnlyList<UseCaseFunctionalResult> Functional { get; init; }
    public required SimulationReport Simulation { get; init; }
    public required CostReport Cost { get; init; }
    public RuleTextLibrary RuleTexts { get; init; } = RuleTextLibrary.Empty;
    public double? ReferenceCostUsd { get; init; }
}

public static class ReviewBuilder
{
    private const int MaxFollowUps = 5;
    private const int MaxStrengths = 10;

    public static Review Build(ReviewInput input) => EngineCulture.Run(() => BuildCore(input));

    private static Review BuildCore(ReviewInput input)
    {
        var context = new Context(input);
        var items = context.Items();
        var summary = items.Take(3)
            .Select(i => new SummaryLine(i.Id, i.Evidence.Count > 0 ? $"{i.Title} — {i.Evidence[0]}" : i.Title, i.Impact))
            .ToList();
        return new Review
        {
            Summary = summary,
            Improvements = items,
            Strengths = context.Strengths(),
            FollowUps = context.FollowUps(items),
            Reference = context.Reference(),
            Study = context.Study(),
        };
    }

    private sealed class Context(ReviewInput input)
    {
        private readonly ChallengeSpec _challenge = input.Challenge;
        private readonly ScoreResult _score = input.Score;
        private readonly double _totalWeight = input.Challenge.Weights.Total <= 0 ? 100 : input.Challenge.Weights.Total;

        public List<ReviewItem> Items()
        {
            var items = new List<ReviewItem>();
            var failed = _score.Checks.Where(c => !c.Passed).ToList();
            var explained = new HashSet<string>(StringComparer.Ordinal);
            var capChecks = new HashSet<string>(_score.Cap?.CheckIds ?? [], StringComparer.Ordinal);

            // Regras: um item por regra, com a evidência dos cenários que a confirmaram.
            foreach (var group in _score.Findings.GroupBy(a => a.Finding.RuleId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var penalty = _score.RulePenalties.First(p => p.RuleId == group.Key);
                var first = group.First().Finding;
                var text = input.RuleTexts.Find(group.Key);
                var confirmedIn = group.Where(a => a.Confirmed).SelectMany(a => a.ScenarioIds).ToHashSet(StringComparer.Ordinal);
                var observedIn = group.Where(a => a.Observed).SelectMany(a => a.ScenarioIds).ToHashSet(StringComparer.Ordinal);
                var nodes = group.SelectMany(a => a.Finding.NodeIds).Distinct(StringComparer.Ordinal).ToList();
                var edges = group.SelectMany(a => a.Finding.EdgeIds).Distinct(StringComparer.Ordinal).ToList();
                var explainedChecks = failed
                    .Where(c => confirmedIn.Contains(c.ScenarioId) && group.Any(a => EvidenceMatcher.Explains(a.Finding, c)))
                    .ToList();
                foreach (var check in explainedChecks) explained.Add(check.Id);

                var evidence = new List<string>();
                foreach (var scenarioId in confirmedIn.Concat(observedIn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    evidence.AddRange(ScenarioEvidence(scenarioId, explainedChecks.Where(c => c.ScenarioId == scenarioId), nodes));

                items.Add(new ReviewItem
                {
                    Id = $"rule:{group.Key}",
                    Kind = ReviewItemKind.Rule,
                    Title = text?.Title ?? $"{group.Key}: {first.Message}",
                    Severity = penalty.Severity,
                    Dimension = penalty.Dimension,
                    Impact = (DimensionWeight(penalty.Dimension) / _totalWeight * penalty.Points) + SharedImpact(explainedChecks, group.Key, failed, confirmedIn),
                    CausesCap = explainedChecks.Any(c => capChecks.Contains(c.Id)),
                    Details = group.Select(a => a.Finding.Message).Distinct(StringComparer.Ordinal).ToList(),
                    Evidence = evidence,
                    Why = Blank(text?.Why),
                    How = Blank(text?.How),
                    Tradeoffs = Blank(text?.Tradeoffs),
                    RuleId = group.Key,
                    Confirmed = confirmedIn.Count > 0,
                    ScenarioIds = confirmedIn.Concat(observedIn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    CheckIds = explainedChecks.Select(c => c.Id).ToList(),
                    NodeIds = nodes,
                    EdgeIds = edges,
                });
            }

            // Cenários com verificações reprovadas que nenhuma regra explica.
            foreach (var scenario in failed.Where(c => !explained.Contains(c.Id)).GroupBy(c => c.ScenarioId, StringComparer.Ordinal))
            {
                var checks = scenario.ToList();
                var result = input.Simulation.Find(scenario.Key);
                var bottlenecks = result?.Bottlenecks ?? [];
                var top = bottlenecks.FirstOrDefault();
                items.Add(new ReviewItem
                {
                    Id = $"scenario:{scenario.Key}",
                    Kind = ReviewItemKind.Scenario,
                    Title = $"Cenário \"{checks[0].ScenarioName}\" reprovado",
                    Severity = checks.Any(c => c.MonetaryViolation) ? Severity.Critical : checks.All(c => c.Score > 0.5) ? Severity.Medium : Severity.High,
                    Dimension = checks.GroupBy(c => c.Dimension).OrderByDescending(g => g.Count()).First().Key,
                    Impact = checks.Sum(CheckImpact),
                    CausesCap = checks.Any(c => capChecks.Contains(c.Id)),
                    Details = checks.Select(c => c.Description).ToList(),
                    Evidence = bottlenecks.Take(3).Select(b => b.Detail).ToList(),
                    Why = ScenarioWhy(checks[0]),
                    How = top is null ? null : BottleneckHow(top.Reason),
                    ScenarioIds = [scenario.Key],
                    CheckIds = checks.Select(c => c.Id).ToList(),
                    NodeIds = bottlenecks.Select(b => b.NodeId).Distinct(StringComparer.Ordinal).ToList(),
                });
            }

            // Testes funcionais.
            var functionalWeight = input.Functional.Sum(f => Scorer.UseCaseWeight(_challenge, f.UseCaseId));
            foreach (var result in input.Functional.Where(f => f.Status != FunctionalStatus.Passed))
            {
                var unmet = result.Capabilities.Concat(result.NegativePaths).Where(c => !c.Satisfied).ToList();
                items.Add(new ReviewItem
                {
                    Id = $"functional:{result.UseCaseId}",
                    Kind = ReviewItemKind.Functional,
                    Title = result.PathOk
                        ? $"{result.UseCaseId} {result.Name}: falta cumprir {unmet.Count} requisito(s) do caso de uso"
                        : $"{result.UseCaseId} {result.Name}: o fluxo não chega ao fim",
                    Severity = result.PathOk ? Severity.High : Severity.Critical,
                    Dimension = Dimension.Functional,
                    Impact = DimensionWeight(Dimension.Functional) / _totalWeight * 100
                        * Scorer.UseCaseWeight(_challenge, result.UseCaseId) * (1 - result.Score) / Math.Max(1, functionalWeight),
                    CausesCap = !result.PathOk,
                    Details = result.PathProblems.Concat(unmet.Select(c => c.Detail)).ToList(),
                    How = result.PathOk
                        ? "Inclua no fluxo os passos que cumprem cada requisito (ex.: checar idempotência antes de debitar) e configure as conexões de falha pedidas."
                        : "Defina o fluxo do caso de uso na aba Fluxos: comece no cliente e siga as setas até a resposta, sem pular componentes.",
                    NodeIds = unmet.SelectMany(c => c.NodeIds).Distinct(StringComparer.Ordinal).ToList(),
                    EdgeIds = unmet.SelectMany(c => c.EdgeIds).Distinct(StringComparer.Ordinal).ToList(),
                });
            }

            // Custo.
            var cost = input.Cost;
            if (_score.CostScore < 100)
            {
                var overBudget = cost.BudgetUsd > 0 && cost.MonthlyUsd > cost.BudgetUsd;
                var biggest = cost.Lines.OrderByDescending(l => l.MonthlyUsd).Take(3).ToList();
                items.Add(new ReviewItem
                {
                    Id = "cost",
                    Kind = ReviewItemKind.Cost,
                    Title = overBudget ? "Custo acima do orçamento" : "Custo bem acima da solução de referência",
                    Severity = overBudget ? Severity.High : Severity.Medium,
                    Dimension = Dimension.Cost,
                    Impact = DimensionWeight(Dimension.Cost) / _totalWeight * (100 - _score.CostScore),
                    Details = [overBudget
                        ? $"{Fmt.Usd(cost.MonthlyUsd)}/mês para um orçamento de {Fmt.Usd(cost.BudgetUsd)} ({Fmt.Pct(cost.BudgetUsage)})."
                        : $"{Fmt.Usd(cost.MonthlyUsd)}/mês contra {Fmt.Usd(input.ReferenceCostUsd ?? 0)}/mês da referência."],
                    Evidence = biggest.Select(l => $"{l.Label}: {Fmt.Usd(l.MonthlyUsd)}/mês ({l.Basis})").ToList(),
                    How = "Ajuste o mínimo de instâncias à carga de baseline e deixe o autoscaling cobrir o pico; prefira cobrança por uso onde a carga oscila e revise classes de banco superdimensionadas.",
                    NodeIds = biggest.Select(l => l.NodeId).Where(id => input.Graph.FindNode(id) is not null).ToList(),
                });
            }

            // A nota da dimensão não fica abaixo de 0: perdas brutas acima disso são reescaladas para somar a perda real.
            items = items.Select(i => i with { Impact = i.Impact * ClampFactor(i.Dimension) }).ToList();

            // A trava: o excesso acima dela é atribuído aos itens que a causaram.
            if (_score.Cap is { Applied: true } cap)
            {
                var causes = items.Where(i => i.CausesCap).ToList();
                var extra = (_score.BaseScore - cap.Value) / Math.Max(1, causes.Count);
                items = items.Select(i => i.CausesCap ? i with { Impact = i.Impact + extra } : i).ToList();
            }

            return items
                .Select(i => i with { Impact = Math.Round(i.Impact, 1, MidpointRounding.AwayFromZero) })
                .OrderByDescending(i => i.CausesCap)
                .ThenByDescending(i => i.Impact)
                .ThenBy(i => i.Severity)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
        }

        public List<Strength> Strengths()
        {
            var strengths = new List<Strength>();
            if (input.Functional.Count > 0 && input.Functional.All(f => f.Status == FunctionalStatus.Passed))
                strengths.Add(new Strength("Todos os casos de uso funcionam de ponta a ponta",
                    $"{input.Functional.Count} caso(s) de uso com caminho, requisitos e caminhos de falha atendidos.", [], []));

            var tested = _challenge.TestPlan.Scenarios
                .Select(s => (Spec: s, Checks: _score.Checks.Where(c => c.ScenarioId == s.Id && c.SloId != "HEADROOM").ToList()))
                .Where(s => s.Checks.Count > 0)
                .ToList();
            var passed = tested.Where(s => s.Checks.All(c => c.Passed)).ToList();
            if (passed.Count > 0 && passed.Count == tested.Count)
            {
                var hidden = passed.Count(s => s.Spec.Visibility == Visibility.Hidden);
                strengths.Add(new Strength(
                    hidden > 0 ? $"Todos os {passed.Count} cenários aprovados, inclusive {hidden} oculto(s)" : $"Todos os {passed.Count} cenários aprovados",
                    string.Join(", ", passed.Select(s => ScenarioName(s.Spec))) + ".", [], []));
            }
            else
            {
                foreach (var (spec, checks) in passed)
                {
                    var hidden = spec.Visibility == Visibility.Hidden ? " (cenário oculto)" : "";
                    strengths.Add(new Strength($"Cenário \"{ScenarioName(spec)}\" aprovado{hidden}",
                        string.Join("; ", checks.Take(3).Select(c => c.Description)) + ".", [], []));
                }
            }

            foreach (var pattern in PatternDetector.Detect(input.Graph).Where(p => PatternDetector.IsStrength(p.Id)))
                strengths.Add(new Strength(pattern.Name, $"Bem aplicado: {pattern.Detail}.", pattern.NodeIds, pattern.EdgeIds));

            if (_score.CostScore >= 100 && input.Cost.BudgetUsd > 0)
                strengths.Add(new Strength("Custo sob controle",
                    $"{Fmt.Usd(input.Cost.MonthlyUsd)}/mês, {Fmt.Pct(input.Cost.BudgetUsage)} do orçamento.", [], []));

            if (_score.For(Dimension.Security) is { Score: >= 100 })
                strengths.Add(new Strength("Segurança e compliance sem achados", "Nenhuma regra de segurança disparou para o perfil deste desafio.", [], []));

            return strengths.Take(MaxStrengths).ToList();
        }

        public List<string> FollowUps(IReadOnlyList<ReviewItem> items)
        {
            var questions = new List<string>();
            foreach (var item in items)
            {
                if (item.RuleId is not null && input.RuleTexts.Find(item.RuleId)?.FollowUps.FirstOrDefault() is { } question)
                    questions.Add(question);
                foreach (var scenarioId in item.ScenarioIds)
                {
                    if (_challenge.TestPlan.Find(scenarioId) is { } scenario && ScenarioQuestion(_challenge, scenario) is { } q)
                        questions.Add(q);
                }
            }

            return questions.Distinct(StringComparer.Ordinal).Take(MaxFollowUps).ToList();
        }

        public ReferenceComparison? Reference()
        {
            if (_challenge.Reference is not { } reference) return null;
            var mine = PatternDetector.Detect(input.Graph).ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
            var theirs = PatternDetector.Detect(reference).ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
            var myCounts = Counts(input.Graph);
            var referenceCounts = Counts(reference);
            var missing = referenceCounts
                .Where(p => myCounts.GetValueOrDefault(p.Key) < p.Value)
                .Select(p => myCounts.ContainsKey(p.Key) ? $"{ArchetypeName(p.Key)} (a referência usa {p.Value})" : ArchetypeName(p.Key))
                .Order(StringComparer.Ordinal)
                .ToList();
            var extra = myCounts.Keys.Where(k => !referenceCounts.ContainsKey(k)).Select(ArchetypeName).Order(StringComparer.Ordinal).ToList();
            return new ReferenceComparison(
                theirs.Where(p => !mine.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.Ordinal).ToList(),
                mine.Where(p => !theirs.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.Ordinal).ToList(),
                mine.Where(p => theirs.ContainsKey(p.Key)).Select(p => p.Value).Order(StringComparer.Ordinal).ToList(),
                missing,
                extra,
                input.ReferenceCostUsd,
                _challenge.ReferenceRationale);
        }

        public List<StudyItem> Study()
        {
            var topics = new SortedDictionary<string, (List<LinkRef> Links, SortedSet<string> Rules)>(StringComparer.Ordinal);
            foreach (var ruleId in _score.RulePenalties.Select(p => p.RuleId))
            {
                if (input.RuleTexts.Find(ruleId) is not { } text) continue;
                foreach (var topic in text.StudyTopics)
                {
                    if (!topics.TryGetValue(topic, out var entry))
                    {
                        entry = ([], new SortedSet<string>(StringComparer.Ordinal));
                        topics[topic] = entry;
                    }

                    entry.Rules.Add(ruleId);
                    foreach (var link in text.Links.Where(l => entry.Links.All(e => e.Url != l.Url))) entry.Links.Add(link);
                }
            }

            return topics.Select(t => new StudyItem(t.Key, t.Value.Links, t.Value.Rules.ToList())).ToList();
        }

        private double DimensionWeight(Dimension dimension) => Scorer.WeightOf(_challenge.Weights, dimension);

        /// <summary>Perda real da dimensão ÷ perda bruta (verificações + penalidades): &lt; 1 quando a nota bateu no zero.</summary>
        private double ClampFactor(Dimension dimension)
        {
            if (_score.For(dimension) is not { } d) return 1;
            var raw = (100 - d.BaseScore) + d.Penalty;
            var actual = 100 - d.Score;
            return raw <= 0 || actual >= raw ? 1 : Math.Max(0, actual / raw);
        }

        /// <summary>
        /// Pontos da nota final perdidos por uma verificação reprovada: a perda do cenário na dimensão, dividida entre
        /// as verificações reprovadas dele na proporção do quanto cada uma ficou abaixo do alvo.
        /// </summary>
        private double CheckImpact(CheckResult check)
        {
            if (_score.For(check.Dimension) is not { } dimension) return 0;
            var scenario = dimension.Scenarios.FirstOrDefault(s => s.CheckIds.Contains(check.Id));
            var totalWeight = dimension.Scenarios.Sum(s => s.Weight);
            if (scenario is null || totalWeight <= 0) return 0;
            var scenarioLoss = DimensionWeight(check.Dimension) / _totalWeight * 100 * scenario.Weight * (1 - scenario.Score) / totalWeight;
            var shortfall = _score.Checks.Where(c => scenario.CheckIds.Contains(c.Id) && !c.Passed).Sum(c => 1 - c.Score);
            return shortfall <= 0 ? 0 : scenarioLoss * (1 - check.Score) / shortfall;
        }

        /// <summary>Parte do impacto das verificações explicadas por esta regra (dividida entre as regras que as explicam).</summary>
        private double SharedImpact(IEnumerable<CheckResult> explainedChecks, string ruleId, IReadOnlyList<CheckResult> failed, IReadOnlySet<string> confirmedIn)
        {
            var total = 0.0;
            foreach (var check in explainedChecks)
            {
                var explainers = _score.Findings
                    .Where(a => a.Confirmed && a.ScenarioIds.Contains(check.ScenarioId) && EvidenceMatcher.Explains(a.Finding, check))
                    .Select(a => a.Finding.RuleId)
                    .Distinct(StringComparer.Ordinal)
                    .Count();
                total += CheckImpact(check) / Math.Max(1, explainers);
            }

            return total;
        }

        private IEnumerable<string> ScenarioEvidence(string scenarioId, IEnumerable<CheckResult> checks, IReadOnlyList<string> nodes)
        {
            var spec = _challenge.TestPlan.Find(scenarioId);
            var name = spec is null || string.IsNullOrWhiteSpace(spec.Name) ? scenarioId : spec.Name;
            foreach (var check in checks) yield return $"{name}: {check.Description}";
            if (input.Simulation.Find(scenarioId) is not { } result) yield break;

            var related = result.Bottlenecks.Where(b => nodes.Contains(b.NodeId)).ToList();
            foreach (var bottleneck in (related.Count > 0 ? related : result.Bottlenecks.ToList()).Take(2))
                yield return $"{name}: {bottleneck.Detail}";

            var integrity = result.Integrity;
            if (integrity.DuplicateEffects > 0)
                yield return $"{name}: {Fmt.N0(integrity.DuplicateEffects)} efeito(s) duplicado(s), {Fmt.N0(integrity.MonetaryDuplicates)} monetário(s)";
            if (integrity.LostEvents > 0) yield return $"{name}: {Fmt.N0(integrity.LostEvents)} evento(s) perdido(s)";
            if (integrity.OrderingViolations > 0) yield return $"{name}: {Fmt.N0(integrity.OrderingViolations)} violação(ões) de ordem";
        }

        private Dictionary<string, int> Counts(ArchitectureGraph graph) => graph.Nodes
            .Where(n => !Archetypes.IsClient(n.Archetype) && !Archetypes.IsExternal(n.Archetype))
            .GroupBy(n => n.Archetype, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        private string ArchetypeName(string archetype) =>
            input.Catalog.FindArchetype(archetype)?.DisplayName is { Length: > 0 } name ? name : archetype;

        private static string ScenarioName(ScenarioSpec spec) => string.IsNullOrWhiteSpace(spec.Name) ? spec.Id : spec.Name;

        private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string ScenarioWhy(CheckResult check) => check.ScenarioKind switch
    {
        ScenarioKind.Load => "Em produção, esse volume acontece: quem não aguenta o pico derruba justamente as transações de maior movimento.",
        ScenarioKind.Chaos => "Falhas de infraestrutura e de dependências são rotina; o desenho precisa degradar com controle em vez de cair junto.",
        _ => "Duplicar ou perder um efeito num sistema financeiro vira prejuízo, reclamação e problema regulatório.",
    };

    private static string BottleneckHow(string reason) => reason switch
    {
        "concurrency-limit" => "Aumente o limite de concorrência (cota da conta, concorrência reservada ou provisionada) ou use contêineres com autoscaling, que não têm esse teto.",
        "cpu" => "Aumente instâncias ou vCPU, baixe o alvo de CPU do autoscaling ou reduza o trabalho por requisição.",
        "connections" => "Use um proxy ou pool de conexões, ou uma classe de banco com mais conexões: cada execução serverless abre a própria conexão.",
        "db-saturation" => "Escale o banco (classe maior), leve leituras para réplicas ou cache, ou reparta os dados.",
        "consumer-lag" => "Aumente consumidores, partições ou o tamanho do lote, e confira se o consumidor não está preso numa dependência lenta.",
        "failover" => "Um banco com primário único para de aceitar escritas durante o failover (dezenas de segundos). Para atravessar a queda de uma zona sem interrupção, use um banco ativo-ativo entre zonas (SQL distribuído ou chave-valor regional), ou absorva a janela com fila de pendências e retry com backoff no cliente.",
        "dependency" => "Proteja a chamada com timeout menor que o orçamento de latência, circuit breaker e fallback (fila de pendências), e retry só com backoff e jitter.",
        "throttled" => "Revise cotas e throttling: aumente o limite ou absorva o pico com uma fila.",
        _ => "Veja o gargalo indicado e dê folga de capacidade ao componente.",
    };

    private static string? ScenarioQuestion(ChallengeSpec challenge, ScenarioSpec scenario)
    {
        var fault = scenario.Faults.FirstOrDefault();
        var target = fault?.Target is { } id ? challenge.FindExternal(id)?.Name ?? id : null;
        return fault?.Type switch
        {
            FaultType.ZoneOutage => $"Se a zona {fault.Zone ?? "a"} ficar fora por 30 minutos, quais componentes perdem capacidade e quanto tempo leva para o sistema se recuperar?",
            FaultType.RegionOutage => "Qual é o seu RTO e o seu RPO se a região inteira cair? O que mudaria para chegar a minutos?",
            FaultType.LatencyInjection or FaultType.DependencyOutage =>
                $"O que acontece com as transações em andamento se o {target ?? "sistema externo"} ficar fora por 10 minutos? Como você reconcilia depois?",
            FaultType.DuplicateDelivery or FaultType.ClientRetry =>
                "Como você garante que uma mensagem reentregue ou uma requisição repetida pelo cliente não produz um segundo efeito?",
            FaultType.PublishCrash or FaultType.ConsumerCrash => "Se o processo cair entre gravar no banco e publicar o evento, como o evento chega ao destino?",
            null when scenario.Kind == ScenarioKind.Load => "Qual componente satura primeiro quando a carga multiplica? Como você descobriria isso em produção antes do cliente?",
            _ => null,
        };
    }
}
