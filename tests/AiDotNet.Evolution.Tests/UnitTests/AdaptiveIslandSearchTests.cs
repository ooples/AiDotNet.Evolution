using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class AdaptiveIslandSearchTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AllocationPreservesEveryEpochFloorAndRewardsProductivity(bool adaptive)
    {
        var policy = Policy(new EvolutionIslandPolicyOptions(8, 2, diversityWeight: 0, adaptiveAllocation: adaptive, enableRestarts: false));
        for (int generation = 1; generation <= 320; generation++)
        {
            int island = policy.SelectIsland(generation, StableRandom.CreateStream(91, (ulong)generation));
            await policy.ProposeAsync(Context(generation, island));
            policy.Observe(Evaluation(generation, island, island == 0 ? 1 : 0), EvolutionArchiveInsertionResult.Replaced);
            if (generation % 16 == 0) Assert.All(policy.Statistics, stat => Assert.True(stat.EpochProposals >= 2));
        }
        Assert.Equal(320, policy.Statistics.Sum(stat => stat.Proposals));
        if (adaptive) Assert.True(policy.Statistics[0].Proposals > policy.Statistics[1].Proposals * 2);
        else Assert.InRange(policy.Statistics[0].Proposals, 120, 200);
        Assert.Equal(256, policy.RecentDecisions.Count);
        Assert.Equal(65, policy.RecentDecisions[0].Generation);
        var restored = Policy(policy.Options); restored.RestoreState(policy.CaptureState());
        Assert.Equal(policy.CaptureState(), restored.CaptureState());
    }

    [Theory]
    [InlineData("fresh", 1)]
    [InlineData("measured", 1)]
    [InlineData("persistent", 0)]
    [InlineData("local", 0)]
    [InlineData("migration", 0)]
    [InlineData("hit", 0)]
    [InlineData("infeasible", 0)]
    [InlineData("failed", 0)]
    [InlineData("direction", 0)]
    public async Task OnlyFreshFeasibleConsistentMeasurementsEarnGainAndDiversity(string kind, int expected)
    {
        var policy = Policy();
        await policy.ProposeAsync(Context(1, 0));
        EvolutionEvaluation evaluation = Evaluation(1, 0, 1, kind);
        policy.Observe(evaluation, EvolutionArchiveInsertionResult.Inserted);
        Assert.Equal(expected, policy.Statistics[0].MeanRecentGain);
        Assert.Equal(expected, policy.Statistics[0].MeanRecentDiversity);
        Assert.Equal(expected, policy.Statistics[0].FreshMeasurements);
        Assert.Throws<InvalidOperationException>(() => policy.Observe(evaluation, null));
    }

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize, 3, 0.5)]
    [InlineData(EvolutionOptimizationDirection.Minimize, 1, 0.5)]
    [InlineData(EvolutionOptimizationDirection.Minimize, 3, 0)]
    [InlineData(EvolutionOptimizationDirection.Maximize, double.MaxValue, 1)]
    public async Task GainUsesDeclaredDirectionScaleAndFiniteClipping(EvolutionOptimizationDirection direction, double quality, double expected)
    {
        var policy = Policy(new EvolutionIslandPolicyOptions(gainScale: 2, direction: direction));
        await policy.ProposeAsync(Context(1, 0, parent: Evaluation(0, 0, 2, direction: direction)));
        policy.Observe(Evaluation(1, 0, quality, direction: direction), EvolutionArchiveInsertionResult.Replaced);
        Assert.Equal(expected, policy.Statistics[0].MeanRecentGain);
        Assert.Equal(0, policy.Statistics[0].MeanRecentDiversity);
    }

    [Fact]
    public async Task RestartsAreBoundedAndWaitForPendingRestartOutcomes()
    {
        var normal = new RecordingChild(); var restart = new RecordingChild();
        var policy = new AdaptiveIslandSearch<TestGenome>(new[] { new EvolutionIslandStrategy<TestGenome>("only", normal, restart) },
            new EvolutionIslandPolicyOptions(stagnationOutcomes: 2, restartProposals: 3));
        for (int g = 1; g <= 2; g++) { await policy.ProposeAsync(Context(g, 0)); policy.Observe(Evaluation(g, 0, 0), null); }
        Assert.Equal(3, policy.Statistics[0].RemainingRestartProposals);
        for (int g = 3; g <= 6; g++) await policy.ProposeAsync(Context(g, 0));
        Assert.Equal(3, normal.Proposals); Assert.Equal(3, restart.Proposals);
        policy.Observe(Evaluation(3, 0, 0), null); policy.Observe(Evaluation(4, 0, 0), null);
        Assert.Equal(1, policy.Statistics[0].RestartPhasesScheduled);
        policy.Observe(Evaluation(5, 0, 0), null);
        Assert.Equal(2, policy.Statistics[0].RestartPhasesScheduled);
        policy.Observe(Evaluation(6, 0, 1), EvolutionArchiveInsertionResult.Replaced);
        Assert.Equal(3, restart.Outcomes); Assert.Equal(3, normal.Outcomes);
        Assert.Equal(3, policy.Statistics[0].RemainingRestartProposals);
        Assert.Equal(new[] { false, false, true, true, true, false }, policy.RecentDecisions.Select(d => d.Restart));
    }

    [Fact]
    public async Task FailedChildAndPendingCheckpointKeepExactAttribution()
    {
        var child = new RecordingChild(failProposal: true);
        var policy = Single(child);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await policy.ProposeAsync(Context(1, 0)));
        var restoredChild = new RecordingChild(failProposal: true); var restored = Single(restoredChild);
        restored.RestoreState(policy.CaptureState());
        Assert.Equal(policy.CaptureState(), restored.CaptureState());
        restored.Observe(Evaluation(1, 0, 0, "failed"), null);
        Assert.Equal(1, restoredChild.Outcomes); Assert.Equal(0, restored.Statistics[0].FreshMeasurements);
        Assert.Throws<InvalidOperationException>(() => restored.Observe(Evaluation(1, 0, 0), null));
    }

    [Fact]
    public async Task ChildFeedbackFailureDoesNotPublishPolicyLearning()
    {
        var policy = Single(new RecordingChild(failFeedback: true));
        await policy.ProposeAsync(Context(1, 0)); string before = policy.CaptureState();
        Assert.Throws<InvalidOperationException>(() => policy.Observe(Evaluation(1, 0, 1), null));
        Assert.Equal(before, policy.CaptureState());
    }

    [Fact]
    public async Task SelectionCancellationAndInvalidDestinationsDoNotConsumeSlots()
    {
        var policy = Policy(); string initial = policy.CaptureState();
        for (int i = 0; i < 10; i++) Assert.Equal(0, policy.SelectIsland(i, StableRandom.CreateStream(42, (ulong)i)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await policy.ProposeAsync(Context(1, 0), canceled.Token));
        await Assert.ThrowsAsync<ArgumentException>(async () => await policy.ProposeAsync(Context(0, 0)));
        await Assert.ThrowsAsync<ArgumentException>(async () => await policy.ProposeAsync(Context(1, 2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.SelectIsland(-1, StableRandom.CreateStream(0, 0)));
        Assert.Equal(initial, policy.CaptureState());
        await policy.ProposeAsync(Context(1, 0)); string pending = policy.CaptureState();
        await Assert.ThrowsAsync<ArgumentException>(async () => await policy.ProposeAsync(Context(2, 0)));
        Assert.Throws<InvalidOperationException>(() => policy.Observe(Evaluation(1, 1, 1), null));
        Assert.Equal(pending, policy.CaptureState());
    }

    [Theory]
    [InlineData(EvolutionDispatchMode.Batch)]
    [InlineData(EvolutionDispatchMode.Continuous)]
    public async Task HeterogeneousLearningAndMigrationAreWorkerDeterministic(EvolutionDispatchMode dispatch)
    {
        var first = StatefulPolicy(); var second = StatefulPolicy();
        var options = Options(48, dispatch); options.MaxDegreeOfParallelism = 4;
        var expected = await EvolutionVariationOutcomeTests.Run(first, Options(48, dispatch));
        var actual = await EvolutionVariationOutcomeTests.Run(second, options);
        Assert.Equal(expected.StateHash, actual.StateHash); Assert.Equal(first.CaptureState(), second.CaptureState());
        Assert.All(second.Statistics, stat => Assert.Equal(stat.Proposals, stat.Outcomes));
        Assert.Equal(47, second.Statistics.Sum(stat => stat.Outcomes));
    }

    [Fact]
    public async Task EngineCheckpointRestoresMembershipPolicyChildrenAndMigrations()
    {
        var store = new InMemoryEvolutionCheckpointStore(); var first = StatefulPolicy();
        await EvolutionVariationOutcomeTests.Run(first, Options(24), store);
        var resumed = StatefulPolicy(); var options = Options(48); options.Resume = true;
        var actual = await EvolutionVariationOutcomeTests.Run(resumed, options, store);
        var complete = StatefulPolicy();
        var expected = await EvolutionVariationOutcomeTests.Run(complete, Options(48), new InMemoryEvolutionCheckpointStore());
        Assert.Equal(expected.StateHash, actual.StateHash); Assert.Equal(complete.CaptureState(), resumed.CaptureState());
        Assert.Equal(complete.RecentDecisions.Select(d => d.Island), resumed.RecentDecisions.Select(d => d.Island));
        Assert.Contains(actual.Islands.SelectMany(island => island.Entries), entry => entry.Evaluation.Lineage.IsMigrant);
    }

    [Fact]
    public async Task SoftRestartKeepsVerifiedArchiveBestEvenWhenExplorationIsWorse()
    {
        var policy = new AdaptiveIslandSearch<TestGenome>(new[] { new EvolutionIslandStrategy<TestGenome>("only",
            new FixedChild(100), new FixedChild(-100)) }, new EvolutionIslandPolicyOptions(stagnationOutcomes: 2, restartProposals: 3));
        var options = EvolutionVariationOutcomeTests.Options(20); options.MaxProposals = 20;
        var result = await EvolutionVariationOutcomeTests.Run(policy, options);
        Assert.Equal(100, result.Islands[0].Best!.Evaluation.Quality);
        Assert.True(policy.Statistics[0].RestartProposals >= 3);
        Assert.Equal(19, policy.Statistics[0].Outcomes);
    }

    [Fact]
    public async Task NoSelectedParentLeavesPolicyUntouched()
    {
        var policy = Policy(); string before = policy.CaptureState();
        await new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), policy,
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }), Options(8),
            selection: new NullEvolutionSelectionPolicy()).RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(before, policy.CaptureState());
    }

    [Fact]
    public void RejectsSharedChildrenAndChangedMembershipOrConfiguration()
    {
        var child = new IncrementVariation();
        Assert.Throws<ArgumentException>(() => new AdaptiveIslandSearch<TestGenome>(new[] { new EvolutionIslandStrategy<TestGenome>("a", child, child) }));
        Assert.Throws<ArgumentException>(() => new AdaptiveIslandSearch<TestGenome>(Array.Empty<EvolutionIslandStrategy<TestGenome>>()));
        Assert.Throws<ArgumentException>(() => new AdaptiveIslandSearch<TestGenome>(Enumerable.Range(0, 65).Select(i => Member(i.ToString(CultureInfo.InvariantCulture)))));
        Assert.Throws<ArgumentException>(() => new AdaptiveIslandSearch<TestGenome>(new[] { Member("a"), Member("a") }));
        Assert.Throws<ArgumentException>(() => new EvolutionIslandStrategy<TestGenome>("bad\n", child, new IncrementVariation()));
        string state = Policy().CaptureState();
        Assert.Throws<InvalidDataException>(() => new AdaptiveIslandSearch<TestGenome>(new[] { Member("b"), Member("a") }).RestoreState(state));
        Assert.Throws<InvalidDataException>(() => Policy(new EvolutionIslandPolicyOptions(rewardWindow: 2)).RestoreState(state));
    }

    [Theory]
    [InlineData(1, EvolutionIslandAssignmentStrategy.RoundRobin)]
    [InlineData(2, EvolutionIslandAssignmentStrategy.InheritParent)]
    public void EngineRejectsIncompatibleSchedulerConfiguration(int islands, EvolutionIslandAssignmentStrategy assignment)
    {
        var options = Options(8); options.IslandCount = islands; options.IslandAssignment = assignment;
        Assert.Throws<ArgumentException>(() => new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), Policy(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }), options));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    public void MalformedStateIsRejectedWithoutMutation(string state)
    {
        var policy = Policy(); string before = policy.CaptureState();
        Assert.Throws<InvalidDataException>(() => policy.RestoreState(state)); Assert.Equal(before, policy.CaptureState());
    }

    [Theory]
    [InlineData("Proposals")]
    [InlineData("Outcomes")]
    [InlineData("Fresh")]
    [InlineData("RestartProposals")]
    [InlineData("RestartOutcomes")]
    [InlineData("Phases")]
    [InlineData("Remaining")]
    [InlineData("Stagnation")]
    [InlineData("EpochProposals")]
    [InlineData("Recent")]
    [InlineData("Pending")]
    [InlineData("Decisions")]
    [InlineData("Children")]
    [InlineData("Epoch")]
    [InlineData("LastGeneration")]
    public async Task CorruptPolicyGraphIsRejectedBeforeChildRestore(string field)
    {
        var child = new RecordingChild(); var policy = Single(child);
        await policy.ProposeAsync(Context(1, 0)); policy.Observe(Evaluation(1, 0, 1), null);
        string before = policy.CaptureState(); var json = JsonNode.Parse(before)!;
        if (field == "Recent") json["Islands"]![0]![field]![0]!["Gain"] = -1;
        else if (field == "Pending") json[field]!["-1"] = new JsonObject { ["Island"] = 0 };
        else if (field == "Decisions") json[field]![0]!["Island"] = 2;
        else if (field == "Children") json[field]![0] = null;
        else if (field == "Epoch" || field == "LastGeneration") json[field] = -1;
        else json["Islands"]![0]![field] = -1;
        Assert.Throws<InvalidDataException>(() => policy.RestoreState(json.ToJsonString()));
        Assert.Equal(0, child.Restores); Assert.Equal(before, policy.CaptureState());
    }

    internal static EvolutionEngineOptions Options(int attempts, EvolutionDispatchMode dispatch = EvolutionDispatchMode.Batch)
    {
        var options = EvolutionVariationOutcomeTests.Options(attempts, dispatch);
        options.IslandCount = 2; options.MigrationInterval = 4; options.MaxProposals = attempts; options.MaxGenerations = attempts;
        return options;
    }
    private static AdaptiveIslandSearch<TestGenome> Policy(EvolutionIslandPolicyOptions? options = null) => new(new[] { Member("a"), Member("b") }, options);
    private static EvolutionIslandStrategy<TestGenome> Member(string id) => new(id, new IncrementVariation(), new IncrementVariation());
    private static AdaptiveIslandSearch<TestGenome> Single(RecordingChild child) => new(new[] { new EvolutionIslandStrategy<TestGenome>("one", child, new IncrementVariation()) });
    private static AdaptiveIslandSearch<TestGenome> StatefulPolicy() => new(new[] {
        new EvolutionIslandStrategy<TestGenome>("a", new RecordingChild(), new StatefulVariation()),
        new EvolutionIslandStrategy<TestGenome>("b", new StatefulVariation(), new RecordingChild()) },
        new EvolutionIslandPolicyOptions(4, stagnationOutcomes: 2, restartProposals: 2));
    private static EvolutionVariationContext<TestGenome> Context(long generation, int island, EvolutionEvaluation? parent = null)
    {
        parent ??= Evaluation(0, island, 0);
        var entry = new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { 0 }),
            new EvolutionCandidate<TestGenome>(0, new EvolutionCanonicalGenome<TestGenome>(new TestGenome(0), parent.GenomeId), parent.Lineage), parent);
        return new(entry, Array.Empty<EvolutionArchiveEntry<TestGenome>>(), StableRandom.CreateStream(42, (ulong)generation), generation, island);
    }
    private static EvolutionEvaluation Evaluation(long generation, int island, double quality, string kind = "fresh",
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        var evaluation = new EvolutionEvaluation(generation, "g" + generation.ToString(CultureInfo.InvariantCulture),
            kind == "failed" ? EvolutionEvaluationStatus.Failed : EvolutionEvaluationStatus.Completed, quality,
            kind == "direction" ? EvolutionOptimizationDirection.Minimize : direction, new Dictionary<string, double>(), Array.Empty<double>(),
            kind == "infeasible" ? new[] { 1.0 } : Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            new EvolutionLineage(null, null, "adaptive-islands", null, generation, island, (ulong)generation),
            kind == "hit" ? EvolutionCacheStatus.Hit : EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
        EvolutionMeasurementOriginKind? origin = kind switch
        {
            "measured" => EvolutionMeasurementOriginKind.Measured,
            "persistent" => EvolutionMeasurementOriginKind.PersistentReuse,
            "local" => EvolutionMeasurementOriginKind.RunLocalReuse,
            "migration" => EvolutionMeasurementOriginKind.MigrationCopy,
            _ => null
        };
        return origin.HasValue ? evaluation.WithMeasurementOrigin(new EvolutionMeasurementOrigin(new string('a', 64), "run", "1",
            new[] { "sample" }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, "calls", "test-v1", origin.Value)) : evaluation;
    }
    private sealed class FixedChild(int value) : IVariationOperator<TestGenome>
    {
        public string Id => "fixed";
        public string VersionHash => value.ToString(CultureInfo.InvariantCulture);
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) => new(new TestGenome(value));
    }
    private sealed class RecordingChild(bool failProposal = false, bool failFeedback = false) : IOutcomeAwareVariationOperator<TestGenome>
    {
        public string Id => "recording";
        public string VersionHash => "v1-" + failProposal + "-" + failFeedback;
        public int Proposals { get; private set; }
        public int Outcomes { get; private set; }
        public int Restores { get; private set; }
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            Proposals++;
            if (failProposal) throw new InvalidOperationException("proposal failure");
            return new(new TestGenome(checked((int)context.Generation * 10 + Outcomes + 2)));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
        {
            if (failFeedback) throw new InvalidOperationException("feedback failure");
            Outcomes++;
        }
        public string CaptureState() => Proposals.ToString(CultureInfo.InvariantCulture) + "," + Outcomes.ToString(CultureInfo.InvariantCulture);
        public void RestoreState(string state)
        {
            string[] pieces = state.Split(','); Proposals = int.Parse(pieces[0], CultureInfo.InvariantCulture);
            Outcomes = int.Parse(pieces[1], CultureInfo.InvariantCulture); Restores++;
        }
    }
}
