using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class AdaptiveIslandSearchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalAndRestartChildrenShareBudgetAcrossCheckpoint(bool restore)
    {
        EvolutionResourceLedger Ledger() => new("island-budget", EvolutionResources.Of("cost_units", 8));
        AdaptiveIslandSearch<TestGenome> Metered(EvolutionResourceLedger ledger) => new(new[]
        {
            new EvolutionIslandStrategy<TestGenome>("one",
                new ResourceMeteredVariationOperator<TestGenome>(new IslandSource("normal"), ledger, EvolutionResources.Of("cost_units", 2), "units-v1"),
                new ResourceMeteredVariationOperator<TestGenome>(new IslandSource("restart"), ledger, EvolutionResources.Of("cost_units", 2), "units-v1"))
        }, new EvolutionIslandPolicyOptions(stagnationOutcomes: 1, restartProposals: 2));
        var ledger = Ledger(); var policy = Metered(ledger);
        await policy.ProposeAsync(Context(1, 0));
        policy.Observe(Evaluation(1, 0, 0), null);
        Assert.Equal(2, policy.Statistics[0].RemainingRestartProposals);
        await policy.ProposeAsync(Context(2, 0));
        if (restore)
        {
            string ledgerState = ledger.CaptureState(), policyState = policy.CaptureState();
            ledger = Ledger(); ledger.RestoreState(ledgerState);
            policy = Metered(ledger); policy.RestoreState(policyState);
        }
        policy.Observe(Evaluation(2, 0, 0), null);
        using (var evaluation = ledger.TryReserve("evaluation", EvolutionResourceStage.Evaluation,
                   EvolutionResources.Of("cost_units", 3), EvolutionResources.Of("cost_units", 3)))
        {
            Assert.NotNull(evaluation); evaluation!.Complete(EvolutionResources.Of("cost_units", 3));
        }
        // Only one unit remains: restart admission must fail without dispatch or overspending.
        await Assert.ThrowsAsync<EvolutionResourceBudgetException>(async () => await policy.ProposeAsync(Context(3, 0)));
        policy.Observe(Evaluation(3, 0, 0, "failed"), null);
        var snapshot = ledger.Snapshot();
        Assert.Equal(7, snapshot.Spent["cost_units"]);
        Assert.Equal(0, snapshot.Unknown); Assert.False(snapshot.MaximumViolated);
        Assert.All(snapshot.Reserved.Values, value => Assert.Equal(0, value));
        Assert.Equal(3, policy.Statistics[0].Outcomes);
        Assert.Equal(2, policy.Statistics[0].RestartOutcomes);
        Assert.Equal(3, snapshot.Receipts.Count);
        var replay = Metered(ledger); replay.RestoreState(policy.CaptureState());
        Assert.Equal(policy.CaptureState(), replay.CaptureState());
    }

    private sealed class IslandSource(string id) : ICostedEvolutionProposalSource<TestGenome>
    {
        public string Id => id;
        public string VersionHash => "v1";
        public ValueTask<EvolutionResourceResult<TestGenome>> ProposeAsync(EvolutionVariationContext<TestGenome> context,
            CancellationToken cancellationToken = default) => new(new EvolutionResourceResult<TestGenome>(new TestGenome(0), EvolutionResources.Of("cost_units", 2)));
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) { }
        public string CaptureState() => "stateless";
        public void RestoreState(string state)
        {
            if (state != "stateless") throw new InvalidDataException("Incompatible source.");
        }
    }
}
