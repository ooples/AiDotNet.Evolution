using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

/// <summary>
/// Per-stage totals are maintained incrementally as work is admitted and settled rather than recomputed by
/// scanning every operation on each Snapshot(). These pin the values against hand-computed ground truth,
/// including across a CaptureState/RestoreState round-trip, which is the one place the running totals are
/// rebuilt rather than accumulated.
/// </summary>
public sealed class ResourceStageTotalsTests
{
    private static EvolutionResources R(decimal cost, decimal calls) =>
        new(new Dictionary<string, decimal> { ["cost_units"] = cost, ["proposal_calls"] = calls });

    private static EvolutionResourceLedger Ledger() =>
        new("stage-totals", R(1000m, 1000m), retainedReceiptLimit: 4, maximumOperations: 1000);

    private static EvolutionResourceStageSnapshot Stage(EvolutionResourceSnapshot snapshot, EvolutionResourceStage stage) =>
        Assert.Single(snapshot.Stages, value => value.Stage == stage);

    [Fact]
    public void StageTotalsSeparateSpentSettledAndStillReservedWorkPerStage()
    {
        var ledger = Ledger();

        // Evaluation: one measured settlement, one unknown settlement charging its maximum, one left pending.
        ledger.TryReserve("eval-1", EvolutionResourceStage.Evaluation, R(2m, 1m), R(5m, 1m))!.Complete(R(3m, 1m));
        ledger.TryReserve("eval-2", EvolutionResourceStage.Evaluation, R(2m, 1m), R(7m, 1m))!
            .Complete(R(7m, 1m), EvolutionResourceOutcome.Unknown);
        var pending = ledger.TryReserve("eval-3", EvolutionResourceStage.Evaluation, R(1m, 1m), R(9m, 1m));
        Assert.NotNull(pending);

        // A different stage must not be folded into the evaluation totals.
        ledger.TryReserve("setup-1", EvolutionResourceStage.Setup, R(4m, 0m), R(4m, 0m))!.Complete(R(4m, 0m));

        var evaluation = Stage(ledger.Snapshot(), EvolutionResourceStage.Evaluation);
        Assert.Equal(3, evaluation.Admitted);
        Assert.Equal(2, evaluation.Settled);
        Assert.Equal(1, evaluation.Unknown);
        Assert.Equal(10m, evaluation.Spent["cost_units"]);   // 3 measured + 7 charged at maximum
        Assert.Equal(9m, evaluation.Reserved["cost_units"]); // only the pending maximum
        Assert.Equal(2m, evaluation.Spent["proposal_calls"]);

        var setup = Stage(ledger.Snapshot(), EvolutionResourceStage.Setup);
        Assert.Equal(1, setup.Admitted);
        Assert.Equal(4m, setup.Spent["cost_units"]);
        Assert.Equal(0m, setup.Reserved["cost_units"]);
        Assert.Equal(0m, setup.Spent["proposal_calls"]);

        // Stage totals must reconcile with the ledger-wide totals.
        var snapshot = ledger.Snapshot();
        Assert.Equal(snapshot.Spent["cost_units"], snapshot.Stages.Sum(value => value.Spent["cost_units"]));
        Assert.Equal(snapshot.Reserved["cost_units"], snapshot.Stages.Sum(value => value.Reserved["cost_units"]));
        Assert.Equal(snapshot.Admitted, snapshot.Stages.Sum(value => value.Admitted));
        Assert.Equal(snapshot.Settled, snapshot.Stages.Sum(value => value.Settled));
        Assert.Equal(snapshot.Unknown, snapshot.Stages.Sum(value => value.Unknown));
    }

