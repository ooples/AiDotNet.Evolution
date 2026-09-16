using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class AdaptiveVariationPortfolioTests
{
    [Fact]
    public async Task RewardsSuccessfulOperatorsAndStillExploresFailures()
    {
        var portfolio = Portfolio();
        await EvolutionVariationOutcomeTests.Run(portfolio, EvolutionVariationOutcomeTests.Options(50));
        Assert.Equal(2, portfolio.Statistics.Count);
        EvolutionOperatorStatistics productive = portfolio.Statistics[0];
        EvolutionOperatorStatistics failing = portfolio.Statistics[1];
        Assert.True(productive.RewardSum > 0);
        Assert.Equal(0, failing.RewardSum);
        Assert.True(productive.Proposals > failing.Proposals);
        Assert.True(failing.Proposals > 1);
        Assert.All(portfolio.Statistics, stat => Assert.Equal(stat.Proposals, stat.Outcomes));
    }

    [Theory]
    [InlineData(EvolutionDispatchMode.Batch)]
    [InlineData(EvolutionDispatchMode.Continuous)]
    public async Task OperatorAttributionIsIndependentOfWorkerCompletionOrder(EvolutionDispatchMode dispatch)
    {
        var first = Portfolio();
        var second = Portfolio();
        EvolutionEngineOptions parallel = EvolutionVariationOutcomeTests.Options(30, dispatch);
        parallel.MaxDegreeOfParallelism = 4;
        EvolutionRunResult<TestGenome> serialRun = await EvolutionVariationOutcomeTests.Run(first,
            EvolutionVariationOutcomeTests.Options(30, dispatch));
        EvolutionRunResult<TestGenome> parallelRun = await EvolutionVariationOutcomeTests.Run(second, parallel);
        Assert.Equal(first.CaptureState(), second.CaptureState());
        Assert.Equal(serialRun.StateHash, parallelRun.StateHash);
    }

    [Fact]
    public async Task CheckpointRestoresChildStateAndPortfolioLearning()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        var first = new AdaptiveVariationPortfolio<TestGenome>(new IVariationOperator<TestGenome>[] { new StatefulVariation() });
        await EvolutionVariationOutcomeTests.Run(first, EvolutionVariationOutcomeTests.Options(8), store);
        var child = new StatefulVariation();
        var resumed = new AdaptiveVariationPortfolio<TestGenome>(new[] { child });
        EvolutionEngineOptions options = EvolutionVariationOutcomeTests.Options(16);
        options.Resume = true;
        EvolutionRunResult<TestGenome> actual = await EvolutionVariationOutcomeTests.Run(resumed, options, store);
        var complete = new AdaptiveVariationPortfolio<TestGenome>(new IVariationOperator<TestGenome>[] { new StatefulVariation() });
        EvolutionRunResult<TestGenome> expected = await EvolutionVariationOutcomeTests.Run(complete,
            EvolutionVariationOutcomeTests.Options(16), new InMemoryEvolutionCheckpointStore());
        Assert.Equal(complete.CaptureState(), resumed.CaptureState());
        Assert.Equal(expected.StateHash, actual.StateHash);
        Assert.True(child.Proposals >= 15);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void RejectsInvalidExploration(double probability) => Assert.Throws<ArgumentOutOfRangeException>(() =>
        new AdaptiveVariationPortfolio<TestGenome>(new[] { new IncrementVariation() }, probability));

    [Fact]
    public void RejectsEmptyDuplicateAndUnboundedOperatorInputs()
    {
        Assert.Throws<ArgumentException>(() => new AdaptiveVariationPortfolio<TestGenome>(Array.Empty<IVariationOperator<TestGenome>>()));
        Assert.Throws<ArgumentException>(() => new AdaptiveVariationPortfolio<TestGenome>(new[] { new IncrementVariation(), new IncrementVariation() }));
        Assert.Throws<ArgumentException>(() => new AdaptiveVariationPortfolio<TestGenome>(Enumerable.Repeat(new IncrementVariation(), 257)));
        Assert.Throws<ArgumentException>(() => new AdaptiveVariationPortfolio<TestGenome>(new IVariationOperator<TestGenome>[] { null! }));
    }

    [Fact]
    public void PolicyAndChildConfigurationParticipateInCompatibility()
    {
        var first = Portfolio();
        var changed = new AdaptiveVariationPortfolio<TestGenome>(new IVariationOperator<TestGenome>[]
            { new UniqueVariation(), new FailingVariation() }, 0.5);
        Assert.NotEqual(first.VersionHash, changed.VersionHash);
        Assert.Throws<InvalidDataException>(() => changed.RestoreState(first.CaptureState()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{}")]
    public void RejectsInvalidStateWithoutChangingLearning(string state)
    {
        var portfolio = Portfolio();
        string before = portfolio.CaptureState();
        Assert.Throws<InvalidDataException>(() => portfolio.RestoreState(state));
        Assert.Equal(before, portfolio.CaptureState());
    }

    [Fact]
    public void RejectsImpossibleRewardAndAttribution()
    {
        var portfolio = Portfolio();
        JsonObject state = JsonNode.Parse(portfolio.CaptureState())!.AsObject();
        state["Arms"]![0]!["RewardSum"] = 2;
        Assert.Throws<InvalidDataException>(() => portfolio.RestoreState(state.ToJsonString()));
        state = JsonNode.Parse(portfolio.CaptureState())!.AsObject();
        state["Pending"]!["-1"] = 0;
        Assert.Throws<InvalidDataException>(() => portfolio.RestoreState(state.ToJsonString()));
    }

    [Fact]
    public async Task CacheHitsDoNotEarnFreeRewardAndSnapshotsAreDetached()
    {
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { new RepeatVariation() });
        IReadOnlyList<EvolutionOperatorStatistics> before = portfolio.Statistics;
        EvolutionEngineOptions options = EvolutionVariationOutcomeTests.Options(8);
        options.MaxProposals = 8;
        await EvolutionVariationOutcomeTests.Run(portfolio, options);
        Assert.Equal(7, portfolio.Statistics[0].Outcomes);
        Assert.Equal(0, portfolio.Statistics[0].RewardSum);
        Assert.Equal(0, before[0].Outcomes);
    }

    private static AdaptiveVariationPortfolio<TestGenome> Portfolio() => new(new IVariationOperator<TestGenome>[]
        { new UniqueVariation(), new FailingVariation() }, 0.2);

    private sealed class UniqueVariation : IVariationOperator<TestGenome>
    {
        public string Id => "unique";
        public string VersionHash => "v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome(checked((int)context.Generation + 2)));
    }

    private sealed class FailingVariation : IVariationOperator<TestGenome>
    {
        public string Id => "failure";
        public string VersionHash => "v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unproductive proposal");
    }

    private sealed class RepeatVariation : IVariationOperator<TestGenome>
    {
        public string Id => "repeat";
        public string VersionHash => "v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(context.Parent.Candidate.CanonicalGenome.Genome);
    }
}
