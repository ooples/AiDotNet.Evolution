using Xunit;
using static AiDotNet.Evolution.CSharp.Tests.CompilerTestSupport;

namespace AiDotNet.Evolution.CSharp.Tests;

/// <summary>V1-83 (#185), D8: each <see cref="CSharpProgramSourceOptions"/> member set both ways and its effect observed.</summary>
public sealed class CSharpSourceOptionBehaviourTests
{
    private const string Marked =
        "public static class C { public static int F() {\n// <<evolve\nint x = 1;\n// evolve>>\nint y = 10;\nreturn x + y;\n} }";

    [Fact]
    public void MaxProgramChars_bounds_the_parent_and_the_candidate()
    {
        CSharpProgramSourceOptions Bounded(int chars)
        {
            CSharpProgramSourceOptions program = Program();
            program.MaxProgramChars = chars;
            return program;
        }

        Assert.Throws<ArgumentException>(() => new CSharpPatchCompiler(Options(), Bounded(Source.Length - 1)).Prepare(Parent(), default));

        // At exactly the parent's length, a one-character-longer candidate is refused; one more character admits it.
        var tight = new CSharpPatchCompiler(Options(), Bounded(Source.Length));
        CSharpPatchPreparation prepared = tight.Prepare(Parent(), default);
        Assert.Null(tight.Apply(prepared, Patch(prepared, "12"), default).Candidate);
        var roomy = new CSharpPatchCompiler(Options(), Bounded(Source.Length + 1));
        CSharpPatchPreparation roomyPrepared = roomy.Prepare(Parent(), default);
        Assert.NotNull(roomy.Apply(roomyPrepared, Patch(roomyPrepared, "12"), default).Candidate);
    }

    [Fact]
    public void EvolveBlockStartMarker_and_EvolveBlockEndMarker_each_define_the_editable_region()
    {
        CSharpProgramSourceOptions Markers(string? start, string? end)
        {
            CSharpProgramSourceOptions program = Program(enforce: true);
            program.EvolveBlockStartMarker = start;
            program.EvolveBlockEndMarker = end;
            return program;
        }

        // Both custom markers: only the literal between them is editable.
        CSharpPatchPreparation prepared = new CSharpPatchCompiler(Options(), Markers("// <<evolve", "// evolve>>")).Prepare(Parent(Marked), default);
        Assert.Contains(prepared.Targets, target => target.Preview == "1");
        Assert.DoesNotContain(prepared.Targets, target => target.Preview == "10");

        // Either marker left at its default no longer matches this source, so the block cannot be found.
        Assert.Throws<ArgumentException>(() => new CSharpPatchCompiler(Options(), Markers("// <<evolve", null)).Prepare(Parent(Marked), default));
        Assert.Throws<ArgumentException>(() => new CSharpPatchCompiler(Options(), Markers(null, "// evolve>>")).Prepare(Parent(Marked), default));
    }

    [Fact]
    public async Task TaskDescription_reaches_the_proposal_prompt()
    {
        const string Task = "Minimise the arithmetic in F without changing its result (task-description-probe).";
        async Task<string> Prompt(string? description)
        {
            CSharpProgramSourceOptions program = Program();
            program.TaskDescription = description;
            var client = new ScriptedClient();
            EvolutionResourceLedger ledger = Ledger(1_000_000m);
            CSharpProposalSource source = CSharpProposalSource.Create(client, Options(), program, ledger);
            await new ResourceMeteredVariationOperator<Programs.ProgramGenome>(
                source, ledger, source.MaximumProposalResources, source.CostUnitVersionHash).ProposeAsync(Context());
            return string.Join("\n", client.Conversations[0].Select(message => message.Text));
        }

        Assert.Contains(Task, await Prompt(Task), StringComparison.Ordinal);
        Assert.DoesNotContain("task-description-probe", await Prompt(null), StringComparison.Ordinal);
    }
}