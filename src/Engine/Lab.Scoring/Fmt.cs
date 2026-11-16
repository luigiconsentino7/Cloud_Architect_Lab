using System.Globalization;

namespace Lab.Scoring;

/// <summary>Formatação pt-BR dos números citados na revisão e nas verificações.</summary>
public static class Fmt
{
    public static CultureInfo PtBr { get; } = CultureInfo.GetCultureInfo("pt-BR");

    public static string N0(double value) => value.ToString("N0", PtBr);

    public static string N1(double value) => value.ToString("N1", PtBr);

    public static string Ms(double value) => value >= 100 ? $"{N0(value)} ms" : $"{value.ToString("0.#", PtBr)} ms";

    /// <summary>Fração como porcentagem, com casas suficientes para distinguir 99,9% de 99,95%.</summary>
    public static string Pct(double fraction)
    {
        var percent = fraction * 100;
        var format = percent >= 99 && percent < 100 ? "0.###" : "0.#";
        return percent.ToString(format, PtBr) + "%";
    }

    public static string Usd(double value) => "US$ " + (value >= 100 ? value.ToString("N0", PtBr) : value.ToString("N2", PtBr));

    public static string Points(double value) => value.ToString("0.#", PtBr);

    public static string MetricName(string metric) => metric switch
    {
        "latency.p50" => "p50 de latência",
        "latency.p95" => "p95 de latência",
        "latency.p99" => "p99 de latência",
        "success-rate" => "taxa de sucesso",
        "async-completion.p95" => "p95 do processamento assíncrono",
        "async-completion.p99" => "p99 do processamento assíncrono",
        "duplicate-effects" => "efeitos duplicados",
        "lost-events" => "eventos perdidos",
        "ordering-violations" => "violações de ordem",
        "capacity.headroom" => "utilização do componente mais ocupado",
        _ => metric,
    };

    public static string MetricValue(string metric, double value) => metric switch
    {
        _ when metric.StartsWith("latency.", StringComparison.Ordinal) || metric.StartsWith("async-completion.", StringComparison.Ordinal) => Ms(value),
        "success-rate" or "capacity.headroom" => Pct(value),
        _ => N0(value),
    };
}
