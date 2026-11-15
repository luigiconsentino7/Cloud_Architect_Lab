namespace Lab.Ir.Scene;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

public sealed record Suggestion(string CatalogId, string DisplayName);

/// <summary>Problema encontrado na compilação da cena. <c>ElementIds</c> são IDs do Excalidraw; <c>IrIds</c>, da IR.</summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    IReadOnlyList<string> ElementIds,
    IReadOnlyList<string> IrIds,
    IReadOnlyList<Suggestion>? Suggestions = null);

public static class DiagnosticCodes
{
    public const string DanglingArrow = "IR001";
    public const string InvalidConfig = "IR002";
    public const string InvalidInteraction = "IR003";
    public const string UntypedShape = "IR004";
    public const string ForeignProvider = "IR005";
    public const string OutsideRegion = "IR006";
    public const string SyncCycle = "IR007";
    public const string Isolated = "IR008";
    public const string InvalidEdgeConfig = "IR009";
    public const string UnknownComponent = "IR010";
    public const string DuplicateId = "IR011";
    public const string MissingFlowStep = "FLX003";
}
