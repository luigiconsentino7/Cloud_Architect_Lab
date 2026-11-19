using Lab.Engine.Tests.Support;
using Lab.Ir.Scene;
using Lab.Rules;
using Lab.Rules.Functional;

namespace Lab.Engine.Tests.Rules;

public sealed class FunctionalVerifierTests
{
    private static IReadOnlyList<UseCaseFunctionalResult> Verify(Design design)
    {
        var challenge = Pix.Challenge();
        var compiled = Pix.Compile(design, challenge);
        return FunctionalVerifier.Verify(new RuleContext(compiled.Graph, challenge, TestContent.Get().Catalog));
    }

    [Fact]
    public void Solid_design_passes_every_use_case()
    {
        var results = Verify(Pix.Good());

        foreach (var result in results)
        {
            result.Status.ShouldBe(FunctionalStatus.Passed,
                string.Join("; ", result.PathProblems.Concat(result.Capabilities.Concat(result.NegativePaths).Where(c => !c.Satisfied).Select(c => c.Detail))));
            result.Score.ShouldBe(1);
        }
    }

    [Fact]
    public void Missing_fallback_makes_the_use_case_partial()
    {
        var uc1 = Verify(Pix.Bad()).Single(r => r.UseCaseId == "UC-1");

        uc1.Status.ShouldBe(FunctionalStatus.Partial);
        uc1.NegativePaths.Single().Satisfied.ShouldBeFalse();
        uc1.Capabilities.Where(c => !c.Satisfied).Select(c => c.Capability).ShouldBeEmpty();
    }

    [Fact]
    public void Use_case_without_flow_fails()
    {
        var design = Pix.Good() with { Flows = [new FlowDefinition("UC-1", Pix.Good().Flows[0].Steps)] };

        var uc2 = Verify(design).Single(r => r.UseCaseId == "UC-2");

        uc2.Status.ShouldBe(FunctionalStatus.Failed);
        uc2.Score.ShouldBe(0);
        uc2.PathProblems.ShouldContain(p => p.Contains("não tem fluxo"));
    }

    [Fact]
    public void Disconnected_step_is_a_path_problem()
    {
        var design = Pix.Good() with
        {
            Flows = [new FlowDefinition("UC-1", ["e01", "e02", "e03", "e08"]), Pix.Good().Flows[1]],
        };

        var uc1 = Verify(design).Single(r => r.UseCaseId == "UC-1");

        uc1.PathOk.ShouldBeFalse();
        uc1.PathProblems.ShouldContain(p => p.Contains("desconectado"));
    }

    [Fact]
    public void Plain_write_does_not_count_as_atomic_balance_update()
    {
        var design = Pix.Good();
        var scene = design.Scene.DeepClone().AsObject();
        foreach (var element in scene["elements"]!.AsArray())
        {
            if (element!["customData"]?["connectionId"]?.GetValue<string>() == "e04")
                element["customData"]!["config"] = System.Text.Json.Nodes.JsonNode.Parse("""{ "ops": [ { "op": "write", "count": 1 } ] }""");
        }

        var uc1 = Verify(design with { Scene = scene }).Single(r => r.UseCaseId == "UC-1");

        uc1.Capabilities.Single(c => c.Capability == "atomic-balance-update").Satisfied.ShouldBeFalse();
        uc1.Capabilities.Single(c => c.Capability == "idempotency-check").Satisfied.ShouldBeTrue(); // o notificador faz escrita condicional
    }
}