    [Fact]
    public void OnlyStagesWithWorkAppearAndTheyStayInEnumOrder()
    {
        var ledger = Ledger();
        ledger.TryReserve("v-1", EvolutionResourceStage.Proposal, R(1m, 1m), R(1m, 1m))!.Complete(R(1m, 1m));
        ledger.TryReserve("s-1", EvolutionResourceStage.Setup, R(1m, 0m), R(1m, 0m))!.Complete(R(1m, 0m));
        var stages = ledger.Snapshot().Stages;
        Assert.Equal(new[] { EvolutionResourceStage.Setup, EvolutionResourceStage.Proposal },
            stages.Select(value => value.Stage).ToArray());
        Assert.DoesNotContain(EvolutionResourceStage.Evaluation, stages.Select(value => value.Stage));
    }

    [Fact]
    public void RestoredLedgerRebuildsExactlyTheSameStageTotals()
    {
        var ledger = Ledger();
        ledger.TryReserve("eval-1", EvolutionResourceStage.Evaluation, R(2m, 1m), R(5m, 1m))!.Complete(R(3m, 1m));
        ledger.TryReserve("eval-2", EvolutionResourceStage.Evaluation, R(2m, 1m), R(7m, 1m))!
            .Complete(R(7m, 1m), EvolutionResourceOutcome.Unknown);
        Assert.NotNull(ledger.TryReserve("eval-3", EvolutionResourceStage.Evaluation, R(1m, 1m), R(9m, 1m)));
        ledger.TryReserve("setup-1", EvolutionResourceStage.Setup, R(4m, 0m), R(4m, 0m))!.Complete(R(4m, 0m));

        var restored = new EvolutionResourceLedger("stage-totals", R(1000m, 1000m), retainedReceiptLimit: 4, maximumOperations: 1000);
        restored.RestoreState(ledger.CaptureState());

        var before = ledger.Snapshot().Stages;
        var after = restored.Snapshot().Stages;
        Assert.Equal(before.Count, after.Count);
        for (int index = 0; index < before.Count; index++)
        {
            EvolutionResourceStageSnapshot first = before[index], second = after[index];
            Assert.Equal(first.Stage, second.Stage);
            Assert.Equal(first.Admitted, second.Admitted);
            Assert.Equal(first.Settled, second.Settled);
            Assert.Equal(first.Unknown, second.Unknown);
            Assert.Equal(first.Spent["cost_units"], second.Spent["cost_units"]);
            Assert.Equal(first.Spent["proposal_calls"], second.Spent["proposal_calls"]);
            Assert.Equal(first.Reserved["cost_units"], second.Reserved["cost_units"]);
            Assert.Equal(first.Reserved["proposal_calls"], second.Reserved["proposal_calls"]);
        }
    }

    [Fact]
    public void DeniedAndAbandonedWorkDoNotCorruptStageTotals()
    {
        var ledger = new EvolutionResourceLedger("tight", R(10m, 10m), retainedReceiptLimit: 4, maximumOperations: 1000);
        ledger.TryReserve("eval-1", EvolutionResourceStage.Evaluation, R(8m, 1m), R(8m, 1m))!.Complete(R(8m, 1m));
        Assert.Null(ledger.TryReserve("eval-2", EvolutionResourceStage.Evaluation, R(5m, 1m), R(5m, 1m)));

        var evaluation = Stage(ledger.Snapshot(), EvolutionResourceStage.Evaluation);
        Assert.Equal(1, evaluation.Admitted);   // the denial admitted nothing
        Assert.Equal(1, evaluation.Settled);
        Assert.Equal(8m, evaluation.Spent["cost_units"]);
        Assert.Equal(0m, evaluation.Reserved["cost_units"]);

        // Disposing an unsettled reservation abandons it: charged at maximum and counted unknown.
        using (ledger.TryReserve("eval-3", EvolutionResourceStage.Evaluation, R(1m, 1m), R(2m, 1m))!) { }
        evaluation = Stage(ledger.Snapshot(), EvolutionResourceStage.Evaluation);
        Assert.Equal(2, evaluation.Admitted);
        Assert.Equal(2, evaluation.Settled);
        Assert.Equal(1, evaluation.Unknown);
        Assert.Equal(10m, evaluation.Spent["cost_units"]);
        Assert.Equal(0m, evaluation.Reserved["cost_units"]);
    }
}
