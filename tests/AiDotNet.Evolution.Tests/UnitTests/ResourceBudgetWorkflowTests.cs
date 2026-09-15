using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class ResourceBudgetWorkflowTests
{
    private static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);
    private static EvolutionResourceLedger Ledger(decimal cap = 10, int retained = 0) => new("budget-workflow", Cost(cap), retained);
    private static EvolutionResourceRequest Request(string id, decimal maximum = 3) => new(id, EvolutionResourceStage.Proposal, Cost(1), Cost(maximum));

    [Theory]
    [InlineData(EvolutionResourceStage.Setup)]
    [InlineData(EvolutionResourceStage.Proposal)]
    [InlineData(EvolutionResourceStage.Refinement)]
    [InlineData(EvolutionResourceStage.SurrogateTraining)]
    [InlineData(EvolutionResourceStage.SurrogateInference)]
    [InlineData(EvolutionResourceStage.Novelty)]
    [InlineData(EvolutionResourceStage.Screening)]
    [InlineData(EvolutionResourceStage.Evaluation)]
    [InlineData(EvolutionResourceStage.Confirmation)]
    [InlineData(EvolutionResourceStage.Persistence)]
    public async Task OverrunIsRetainedButNeverReturnedAsUsableStageValue(EvolutionResourceStage stage)
    {
        var ledger = Ledger(100);
        var error = await Assert.ThrowsAsync<EvolutionResourceLimitExceededException>(() =>
            EvolutionResourceWork.RunAsync(ledger, "attempt-1", stage, Cost(1), Cost(2),
                _ => new ValueTask<EvolutionResourceResult<int>>(new EvolutionResourceResult<int>(42, Cost(3)))).AsTask());
        Assert.Equal("attempt-1", error.OperationId);
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        Assert.True(ledger.Snapshot().MaximumViolated);
        Assert.Equal(0, ledger.Snapshot().Unknown);
        Assert.Null(ledger.TryReserve("next", stage, Cost(0), Cost(0)));
    }

    [Fact]
    public async Task AllStageTotalsAndRetriesSurviveZeroReceiptRetentionAndRestore()
    {
        var ledger = Ledger(100);
        foreach (EvolutionResourceStage stage in Enum.GetValues(typeof(EvolutionResourceStage)))
            for (int attempt = 1; attempt <= 2; attempt++)
                await EvolutionResourceWork.RunAsync(ledger, stage + "/" + attempt, stage, Cost(1), Cost(2),
                    _ => new ValueTask<EvolutionResourceResult<int>>(new EvolutionResourceResult<int>(0, Cost(1), EvolutionResourceOutcome.Rejected)), attempt);
        ledger.TryReserve("unreported-model", EvolutionResourceStage.Proposal, Cost(1), Cost(2))!.Dispose();
        var restored = Ledger(100); restored.RestoreState(ledger.CaptureState());
        var snapshot = restored.Snapshot();
        Assert.Empty(snapshot.Receipts);
        Assert.Equal(snapshot.Settled, snapshot.DroppedReceipts);
        Assert.Equal(10, snapshot.Stages.Count);
        Assert.Equal(snapshot.Spent["cost_units"], snapshot.Stages.Sum(stage => stage.Spent["cost_units"]));
        Assert.Equal(snapshot.Unknown, snapshot.Stages.Sum(stage => stage.Unknown));
        Assert.Equal(4, snapshot.Stages.Single(stage => stage.Stage == EvolutionResourceStage.Proposal).Spent["cost_units"]);
        Assert.All(snapshot.Stages, stage => Assert.Equal(0, stage.Reserved["cost_units"]));
    }

    [Fact]
    public async Task WaveAdmissionDoesNotDependOnEnumerationOrWorkerCompletionOrder()
    {
        foreach (bool reverse in new[] { false, true })
        {
            var ledger = Ledger(6);
            var requests = new[] { Request("03"), Request("01"), Request("02") };
            var wave = ledger.ReserveBatch(reverse ? requests.Reverse() : requests);
            Assert.Equal(new[] { "01", "02", "03" }, wave.Select(row => row.Request.OperationId));
            Assert.Equal(new[] { true, true, false }, wave.Select(row => row.Reservation is not null));
            Assert.Equal(6, ledger.Snapshot().Reserved["cost_units"]);
            var accepted = wave.Where(row => row.Reservation is not null).ToArray();
            await Task.WhenAll((reverse ? accepted.Reverse() : accepted).Select(row => Task.Run(() => row.Reservation!.Complete(Cost(1)))));
            Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
            Assert.Null(wave[2].Reservation); // released maxima do not retroactively backfill this wave
            using var next = ledger.TryReserve("04", EvolutionResourceStage.Proposal, Cost(1), Cost(3));
            Assert.NotNull(next);
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("undeclared")]
    [InlineData("estimate")]
    [InlineData("known")]
    [InlineData("empty")]
    [InlineData("oversized")]
    public void InvalidWaveMutatesNoAccounting(string mode)
    {
        var ledger = Ledger();
        if (mode == "known") ledger.TryReserve("known", EvolutionResourceStage.Setup, Cost(0), Cost(0))!.Complete(Cost(0));
        string before = ledger.CaptureState();
        EvolutionResourceRequest[] requests = mode switch
        {
            "duplicate" => new[] { Request("same"), Request("same") },
            "undeclared" => new[] { Request("ok"), new EvolutionResourceRequest("bad", EvolutionResourceStage.Setup, Cost(0), EvolutionResources.Of("money", 1)) },
            "estimate" => new[] { Request("ok"), new EvolutionResourceRequest("bad", EvolutionResourceStage.Setup, Cost(2), Cost(1)) },
            "known" => new[] { Request("new"), Request("known") },
            "empty" => Array.Empty<EvolutionResourceRequest>(),
            _ => Enumerable.Range(0, 1025).Select(i => Request("id" + i)).ToArray()
        };
        Assert.ThrowsAny<Exception>(() => ledger.ReserveBatch(requests));
        Assert.Equal(before, ledger.CaptureState());
    }

    [Fact]
    public void PendingWorkPreventsEngineCaptureAndReentrantWorkIsNeverRolledBack()
    {
        var ledger = Ledger();
        using var pending = ledger.TryReserve("pending", EvolutionResourceStage.Evaluation, Cost(1), Cost(2));
        int captures = 0;
        Assert.Throws<InvalidOperationException>(() => EvolutionResourceBoundary.Capture(ledger, () => { captures++; return "engine"; }));
        Assert.Equal(0, captures);
        pending!.Complete(Cost(1));
        Assert.Throws<InvalidOperationException>(() => EvolutionResourceBoundary.Capture(ledger, () =>
        {
            ledger.TryReserve("reentrant", EvolutionResourceStage.Setup, Cost(1), Cost(1))!.Complete(Cost(1));
            return "engine";
        }));
        Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public void BoundaryBindsBothStatesAndCannotOverwriteOrEraseSpendOnRestore()
    {
        var ledger = Ledger();
        ledger.TryReserve("compiled", EvolutionResourceStage.Setup, Cost(2), Cost(2))!.Complete(Cost(2));
        var boundary = EvolutionResourceBoundary.Capture(ledger, () => "{\"engine-sequence\":17}");
        var loaded = EvolutionResourceBoundary.Deserialize(boundary.Serialize(), boundary.Checksum);
        Assert.Equal(boundary.EngineState, loaded.EngineState);
        var restored = loaded.RestoreLedger(Cost(1), retainedReceiptLimit: 0);
        Assert.Equal(2, restored.Snapshot().Spent["cost_units"]);
        Assert.Null(restored.TryReserve("new", EvolutionResourceStage.Setup, Cost(0), Cost(0)));
        Assert.Throws<InvalidOperationException>(() => restored.TryReserve("compiled", EvolutionResourceStage.Setup, Cost(0), Cost(0)));
        string root = Path.Combine(Path.GetTempPath(), "evolution-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "boundary.json");
            boundary.SaveNew(path);
            string before = File.ReadAllText(path);
            Assert.Throws<IOException>(() => boundary.SaveNew(path));
            Assert.Equal(before, File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(root));
            Assert.Equal(boundary.Checksum, EvolutionResourceBoundary.Deserialize(before, boundary.Checksum).Checksum);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("EngineState")]
    [InlineData("LedgerState")]
    [InlineData("RunId")]
    [InlineData("Checksum")]
    public void ChangedBoundaryCannotBeRestored(string field)
    {
        var boundary = EvolutionResourceBoundary.Capture(Ledger(), () => "engine");
        var payload = JsonNode.Parse(boundary.Serialize())!;
        payload[field] = "changed";
        Assert.ThrowsAny<Exception>(() => EvolutionResourceBoundary.Deserialize(payload.ToJsonString(), boundary.Checksum));
    }
}
