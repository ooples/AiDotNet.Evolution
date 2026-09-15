using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionResourceLedgerTests
{
    private static EvolutionResources Cost(decimal amount) => EvolutionResources.Of("cost_units", amount);
    private static EvolutionResourceLedger Ledger(decimal cap = 10, int retained = 4, int operations = 100) => new("test", Cost(cap), retained, operations);

    [Fact]
    public void ReservesMaximumAndRefundsOnlyMeasuredDifference()
    {
        var ledger = Ledger();
        using var first = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("a", EvolutionResourceStage.Proposal, Cost(2), Cost(6)));
        Assert.Null(ledger.TryReserve("b", EvolutionResourceStage.Evaluation, Cost(5), Cost(5)));
        Assert.Equal(6, ledger.Snapshot().Reserved["cost_units"]);
        Assert.True(first.Complete(Cost(3)));
        using var second = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("b", EvolutionResourceStage.Evaluation, Cost(5), Cost(5)));
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(5, ledger.Snapshot().Reserved["cost_units"]);
        Assert.True(ledger.Snapshot().Receipts[0].ExceededEstimate);
        Assert.False(ledger.Snapshot().MaximumViolated);
    }

    [Fact]
    public void DisposingUnknownWorkNeverMakesItsBudgetFree()
    {
        var ledger = Ledger();
        var reservation = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("a", EvolutionResourceStage.Refinement, Cost(1), Cost(10)));
        reservation.Dispose(); reservation.Dispose();
        Assert.Equal(10, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(0, ledger.Snapshot().Reserved["cost_units"]);
        Assert.Equal(1, ledger.Snapshot().Unknown);
        Assert.Equal(EvolutionResourceOutcome.Unknown, Assert.Single(ledger.Snapshot().Receipts).Outcome);
        Assert.Null(ledger.TryReserve("b", EvolutionResourceStage.Evaluation, Cost(1), Cost(1)));
        Assert.Throws<InvalidOperationException>(() => reservation.Complete(Cost(0)));
    }

    [Fact]
    public void ReceiptsAreExactlyOnceAndConflictingRepliesAreRejected()
    {
        var ledger = Ledger();
        using var reservation = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("a", EvolutionResourceStage.Evaluation, Cost(2), Cost(2)));
        Assert.True(reservation.Complete(Cost(1), EvolutionResourceOutcome.Failed));
        Assert.False(reservation.Complete(Cost(1), EvolutionResourceOutcome.Failed));
        Assert.Throws<InvalidOperationException>(() => reservation.Complete(Cost(2), EvolutionResourceOutcome.Failed));
        Assert.Throws<InvalidOperationException>(() => ledger.TryReserve("a", EvolutionResourceStage.Evaluation, Cost(2), Cost(2)));
        Assert.Equal(1, ledger.Snapshot().Settled);
        Assert.Equal(1, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public void MaximumViolationsAreRetainedAndBlockFurtherDispatch()
    {
        var ledger = Ledger(100);
        using var reservation = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("a", EvolutionResourceStage.Evaluation, Cost(1), Cost(2)));
        reservation.Complete(Cost(3));
        Assert.True(ledger.Snapshot().MaximumViolated);
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        Assert.Null(ledger.TryReserve("b", EvolutionResourceStage.Setup, Cost(0), Cost(0)));
    }

    [Fact]
    public async Task ConcurrentReservationsCannotOversubscribeAnyResource()
    {
        var limits = new EvolutionResources(new Dictionary<string, decimal> { ["cost_units"] = 20, ["tokens"] = 7 });
        var ledger = new EvolutionResourceLedger("test", limits);
        EvolutionResources one = new(new Dictionary<string, decimal> { ["cost_units"] = 1, ["tokens"] = 1 });
        EvolutionResourceReservation?[] reservations = await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            ledger.TryReserve("p" + index, EvolutionResourceStage.Proposal, one, one))));
        Assert.Equal(7, reservations.Count(value => value is not null));
        Assert.Equal(7, ledger.Snapshot().Reserved["tokens"]);
        foreach (var reservation in reservations) reservation?.Complete(one);
        Assert.Equal(7, ledger.Snapshot().Spent["tokens"]);
        Assert.Equal(0, ledger.Snapshot().Reserved["tokens"]);
    }

    [Fact]
    public void BoundedDetailsDoNotTruncateAccountingOrDuplicateProtection()
    {
        var ledger = Ledger(10, retained: 1, operations: 3);
        for (int i = 0; i < 3; i++)
        {
            using var reservation = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("p" + i, EvolutionResourceStage.Screening, Cost(1), Cost(1)));
            reservation.Complete(Cost(1), EvolutionResourceOutcome.Rejected);
        }
        var snapshot = ledger.Snapshot();
        Assert.Equal(3, snapshot.Settled); Assert.Equal(2, snapshot.DroppedReceipts);
        Assert.Equal(3, snapshot.Spent["cost_units"]);
        Assert.Equal("p2", Assert.Single(snapshot.Receipts).OperationId);
        Assert.Throws<InvalidOperationException>(() => ledger.TryReserve("p0", EvolutionResourceStage.Screening, Cost(1), Cost(1)));
        Assert.Null(ledger.TryReserve("p3", EvolutionResourceStage.Screening, Cost(0), Cost(0)));
    }

    [Fact]
    public void SnapshotIsDetachedAndResourceInputIsCopied()
    {
        var amounts = new Dictionary<string, decimal> { ["cost_units"] = 2 };
        var resources = new EvolutionResources(amounts); amounts["cost_units"] = 99;
        Assert.Equal(2, resources["cost_units"]);
        var ledger = Ledger(); var before = ledger.Snapshot();
        using var reservation = ledger.TryReserve("p", EvolutionResourceStage.Proposal, resources, resources);
        Assert.Equal(0, before.Reserved["cost_units"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, decimal>)resources.Amounts).Add("other", 1));
    }

    [Fact]
    public void RestorePreservesPendingAndSettledOperationsAndAllowsBudgetChanges()
    {
        var ledger = Ledger(10, retained: 1);
        using var settled = Assert.IsType<EvolutionResourceReservation>(ledger.TryReserve("z", EvolutionResourceStage.Setup, Cost(2), Cost(2)));
        settled.Complete(Cost(1));
        ledger.TryReserve("a", EvolutionResourceStage.Proposal, Cost(2), Cost(4));
        string state = ledger.CaptureState();
        var restored = Ledger(20, retained: 1); restored.RestoreState(state);
        Assert.Equal(state, restored.CaptureState());
        using var pending = restored.GetPendingReservation("a"); pending.Complete(Cost(3), EvolutionResourceOutcome.Failed);
        Assert.Equal(4, restored.Snapshot().Spent["cost_units"]);
        Assert.Equal(0, restored.Snapshot().Reserved["cost_units"]);
        Assert.Throws<InvalidOperationException>(() => restored.GetPendingReservation("z"));
        Assert.Throws<InvalidOperationException>(() => restored.RestoreState(state));
        var lowered = Ledger(1, retained: 1); lowered.RestoreState(state);
        Assert.Null(lowered.TryReserve("new", EvolutionResourceStage.Setup, Cost(1), Cost(1)));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("negative")]
    [InlineData("duplicate")]
    [InlineData("receipt")]
    [InlineData("outcome")]
    public void MalformedRestoreIsTransactional(string fault)
    {
        var source = Ledger();
        using var reservation = source.TryReserve("a", EvolutionResourceStage.Proposal, Cost(2), Cost(2));
        reservation!.Complete(Cost(1));
        JsonNode state = JsonNode.Parse(source.CaptureState())!;
        JsonNode operation = state["Operations"]![0]!;
        if (fault == "identity") state["ConfigurationHash"] = "other";
        if (fault == "negative") operation["Charged"]!["cost_units"] = -1;
        if (fault == "duplicate") state["Operations"]!.AsArray().Add(operation.DeepClone());
        if (fault == "receipt") state["ReceiptOrder"]![0] = "absent";
        if (fault == "outcome") operation["Outcome"] = 999;
        var restored = Ledger();
        Assert.ThrowsAny<ArgumentException>(() => restored.RestoreState(state.ToJsonString()));
        Assert.Equal(0, restored.Snapshot().Admitted);
        Assert.NotNull(restored.TryReserve("ok", EvolutionResourceStage.Setup, Cost(1), Cost(1)));
    }

    [Fact]
    public void UnknownAndOverrunStateSurviveRoundTrip()
    {
        var source = Ledger();
        source.TryReserve("unknown", EvolutionResourceStage.Proposal, Cost(1), Cost(2))!.Dispose();
        source.TryReserve("overrun", EvolutionResourceStage.Evaluation, Cost(1), Cost(1))!.Complete(Cost(2));
        var restored = Ledger(); restored.RestoreState(source.CaptureState());
        Assert.Equal(source.CaptureState(), restored.CaptureState());
        Assert.True(restored.Snapshot().MaximumViolated); Assert.Equal(1, restored.Snapshot().Unknown);
    }

    [Theory]
    [InlineData(EvolutionResourceStage.Proposal)]
    [InlineData(EvolutionResourceStage.Refinement)]
    [InlineData(EvolutionResourceStage.SurrogateTraining)]
    [InlineData(EvolutionResourceStage.SurrogateInference)]
    [InlineData(EvolutionResourceStage.Novelty)]
    [InlineData(EvolutionResourceStage.Screening)]
    [InlineData(EvolutionResourceStage.Evaluation)]
    [InlineData(EvolutionResourceStage.Confirmation)]
    [InlineData(EvolutionResourceStage.Setup)]
    public async Task EveryStageCanChargeSuccessfulOrUnsuccessfulWork(EvolutionResourceStage stage)
    {
        var ledger = Ledger();
        int value = await EvolutionResourceWork.RunAsync(ledger, "stage/attempt/2", stage, Cost(1), Cost(3),
            _ => new ValueTask<EvolutionResourceResult<int>>(new EvolutionResourceResult<int>(42, Cost(2), EvolutionResourceOutcome.Failed)), attempt: 2);
        Assert.Equal(42, value);
        var receipt = Assert.Single(ledger.Snapshot().Receipts);
        Assert.Equal(stage, receipt.Stage); Assert.Equal(2, receipt.Attempt);
        Assert.Equal(EvolutionResourceOutcome.Failed, receipt.Outcome);
    }

    [Fact]
    public async Task DenialAndPriorCancellationDoNotInvokeWorkAndThrownFailuresStayCharged()
    {
        var ledger = Ledger(1);
        int calls = 0;
        ValueTask<EvolutionResourceResult<int>> Work(CancellationToken _) { calls++; throw new InvalidOperationException("failed"); }
        await Assert.ThrowsAsync<EvolutionResourceBudgetException>(() => EvolutionResourceWork.RunAsync(ledger, "denied", EvolutionResourceStage.Proposal,
            Cost(2), Cost(2), Work).AsTask());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EvolutionResourceWork.RunAsync(ledger, "cancelled", EvolutionResourceStage.Proposal,
            Cost(1), Cost(1), Work, cancellationToken: cancellation.Token).AsTask());
        Assert.Equal(0, calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => EvolutionResourceWork.RunAsync(ledger, "failed", EvolutionResourceStage.Proposal,
            Cost(1), Cost(1), Work).AsTask());
        Assert.Equal(1, calls); Assert.Equal(1, ledger.Snapshot().Unknown);
        Assert.Equal(1, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public void RejectsMalformedResourcesEstimatesAndUndeclaredCharges()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cost(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cost(EvolutionResources.MaximumAmount + 1));
        Assert.Throws<ArgumentException>(() => EvolutionResources.Of("\n", 1));
        Assert.Throws<ArgumentException>(() => new EvolutionResources(Enumerable.Repeat(new KeyValuePair<string, decimal>("x", 1), 2)));
        Assert.Throws<ArgumentException>(() => new EvolutionResources(Enumerable.Range(0, 33).Select(i => new KeyValuePair<string, decimal>("x" + i, 1))));
        var ledger = Ledger();
        Assert.Throws<ArgumentException>(() => ledger.TryReserve("a", EvolutionResourceStage.Setup, Cost(2), Cost(1)));
        Assert.Throws<ArgumentException>(() => ledger.TryReserve("a", EvolutionResourceStage.Setup, EvolutionResources.Of("money", 1), Cost(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryReserve("a", (EvolutionResourceStage)999, Cost(1), Cost(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryReserve("a", EvolutionResourceStage.Setup, Cost(1), Cost(1), attempt: 0));
        Assert.Throws<ArgumentException>(() => ledger.TryReserve(" ", EvolutionResourceStage.Setup, Cost(1), Cost(1)));
        Assert.Equal(0, ledger.Snapshot().Admitted);
    }
}
