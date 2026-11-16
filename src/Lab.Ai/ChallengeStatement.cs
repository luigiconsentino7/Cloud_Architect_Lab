using System.Globalization;
using System.Text;
using Lab.Ir;
using Lab.Ir.Challenges;

namespace Lab.Ai;

/// <summary>
/// A parte pública do desafio em texto corrido (pt-BR), como o entrevistador a recebe: só os cenários e SLOs públicos,
/// sem a referência e com a contagem (não os nomes) dos cenários ocultos.
/// </summary>
public static class ChallengeStatement
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Write(ChallengeSpec spec)
    {
        var publicScenarios = spec.TestPlan.Scenarios.Where(s => s.Visibility == Visibility.Public).ToList();
        var publicIds = publicScenarios.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var view = new
        {
            spec.Summary,
            spec.Narrative,
            spec.Provider,
            spec.Region,
            spec.Difficulty,
            spec.Mode,
            spec.UseCases,
            spec.ExternalSystems,
            spec.Workload,
            Slos = spec.TestPlan.Slos.Where(s => s.Scenarios.Any(publicIds.Contains)).ToList(),
            Scenarios = publicScenarios,
            HiddenScenarios = spec.TestPlan.Scenarios.Count - publicScenarios.Count,
            spec.Constraints,
        };
        var text = new StringBuilder();
        Line(text, $"Resumo: {view.Summary}");
        Line(text, $"Enunciado: {view.Narrative}");
        Line(text, $"Provedor: {view.Provider.DisplayName()}; região: {view.Region}; dificuldade: {view.Difficulty.ToString().ToLowerInvariant()}; modo: {view.Mode.ToString().ToLowerInvariant()}.");

        if (view.UseCases.Count > 0)
        {
            text.AppendLine("Casos de uso:");
            foreach (var uc in view.UseCases)
            {
                var flags = new List<string>();
                if (uc.Critical) flags.Add("crítico");
                if (uc.Monetary) flags.Add("movimenta dinheiro");
                if (uc.RequiresOrdering) flags.Add("exige ordem por chave");
                if (uc.ReadYourWrites) flags.Add("leitura logo após a escrita");
                var share = view.Workload.Mix.TryGetValue(uc.Id, out var load) ? $"; {N(load.Share * 100)}% do tráfego" : "";
                Line(text, $"- {uc.Name}: {uc.Description}{(flags.Count > 0 ? $" ({string.Join(", ", flags)})" : "")}{share}");
            }
        }

        if (view.ExternalSystems.Count > 0)
        {
            text.AppendLine("Sistemas externos:");
            foreach (var ext in view.ExternalSystems)
            {
                var limit = ext.RateLimitRps is { } rps ? $"; limite de {N(rps)} req/s" : "";
                var about = string.IsNullOrWhiteSpace(ext.Description) ? "" : $"{ext.Description.Trim().TrimEnd('.')}. ";
                Line(text, $"- {ext.Name}: {about}Latência p50 {N(ext.Latency.P50)} ms e p99 {N(ext.Latency.P99)} ms; disponibilidade {N(ext.Availability * 100)}%{limit}.");
            }
        }

        var w = view.Workload;
        Line(text, $"Carga: {N(w.BaselineRps)} req/s no horário normal; pico de {N(w.DailyPattern.PeakMultiplier)}x por {N(w.DailyPattern.PeakHoursPerDay)} h por dia; " +
            $"timeout do cliente de {N(w.ClientTimeoutMs)} ms; {N(w.Keys)} chaves; {N(w.StoredGb)} GB armazenados.");

        if (view.Slos.Count > 0)
        {
            text.AppendLine("SLOs:");
            foreach (var slo in view.Slos)
            {
                var bound = slo.Max is { } max ? $"no máximo {N(max)}" : slo.Min is { } min ? $"no mínimo {N(min)}" : "";
                Line(text, $"- {(string.IsNullOrWhiteSpace(slo.Description) ? $"{slo.Metric} {bound}" : slo.Description)}");
            }
        }

        if (view.Scenarios.Count > 0)
        {
            text.AppendLine("Cenários de teste públicos:");
            foreach (var scenario in view.Scenarios)
                Line(text, string.IsNullOrWhiteSpace(scenario.Description) ? $"- {scenario.Name}" : $"- {scenario.Name}: {scenario.Description}");
        }

        if (view.HiddenScenarios > 0) Line(text, $"Outros {view.HiddenScenarios} cenários ficam ocultos até a submissão.");

        var c = view.Constraints;
        var constraints = new List<string> { $"orçamento de US$ {N(c.BudgetUsdMonth)} por mês" };
        if (c.DataResidencyBrazil) constraints.Add("dados no Brasil");
        if (c.RequireEncryptionAtRest) constraints.Add("criptografia em repouso");
        if (c.RequireAuditLog) constraints.Add("trilha de auditoria");
        if (c.RtoSeconds is { } rto) constraints.Add($"RTO de {N(rto)} s");
        if (c.RpoSeconds is { } rpo) constraints.Add($"RPO de {N(rpo)} s");
        if (c.Compliance.Count > 0) constraints.Add($"conformidade: {string.Join(", ", c.Compliance)}");
        Line(text, $"Restrições: {string.Join("; ", constraints)}.");
        return text.ToString().TrimEnd();
    }

    private static string N(double value) => value.ToString("#,0.##", PtBr);

    private static void Line(StringBuilder text, string line) => text.AppendLine(line);
}
