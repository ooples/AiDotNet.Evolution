using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests;

public sealed class ProgramFoundationAdversarialTests
{
    private static readonly EvolutionEvaluationContext Context = new(0, 1234UL, 7UL, 1);
    private static EvolutionCandidate<ProgramGenome> Candidate(string source, ProgramLanguage language = ProgramLanguage.Generic) =>
        new(0, new EvolutionCanonicalGenome<ProgramGenome>(new ProgramGenome(source, language), ProgramGenome.ComputeId(source, language)),
            new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL));

    [Theory]
    [InlineData("x = 1\n")]
    [InlineData("# EVOLVE-BLOCK-START\nx = 1\n")]
    [InlineData("# EVOLVE-BLOCK-END\nx = 1\n")]
    public void Enforced_edits_fail_closed_without_complete_markers(string source)
    {
        var result = ProgramDiff.Apply(source, new[] { new ProgramDiffBlock("x = 1", "x = 2") },
            new ProgramTaskOptions { EnforceEvolveBlocks = true, Language = ProgramLanguage.Python });
        Assert.False(result.IsSuccess);
        Assert.Equal(source, result.ModifiedSource);
        Assert.Equal(0, result.AppliedCount);
        Assert.Contains(result.Failures, failure => failure.Reason == ProgramDiffFailureReason.OutsideEvolveBlock);
    }

    [Theory]
    [InlineData("# EVOLVE-BLOCK-END\nescaped = 1\n# EVOLVE-BLOCK-START")]
    [InlineData("# EVOLVE-BLOCK-END")]
    public void Replacement_cannot_inject_or_damage_markers(string replacement)
    {
        const string source = "protected = 0\r\n# EVOLVE-BLOCK-START\nx = 1\r# EVOLVE-BLOCK-END\nlast = 3";
        var result = ProgramDiff.Apply(source, new[] { new ProgramDiffBlock("x = 1", replacement) },
            new ProgramTaskOptions { EnforceEvolveBlocks = true, Language = ProgramLanguage.Python });
        Assert.False(result.IsSuccess);
        Assert.Equal(source, result.ModifiedSource);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Edits_preserve_every_unmatched_character_including_mixed_terminators(bool enforce)
    {
        const string source = "protected = '''a  \rb\r\n'''\n# EVOLVE-BLOCK-START\rx = 1\n# EVOLVE-BLOCK-END\r\nlast = 3  ";
        var result = ProgramDiff.Apply(source, new[] { new ProgramDiffBlock("x = 1", "x = 2  ") },
            new ProgramTaskOptions { EnforceEvolveBlocks = enforce, Language = ProgramLanguage.Python });
        Assert.True(result.IsSuccess);
        Assert.Equal(source.Replace("x = 1", "x = 2  ", StringComparison.Ordinal), result.ModifiedSource);
    }

    [Fact]
    public void Failed_edit_does_not_normalize_source()
    {
        const string source = "a\r\nb\nc\rd  ";
        var result = ProgramDiff.Apply(source, new[] { new ProgramDiffBlock("missing", "new") });
        Assert.False(result.IsSuccess);
        Assert.Equal(source, result.ModifiedSource);
    }

    [Fact]
    public void Parser_keeps_content_whitespace_and_blank_replacement_lines()
    {
        var parsed = ProgramDiff.Parse("<<<<<<< SEARCH\nx  \n=======\ny  \n\n>>>>>>> REPLACE\n");
        var block = Assert.Single(parsed.Blocks);
        Assert.Equal("x  ", block.SearchText);
        Assert.Equal("y  \n", block.ReplaceText);
        var result = ProgramDiff.Apply("x  \nkeep", parsed.Blocks);
        Assert.True(result.IsSuccess);
        Assert.Equal("y  \n\nkeep", result.ModifiedSource);
    }

    [Fact]
    public void Bounds_apply_to_direct_blocks_responses_and_generated_source()
    {
        var options = new ProgramTaskOptions { MaxProgramChars = 5, Diff = new ProgramDiffOptions { MaxBlocks = 1, MaxResponseChars = 8 } };
        Assert.False(ProgramDiff.Apply("longer", new[] { new ProgramDiffBlock("longer", "x") }, options).IsSuccess);
        var oversized = ProgramDiff.Apply("x", new[] { new ProgramDiffBlock("x", "longer") }, options);
        Assert.False(oversized.IsSuccess);
        Assert.Equal("x", oversized.ModifiedSource);
        Assert.False(ProgramDiff.Apply("x", new[] { new ProgramDiffBlock("x", "y"), new ProgramDiffBlock("y", "z") }, options).IsSuccess);
        Assert.Equal(ProgramDiffFailureReason.LimitExceeded, Assert.Single(ProgramDiff.Parse(new string('x', 9), options.Diff).Failures).Reason);
        Assert.False(ProgramDiff.Apply(" \nx", new[] { new ProgramDiffBlock(" ", "injected") }).IsSuccess);
    }

    [Fact]
    public void Split_targets_validates_options_and_refuses_ambiguous_edits()
    {
        var blocks = new[] { new ProgramDiffBlock("x", "y") };
        var result = ProgramDiff.SplitByTarget(blocks, "x", "x");
        Assert.False(result.IsSuccess);
        Assert.Equal(ProgramDiffFailureReason.AmbiguousTarget, Assert.Single(result.Failures).Reason);
        Assert.Throws<ArgumentOutOfRangeException>(() => ProgramDiff.SplitByTarget(blocks, "x", "note", new ProgramTaskOptions { MaxProgramChars = 0 }));
    }

    [Fact]
    public async Task Task_rejects_language_mismatch_and_cancellation_before_dispatch()
    {
        int calls = 0;
        var evaluator = new DelegateProgramFitnessEvaluator(_ => { calls++; return 1; });
        var task = new ProgramEvolutionTask(evaluator, options: new ProgramTaskOptions { Language = ProgramLanguage.Python });
        var mismatch = await task.EvaluateAsync(Candidate("x", ProgramLanguage.CSharp), Context);
        Assert.Equal("program_language_mismatch", Assert.Single(mismatch.Diagnostics).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.EvaluateAsync(Candidate("x", ProgramLanguage.Python), Context, cancelled.Token));
        Assert.Equal(0, calls);
        Assert.Equal(EvolutionEvaluationStatus.Completed, (await task.EvaluateAsync(Candidate("x", ProgramLanguage.Python), Context)).Status);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Task_merging_preserves_current_cost_metrics_and_original_sample_identity()
    {
        var origin = new EvolutionMeasurementOrigin(new string('a', 64), "run", "evaluation", new[] { "sample" },
            DateTimeOffset.Parse("2026-09-16T00:00:00Z"), 4, "calls", "stats-v1");
        var original = new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 2,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["length"] = 9 },
            costUnits: 3, metrics: new Dictionary<string, double> { ["time"] = 12 }).WithMeasurementOrigin(origin);
        var evaluator = new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>(original));
        var task = new ProgramEvolutionTask(evaluator, new ProgramDescriptorSet(new ProgramLengthDescriptor(), new ProgramTokenComplexityDescriptor()));
        var result = await task.EvaluateAsync(Candidate("x = 1"), Context);
        Assert.Equal(9, result.Descriptors["length"]);
        Assert.Equal(3, result.Descriptors["tokenComplexity"]);
        Assert.Equal(3, result.CostUnits);
        Assert.Equal(12, result.Metrics["time"]);
        Assert.Equal(origin.ToJson(), result.MeasurementOrigin!.ToJson());
    }

    [Fact]
    public async Task Missing_receipt_is_not_relabelled_as_zero_cost_failure()
    {
        var evaluator = new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>((EvolutionTaskResult)null!));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new ProgramEvolutionTask(evaluator).EvaluateAsync(Candidate("x"), Context));
    }

    [Fact]
    public void Rebase_keeps_old_descriptor_identity_and_changes_new_identity()
    {
        var original = new ProgramDescriptorSet(new ProgramLengthDescriptor(),
            new ProgramDiversityDescriptor(new[] { "a" }));
        string identity = original.VersionHash;
        var rebased = original.Rebase(new[] { new ProgramGenome("abcdef") });
        Assert.Equal(identity, original.VersionHash);
        Assert.NotEqual(identity, rebased.VersionHash);
        Assert.NotEqual(original.Compute(new ProgramGenome("xyz"))["diversity"], rebased.Compute(new ProgramGenome("xyz"))["diversity"]);
        Assert.Same(original.Descriptors[0], rebased.Descriptors[0]);
    }

    [Fact]
    public void Standalone_types_do_not_resolve_to_AiDotNet_or_mutable_caller_options()
    {
        var options = new ProgramTaskOptions();
        var task = new ProgramEvolutionTask(new DelegateProgramFitnessEvaluator(_ => 1), options: options);
        options.Diff.MaxBlocks = 1;
        Assert.Equal(64, task.GetOptions().Diff.MaxBlocks);
        task.GetOptions().Diff.MaxBlocks = 2;
        Assert.Equal(64, task.GetOptions().Diff.MaxBlocks);
        Assert.Equal(typeof(ProgramGenome).Assembly, typeof(ProgramEvolutionTask).Assembly);
        Assert.DoesNotContain(typeof(ProgramEvolutionTask).Assembly.GetReferencedAssemblies(), reference => reference.Name == "AiDotNet");
    }
}
