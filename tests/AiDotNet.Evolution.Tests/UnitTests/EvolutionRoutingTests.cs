using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>US-19 routing: escalation up a model ladder, and explicit handling of stale reward estimates.</summary>
public sealed class EvolutionRoutingTests
{
    [Fact]
    public async Task Escalates_after_consecutive_failures_and_returns_to_the_cheapest_tier_after_a_success()
    {
        var ladder = new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[] { new Failing("cheap"), new Unique("strong") }, 3);
        await EvolutionVariationOutcomeTests.Run(ladder, EvolutionVariationOutcomeTests.Options(40));
        Assert.NotEmpty(ladder.Escalations);
        EvolutionEscalation first = ladder.Escalations[0];
        Assert.Equal((0, 1, "cheap", "strong", 3), (first.FromTier, first.ToTier, first.FromOperatorId, first.ToOperatorId, first.ConsecutiveFailures));
        Assert.True(ladder.TierSuccesses[1] > 0, "the stronger tier succeeded");
        Assert.True(ladder.TierProposals[0] > 3, "a success returned proposals to the cheapest tier");
        Assert.True(ladder.Escalations.Count > 1, "repeated cheap failures escalate again");
    }

    [Fact]
    public async Task Never_escalates_past_the_strongest_tier()
    {
        var ladder = new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[] { new Failing("a"), new Failing("b") }, 2);
        await EvolutionVariationOutcomeTests.Run(ladder, EvolutionVariationOutcomeTests.Options(24));
        Assert.Single(ladder.Escalations);
        Assert.Equal(1, ladder.CurrentTier);
    }

    [Fact]
    public async Task Records_the_incremental_cost_of_an_escalation_from_actual_receipts()
    {
        var ladder = new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[]
            { new Costed("cheap", 1m, fails: true), new Costed("strong", 5m, fails: false) }, 2);
        await EvolutionVariationOutcomeTests.Run(ladder, EvolutionVariationOutcomeTests.Options(24));
        EvolutionResources? incremental = ladder.Escalations[0].IncrementalCost;
        Assert.NotNull(incremental);
        Assert.Equal(4m, incremental["cost_units"]);
    }

    [Fact]
    public async Task A_resumed_ladder_matches_an_uninterrupted_run()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await EvolutionVariationOutcomeTests.Run(Ladder(), EvolutionVariationOutcomeTests.Options(10), store);
        EvolutionEngineOptions resume = EvolutionVariationOutcomeTests.Options(30);
        resume.Resume = true;
        var resumed = Ladder();
        EvolutionRunResult<TestGenome> actual = await EvolutionVariationOutcomeTests.Run(resumed, resume, store);
        var complete = Ladder();
        EvolutionRunResult<TestGenome> expected = await EvolutionVariationOutcomeTests.Run(complete, EvolutionVariationOutcomeTests.Options(30),
            new InMemoryEvolutionCheckpointStore());
        Assert.Equal(complete.CaptureState(), resumed.CaptureState());
        Assert.Equal(expected.StateHash, actual.StateHash);
        Assert.Equal(complete.Escalations.Count, resumed.Escalations.Count);
    }

    [Fact]
    public void Tampered_or_foreign_ladder_state_is_refused()
    {
        var ladder = Ladder();
        string state = ladder.CaptureState();
        Assert.Throws<InvalidDataException>(() => new EscalatingVariationOperator<TestGenome>(
            new IVariationOperator<TestGenome>[] { new Failing("cheap"), new Unique("other") }, 3).RestoreState(state));
        Assert.Throws<InvalidDataException>(() => Ladder().RestoreState(state.Replace("\"Tier\":0", "\"Tier\":7")));
        Ladder().RestoreState(state);
    }

    [Fact]
    public void Invalid_ladders_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[] { new Unique("x") }, 1));
        Assert.Throws<ArgumentException>(() => new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[] { new Unique("x"), new Failing("x") }, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[] { new Unique("x"), new Failing("y") }, 0));
    }

    [Fact]
    public async Task A_stale_estimate_is_re_probed_only_when_aging_is_configured()
    {
        EvolutionOperatorRewardPolicy plain = Policy();
        EvolutionOperatorRewardPolicy aged = plain.WithEstimateAging(4);
        Assert.NotEqual(plain.VersionHash, aged.VersionHash);
        var without = new AdaptiveVariationPortfolio<TestGenome>(new IVariationOperator<TestGenome>[] { new Unique("good"), new Failing("bad") }, plain, 1e-9);
        var with = new AdaptiveVariationPortfolio<TestGenome>(new IVariationOperator<TestGenome>[] { new Unique("good"), new Failing("bad") }, aged, 1e-9);
        await EvolutionVariationOutcomeTests.Run(without, EvolutionVariationOutcomeTests.Options(48));
        await EvolutionVariationOutcomeTests.Run(with, EvolutionVariationOutcomeTests.Options(48));
        Assert.True(without.Statistics[1].Proposals <= 2, $"without aging the bad arm stays abandoned ({without.Statistics[1].Proposals})");
        Assert.True(with.Statistics[1].Proposals >= 4, $"with aging its stale estimate is re-probed ({with.Statistics[1].Proposals})");
        Assert.DoesNotContain("OutcomeTick", without.CaptureState());
        Assert.Contains("OutcomeTick", with.CaptureState());
    }

    [Fact]
    public void Estimate_aging_is_validated_and_configured_once()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy().WithEstimateAging(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy().WithEstimateAging(3, 0));
        Assert.Throws<InvalidOperationException>(() => Policy().WithEstimateAging(3).WithEstimateAging(3));
        Assert.NotEqual(Policy().WithEstimateAging(3).VersionHash, Policy().WithEstimateAging(3, 8).VersionHash);
    }

    private static EvolutionOperatorRewardPolicy Policy() =>
        new(EvolutionOperatorRewardKind.ArchiveSuccess, EvolutionOperatorCostBasis.Evaluation, "units-v1");

    private static EscalatingVariationOperator<TestGenome> Ladder() =>
        new(new IVariationOperator<TestGenome>[] { new Failing("cheap"), new Unique("strong") }, 3);

    [Fact]
    public async Task A_canceled_proposal_leaves_no_pending_outcome_so_its_generation_can_be_proposed_again()
    {
        // The engine rethrows a cancellation instead of committing an outcome, so Observe never clears the generation.
        var (candidate, evaluation) = MapElitesArchiveTests.Create(0, "parent", 0, 0);
        var entry = new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
        EvolutionVariationContext<TestGenome> Context() => new(entry, Array.Empty<EvolutionArchiveEntry<TestGenome>>(), StableRandom.CreateStream(1, 1), 7, 0);
        var tier = new CancelingDuringCall("cheap");
        var ladder = new EscalatingVariationOperator<TestGenome>(new IVariationOperator<TestGenome>[] { tier, new Unique("strong") }, 3);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var run = new CancellationTokenSource();
            tier.Source = run;
            // The second attempt reuses generation 7; it must be canceled again, not refused as a repeated proposal.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ladder.ProposeAsync(Context(), run.Token).AsTask());
        }
    }

    // Cancels the caller's token during the delegated call, as a run being stopped mid-proposal does.
    private sealed class CancelingDuringCall(string id) : IVariationOperator<TestGenome>
    {
        public CancellationTokenSource? Source { get; set; }
        public string Id => id;
        public string VersionHash => "v1";
        public async ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Source?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return new TestGenome(0);
        }
    }

    private sealed class Unique(string id) : IVariationOperator<TestGenome>
    {
        public string Id => id;
        public string VersionHash => "v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome(checked((int)context.Generation + 2)));
    }

    private sealed class Failing(string id) : IVariationOperator<TestGenome>
    {
        public string Id => id;
        public string VersionHash => "v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("unproductive proposal");
    }

    private sealed class Costed(string id, decimal cost, bool fails) : IVariationOperator<TestGenome>, IEvolutionProposalCostProvider
    {
        private readonly HashSet<long> _proposed = new();
        public string Id => id;
        public string VersionHash => "v1";
        public string CostUnitVersionHash => "units-v1";
        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            _proposed.Add(context.Generation);
            if (fails) throw new InvalidOperationException("unproductive proposal");
            return new(new TestGenome(checked((int)context.Generation + 2)));
        }
        public EvolutionProposalCost GetProposalCost(long generation) => _proposed.Contains(generation)
            ? new EvolutionProposalCost(id + "-" + generation, EvolutionResources.Of("cost_units", cost), EvolutionResourceOutcome.Completed)
            : throw new InvalidOperationException("unknown generation");
    }
}