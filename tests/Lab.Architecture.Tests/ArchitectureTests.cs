using ArchUnitNET.Fluent;
using ArchUnitNET.Fluent.Syntax.Elements.Types;
using ArchUnitNET.Loader;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Lab.Architecture.Tests;

/// <summary>Fronteiras da arquitetura (spec 09): motor puro e módulos que só se falam pelos contratos.</summary>
public sealed class ArchitectureTests
{
    internal static readonly ArchUnitNET.Domain.Architecture Loaded = new ArchLoader().LoadAssemblies(
        typeof(Lab.Ir.ArchitectureGraph).Assembly,
        typeof(Lab.Rules.RuleEngine).Assembly,
        typeof(Lab.Simulation.Simulator).Assembly,
        typeof(Lab.Cost.CostModel).Assembly,
        typeof(Lab.Scoring.Scorer).Assembly,
        typeof(Lab.Pipeline.EvaluationPipeline).Assembly,
        typeof(Lab.Modules.LabModules).Assembly).Build();

    private static readonly string[] EngineNamespaces = ["Lab.Ir", "Lab.Rules", "Lab.Simulation", "Lab.Cost", "Lab.Scoring", "Lab.Pipeline"];

    private static readonly string[] Modules = ["Profile", "Challenges", "Design", "Evaluation", "Gamification"];

    // O builder fluente é mutável: cada regra começa de um GivenTypes novo.
    private static GivenTypesConjunction Engine() =>
        Types().That().ResideInNamespaceMatching($"^({string.Join('|', EngineNamespaces.Select(n => n.Replace(".", "\\.")))})(\\..*)?$");

    [Fact]
    public void Engine_has_no_web_or_database_dependencies()
    {
        Engine().Should().NotDependOnAny(Types().That().ResideInNamespaceMatching("^Microsoft\\.(AspNetCore|EntityFrameworkCore)(\\..*)?$"))
            .Because("o motor é uma função pura: sem web nem banco")
            .Verify();
    }

    [Fact]
    public void Engine_does_no_file_network_or_clock_io()
    {
        Engine().Should().NotDependOnAny(Types().That().HaveFullName("System.IO.File").Or().HaveFullName("System.IO.Directory")
                .Or().HaveFullName("System.Net.Http.HttpClient").Or().HaveFullName("System.DateTime").Or().HaveFullName("System.DateTimeOffset"))
            .Because("o motor não lê arquivos, não faz rede e não usa o relógio do sistema")
            .Verify();
    }

    [Fact]
    public void Modules_only_talk_through_contracts()
    {
        foreach (var module in Modules)
        {
            var others = string.Join('|', Modules.Where(m => m != module));
            Types().That().ResideInNamespaceMatching($"^Lab\\.Modules\\.{module}(\\..*)?$")
                .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching($"^Lab\\.Modules\\.({others})(\\.(?!Contracts$)[A-Za-z.]+)?$"))
                .Because($"{module} só conhece os outros módulos pelos Contracts")
                .Verify();
        }
    }

    [Fact]
    public void Contracts_do_not_depend_on_module_internals()
    {
        Types().That().ResideInNamespaceMatching("^Lab\\.Modules\\.[A-Za-z]+\\.Contracts$")
            .Should().NotDependOnAny(Types().That().ResideInNamespaceMatching($"^Lab\\.Modules\\.({string.Join('|', Modules)})$"))
            .Verify();
    }
}

internal static class ArchRuleExtensions
{
    public static void Verify(this IArchRule rule)
    {
        var violations = rule.Evaluate(ArchitectureTests.Loaded).Where(r => !r.Passed).Select(r => r.Description).ToList();
        violations.ShouldBeEmpty(rule.Description);
    }
}
