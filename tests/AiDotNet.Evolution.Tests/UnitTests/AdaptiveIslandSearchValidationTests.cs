using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class AdaptiveIslandSearchTests
{
    [Theory]
    [InlineData("epoch-zero")]
    [InlineData("epoch-large")]
    [InlineData("floor-zero")]
    [InlineData("floor-large")]
    [InlineData("window-zero")]
    [InlineData("window-large")]
    [InlineData("scale-zero")]
    [InlineData("scale-nan")]
    [InlineData("scale-infinity")]
    [InlineData("diversity-negative")]
    [InlineData("diversity-large")]
    [InlineData("diversity-nan")]
    [InlineData("stagnation-zero")]
    [InlineData("stagnation-large")]
    [InlineData("restart-zero")]
    [InlineData("restart-large")]
    [InlineData("direction")]
    public void PolicySettingsAreBounded(string invalid)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionIslandPolicyOptions(
            proposalsPerIslandPerEpoch: invalid == "epoch-zero" ? 0 : invalid == "epoch-large" ? 1025 : 16,
            minimumPerIslandPerEpoch: invalid == "floor-zero" ? 0 : invalid == "floor-large" ? 17 : 1,
            rewardWindow: invalid == "window-zero" ? 0 : invalid == "window-large" ? 4097 : 32,
            gainScale: invalid == "scale-zero" ? 0 : invalid == "scale-nan" ? double.NaN : invalid == "scale-infinity" ? double.PositiveInfinity : 1,
            diversityWeight: invalid == "diversity-negative" ? -1 : invalid == "diversity-large" ? 2 : invalid == "diversity-nan" ? double.NaN : 0.25,
            stagnationOutcomes: invalid == "stagnation-zero" ? 0 : invalid == "stagnation-large" ? 1_000_001 : 64,
            restartProposals: invalid == "restart-zero" ? 0 : invalid == "restart-large" ? 4097 : 8,
            direction: invalid == "direction" ? (EvolutionOptimizationDirection)99 : EvolutionOptimizationDirection.Maximize));
    }

    [Theory]
    [InlineData("infeasible")]
    [InlineData("failed")]
    [InlineData("direction")]
    public async Task InvalidParentCannotCreateImprovementCredit(string kind)
    {
        var policy = Policy();
        await policy.ProposeAsync(Context(1, 0, Evaluation(0, 0, 0, kind)));
        policy.Observe(Evaluation(1, 0, 1), EvolutionArchiveInsertionResult.Replaced);
        Assert.Equal(0, policy.Statistics[0].MeanRecentGain);
    }

    [Theory]
    [InlineData("null-island")]
    [InlineData("null-pending")]
    [InlineData("bad-parent")]
    [InlineData("bad-destination")]
    [InlineData("null-reward")]
    [InlineData("bad-diversity")]
    [InlineData("bad-gain")]
    [InlineData("fresh-zero")]
    [InlineData("false-epoch")]
    [InlineData("duplicate-decision")]
    [InlineData("wrong-pending-decision")]
    [InlineData("null-decisions")]
    [InlineData("empty-children")]
    [InlineData("unexpected-child")]
    public async Task CrossFieldStateCorruptionIsRejectedWithoutPublishingState(string corruption)
    {
        var policy = Single(new RecordingChild());
        await policy.ProposeAsync(Context(1, 0)); policy.Observe(Evaluation(1, 0, 1), EvolutionArchiveInsertionResult.Inserted);
        await policy.ProposeAsync(Context(2, 0));
        string before = policy.CaptureState(); var json = JsonNode.Parse(before)!;
        switch (corruption)
        {
            case "null-island": json["Islands"]![0] = null; break;
            case "null-pending": json["Pending"]!["2"] = null; break;
            case "bad-parent": json["Pending"]!["2"]!["ParentQuality"] = "NaN"; break;
            case "bad-destination": json["Pending"]!["2"]!["Island"] = -1; break;
            case "null-reward": json["Islands"]![0]!["Recent"]![0] = null; break;
            case "bad-diversity": json["Islands"]![0]!["Recent"]![0]!["Diversity"] = 0.5; break;
            case "bad-gain": json["Islands"]![0]!["Recent"]![0]!["Gain"] = 2; break;
            case "fresh-zero": json["Islands"]![0]!["Fresh"] = 0; break;
            case "false-epoch": json["Epoch"] = 1; break;
            case "duplicate-decision": json["Decisions"]![1]!["Generation"] = 1; break;
            case "wrong-pending-decision": json["Decisions"]![1]!["Restart"] = true; break;
            case "null-decisions": json["Decisions"] = null; break;
            case "empty-children": json["Children"] = new JsonArray(); break;
            case "unexpected-child": json["Children"]![1] = "unexpected"; break;
        }
        Assert.Throws<InvalidDataException>(() => policy.RestoreState(json.ToJsonString()));
        Assert.Equal(before, policy.CaptureState());
    }

    [Fact]
    public async Task RecentRewardsExpireAndDiagnosticViewsAreDetached()
    {
        var policy = new AdaptiveIslandSearch<TestGenome>(new[] { Member("a") }, new EvolutionIslandPolicyOptions(rewardWindow: 2, enableRestarts: false));
        var initial = policy.Statistics; var decisions = policy.RecentDecisions;
        for (int g = 1; g <= 3; g++)
        {
            await policy.ProposeAsync(Context(g, 0));
            policy.Observe(Evaluation(g, 0, g == 1 ? 1 : 0), EvolutionArchiveInsertionResult.Replaced);
        }
        Assert.Equal(0, policy.Statistics[0].MeanRecentGain); Assert.Equal(0, initial[0].Proposals); Assert.Empty(decisions);
        Assert.Equal("increment", policy.RecentDecisions[0].OperatorId); Assert.Equal("a", policy.RecentDecisions[0].IslandId);
        Assert.Equal(0, policy.Statistics[0].RestartPhasesScheduled);
    }

    [Fact]
    public void MutableChildIdentityAndOversizedCheckpointFailClosed()
    {
        var child = new MutableChild(); var policy = new AdaptiveIslandSearch<TestGenome>(new[] { new EvolutionIslandStrategy<TestGenome>("a", child, new IncrementVariation()) });
        string before = policy.CaptureState(); child.VersionHash = "v2";
        Assert.Throws<InvalidOperationException>(() => policy.CaptureState());
        Assert.Throws<InvalidOperationException>(() => policy.SelectIsland(1, StableRandom.CreateStream(0, 0)));
        Assert.Throws<InvalidOperationException>(() => policy.RestoreState(before));
        Assert.Throws<InvalidDataException>(() => Policy().RestoreState(new string('x', 16 * 1024 * 1024 + 1)));
        Assert.Throws<ArgumentException>(() => new EvolutionIslandStrategy<TestGenome>(new string('x', 129), new IncrementVariation(), new IncrementVariation()));
        child.VersionHash = "";
        Assert.Throws<ArgumentException>(() => new EvolutionIslandStrategy<TestGenome>("a", child, new IncrementVariation()));
    }

    [Fact]
    public void EngineRejectsSchedulerWithoutCheckpointContract()
    {
        Assert.Throws<ArgumentException>(() => new EvolutionEngine<TestGenome>(new SyntheticEvolutionTask(), new UncheckpointedScheduler(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }), Options(8)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task EngineRejectsOutOfRangeSchedulerBeforeDispatch(int destination)
    {
        var task = new SyntheticEvolutionTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionVariationOutcomeTests.Run(new BadScheduler(destination), Options(8), task: task));
        Assert.Equal(1, task.Calls);
    }

    private class UncheckpointedScheduler : IVariationOperator<TestGenome>, IEvolutionIslandProposalScheduler
    {
        public string Id => "scheduler";
        public string VersionHash => "v1";
        public int IslandCount => 2;
        public virtual int SelectIsland(long evaluationId, StableRandom random) => 0;
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) => new(new TestGenome(1));
    }

    private sealed class BadScheduler(int destination) : UncheckpointedScheduler, ICheckpointableVariationOperator<TestGenome>
    {
        public override int SelectIsland(long evaluationId, StableRandom random) => destination;
        public string CaptureState() => "state";
        public void RestoreState(string state) { }
    }

    private sealed class MutableChild : IVariationOperator<TestGenome>
    {
        public string Id => "mutable";
        public string VersionHash { get; set; } = "v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) => new(new TestGenome(1));
    }
}
