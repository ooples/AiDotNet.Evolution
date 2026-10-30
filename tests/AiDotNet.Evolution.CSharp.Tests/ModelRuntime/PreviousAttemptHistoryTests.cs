using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// E-4 (#347): the previous-attempts section recounts evaluated children - what each changed, what it measured, and
/// how that compared with its own parent - plus the replies on this parent that could not be applied. The third
/// pilot's prompts instead listed every proposal retry, so an accepted first try read "Changes: Accepted:" with no
/// measurements and a "Mixed results" outcome.
/// </summary>
public sealed class PreviousAttemptHistoryTests
{
    private static readonly ProgramGenome Parent = new("def solve(x):\n    return x\n", ProgramLanguage.Python);

    [Fact]
    public async Task AnEvaluatedChildIsRecountedWithItsChangeItsMeasurementsAndHowItComparedWithItsParent()
    {
        var client = new FakeChatClient(DiffResponse("    return x", "    return x + 1"), DiffResponse("    return x", "    return 2 * x"));
        var variation = new LlmProgramVariationOperator(client);

        ProgramGenome child = await variation.ProposeAsync(Context(Parent));
        variation.Observe(Evaluation(child, 1, 0.9, Parent.Id), EvolutionArchiveInsertionResult.Inserted);
        await variation.ProposeAsync(Context(Parent));

        string user = client.Conversations[1][1].Text;
        Assert.Contains("### Attempt 1", user, StringComparison.Ordinal);
        Assert.Contains("Change 1: 'return x' to 'return x + 1'", user, StringComparison.Ordinal);
        Assert.Contains("quality: 0.9000, x: 1.9000", user, StringComparison.Ordinal);
        Assert.Contains("Every measurement improved", user, StringComparison.Ordinal);
        // The accepted reply itself is not listed; its child is.
        Assert.DoesNotContain("Accepted:", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFullRewriteAndAMultiLineEditAreSummarizedAsOpenEvolveSummarizesThem()
    {
        var parent = new ProgramGenome("def solve(x):\n    y = x\n    return y\n", ProgramLanguage.Python);
        var client = new FakeChatClient(
            DiffResponse("    y = x\n    return y", "    y = x * 2\n    return y + 1"),
            "```python\ndef solve(x):\n    return 7\n```",
            DiffResponse("    y = x", "    y = 0"));
        var diff = new LlmProgramVariationOperator(client, new ProgramProposalOptions { Language = ProgramLanguage.Python });
        var rewrite = new LlmProgramVariationOperator(client, new ProgramProposalOptions { Language = ProgramLanguage.Python },
            new LlmProgramVariationOptions { Mode = ProgramEvolutionMode.FullRewrite });

        ProgramGenome edited = await diff.ProposeAsync(Context(parent));
        diff.Observe(Evaluation(edited, 1, 0.2, parent.Id), null);
        ProgramGenome rewritten = await rewrite.ProposeAsync(Context(parent));
        rewrite.Observe(Evaluation(rewritten, 2, 0.2, parent.Id), null);
        await diff.ProposeAsync(Context(parent));

        Assert.Contains("Change 1: Replace:\n  y = x\n  return y\nwith:\n  y = x * 2\n  return y + 1",
            client.Conversations[2][1].Text, StringComparison.Ordinal);
        Assert.Contains("Every measurement regressed", client.Conversations[2][1].Text, StringComparison.Ordinal);
        Assert.Equal(ProgramDiffSummary.FullRewrite, "Full rewrite");
        string state = rewrite.CaptureState();
        Assert.Contains("\"Changes\":\"Full rewrite\"", state, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedEditKeepsItsRelativeIndentationAndLongBlocksAreCut()
    {
        var nested = new ProgramDiffBlock("    if x:\n        return 1\n", "    if x:\n        return 2\n");
        var longBlock = new ProgramDiffBlock("a\nb\nc", new string('z', 12) + "\nq\nr");

        Assert.Equal("Change 1: Replace:\n  if x:\n      return 1\nwith:\n  if x:\n      return 2",
            ProgramDiffSummary.Format(new[] { nested }));
        Assert.Equal("Change 1: Replace:\n  a\n  b\n  ... (1 more lines)\nwith:\n  zzzzzzz...\n  q\n  ... (1 more lines)",
            ProgramDiffSummary.Format(new[] { longBlock }, maxLineLength: 10, maxLines: 2));
    }

    [Fact]
    public async Task ARepliedEditThatCouldNotBeAppliedIsRecountedAsNotApplied()
    {
        var client = new FakeChatClient(
            DiffResponse("    return nothing", "    return 3"),
            DiffResponse("    return x", "    return 3"),
            DiffResponse("    return x", "    return 4"));
        var variation = new LlmProgramVariationOperator(client);

        await variation.ProposeAsync(Context(Parent));
        await variation.ProposeAsync(Context(Parent));

        string user = client.Conversations[2][1].Text;
        Assert.Contains("Not applied: ParseFailed", user, StringComparison.Ordinal);
        Assert.DoesNotContain("Mixed results", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnusableReplyOnAnotherParentIsNotRecounted()
    {
        var other = new ProgramGenome("def solve(x):\n    return -x\n", ProgramLanguage.Python);
        var client = new FakeChatClient(
            DiffResponse("    return nothing", "    return 3"),
            DiffResponse("    return -x", "    return 3"),
            DiffResponse("    return x", "    return 4"));
        var variation = new LlmProgramVariationOperator(client);

        await variation.ProposeAsync(Context(other));
        await variation.ProposeAsync(Context(Parent));

        Assert.DoesNotContain("Not applied", client.Conversations[2][1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnusableRepliesAreTheNewestEntriesSoTheyAreShownFirstAndSurviveACut()
    {
        var client = new FakeChatClient(
            DiffResponse("    return x", "    return x + 1"),
            DiffResponse("    return nothing", "    return 3"),
            DiffResponse("    return x", "    return x + 2"),
            DiffResponse("    return x", "    return x + 3"),
            DiffResponse("    return x", "    return x + 4"));
        var variation = new LlmProgramVariationOperator(client, variationOptions: new LlmProgramVariationOptions { MaxPreviousAttempts = 1 });

        variation.Observe(Evaluation(await variation.ProposeAsync(Context(Parent)), 1, 0.6, Parent.Id), null);
        await variation.ProposeAsync(Context(Parent));
        await variation.ProposeAsync(Context(Parent));

        string user = client.Conversations[3][1].Text;
        int reply = user.IndexOf("Not applied", StringComparison.Ordinal);
        int child = user.IndexOf("'return x + 1'", StringComparison.Ordinal);
        Assert.True(reply >= 0 && child >= 0 && reply < child, user);
    }

    [Fact]
    public async Task AChildWhoseEvaluationFailedSaysSoInsteadOfComparingNothing()
    {
        var client = new FakeChatClient(DiffResponse("    return x", "    return 1 / 0"), DiffResponse("    return x", "    return 5"));
        var variation = new LlmProgramVariationOperator(client);

        ProgramGenome child = await variation.ProposeAsync(Context(Parent));
        variation.Observe(Evaluation(child, 1, null, Parent.Id, status: EvolutionEvaluationStatus.Failed), null);
        await variation.ProposeAsync(Context(Parent));

        string user = client.Conversations[1][1].Text;
        Assert.Contains("Evaluation Failed", user, StringComparison.Ordinal);
        Assert.Contains("Change 1: 'return x' to 'return 1 / 0'", user, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProgramPreviousAttemptScope.Island, new[] { "x + 10" })]
    [InlineData(ProgramPreviousAttemptScope.Parent, new[] { "x + 20" })]
    [InlineData(ProgramPreviousAttemptScope.Run, new[] { "x + 10", "x + 20", "x + 30" })]
    public async Task TheScopeDecidesWhoseChildrenAreRecounted(ProgramPreviousAttemptScope scope, string[] recounted)
    {
        var other = new ProgramGenome("def solve(x):\n    return -x\n", ProgramLanguage.Python);
        var client = new FakeChatClient(
            DiffResponse("    return -x", "    return x + 10"),
            DiffResponse("    return x", "    return x + 20"),
            DiffResponse("    return -x", "    return x + 30"),
            DiffResponse("    return x", "    return x + 40"));
        var variation = new LlmProgramVariationOperator(client, variationOptions: new LlmProgramVariationOptions
        {
            PreviousAttemptScope = scope
        });

        // Island 0: a child of another parent. Island 1: a child of this parent, and one of the other parent.
        variation.Observe(Evaluation(await variation.ProposeAsync(Context(other, island: 0)), 1, 0.6, other.Id, island: 0), null);
        variation.Observe(Evaluation(await variation.ProposeAsync(Context(Parent, island: 1)), 2, 0.6, Parent.Id, island: 1), null);
        variation.Observe(Evaluation(await variation.ProposeAsync(Context(other, island: 1)), 3, 0.6, other.Id, island: 1), null);
        await variation.ProposeAsync(Context(Parent, island: 0));

        string user = client.Conversations[3][1].Text;
        foreach (string edit in new[] { "x + 10", "x + 20", "x + 30" })
            Assert.Equal(recounted.Contains(edit), user.Contains("'return " + edit + "'", StringComparison.Ordinal));
        // The prompt shows the newest first, as OpenEvolve does.
        int[] at = recounted.Select(edit => user.IndexOf("'return " + edit + "'", StringComparison.Ordinal)).ToArray();
        Assert.Equal(at.OrderByDescending(index => index), at);
    }

    [Fact]
    public async Task TheHistoryAndAReplyAwaitingItsEvaluationSurviveACheckpoint()
    {
        var client = new FakeChatClient(
            DiffResponse("    return x", "    return x + 1"),
            DiffResponse("    return x", "    return x + 2"),
            DiffResponse("    return x", "    return x + 3"),
            DiffResponse("    return x", "    return x + 3"));
        var original = new LlmProgramVariationOperator(client);
        ProgramGenome first = await original.ProposeAsync(Context(Parent));
        original.Observe(Evaluation(first, 1, 0.7, Parent.Id), null);
        // Proposed but not yet committed when the checkpoint is taken.
        ProgramGenome second = await original.ProposeAsync(Context(Parent));

        var resumed = new LlmProgramVariationOperator(client);
        resumed.RestoreState(original.CaptureState());
        original.Observe(Evaluation(second, 2, 0.3, Parent.Id), null);
        resumed.Observe(Evaluation(second, 2, 0.3, Parent.Id), null);
        Assert.Equal(original.CaptureState(), resumed.CaptureState());

        await original.ProposeAsync(Context(Parent));
        await resumed.ProposeAsync(Context(Parent));
        Assert.Equal(client.Conversations[2][1].Text, client.Conversations[3][1].Text);
        Assert.Contains("'return x + 2'", client.Conversations[3][1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AVersionOneStateIsStillRead()
    {
        var variation = new LlmProgramVariationOperator(new FakeChatClient("unused"));

        variation.RestoreState("{\"SchemaVersion\":1,\"Attempts\":[]}");

        Assert.Contains("\"SchemaVersion\":2", variation.CaptureState(), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => variation.RestoreState("{\"SchemaVersion\":3,\"Attempts\":[]}"));
    }

    [Fact]
    public void AnEvaluationTheOperatorDidNotProposeIsIgnored()
    {
        var variation = new LlmProgramVariationOperator(new FakeChatClient("unused"));
        string before = variation.CaptureState();

        variation.Observe(Evaluation(new ProgramGenome("print(1)", ProgramLanguage.Python), 5, 1, Parent.Id), null);

        Assert.Equal(before, variation.CaptureState());
    }

    private static string DiffResponse(string search, string replace) =>
        "<<<<<<< SEARCH\n" + search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private static EvolutionVariationContext<ProgramGenome> Context(ProgramGenome parent, int island = 0) =>
        new(Entry(parent), Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(), new StableRandom(1234UL, 7UL), 1, island);

    private static EvolutionArchiveEntry<ProgramGenome> Entry(ProgramGenome genome)
    {
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new EvolutionCanonicalGenome<ProgramGenome>(genome, genome.Id), lineage);
        return new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 0 }), candidate, Evaluation(genome, 0, 0.5, null));
    }

    private static EvolutionEvaluation Evaluation(ProgramGenome genome, long id, double? quality, string? parentId, int island = 0,
        EvolutionEvaluationStatus status = EvolutionEvaluationStatus.Completed)
    {
        var lineage = new EvolutionLineage(parentId is null ? null : new[] { parentId }, null,
            parentId is null ? "seed" : "llm-program-variation", null, parentId is null ? 0 : 1, island, (ulong)id);
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        if (quality is double value) metrics["x"] = value + 1;
        return new EvolutionEvaluation(id, genome.Id, status, quality, EvolutionOptimizationDirection.Maximize, metrics,
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage,
            EvolutionCacheStatus.Miss,
            status == EvolutionEvaluationStatus.Completed
                ? Array.Empty<EvolutionDiagnostic>()
                : new[] { new EvolutionDiagnostic("evaluator_exception", "ZeroDivisionError") },
            "task-v1", "evaluator-v1", "config-v1");
    }
}
