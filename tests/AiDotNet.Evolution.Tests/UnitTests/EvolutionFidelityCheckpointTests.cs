using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionFidelityCheckpointTests
{
    private static EvolutionFidelityPlan Plan(double exploration = 0.25) => new(new[] { new EvolutionFidelityLevel("low", 1, 1), new("medium", 3, 3), new("full", 9, 9) },
        0, 1, explorationFraction: exploration);
    private static EvolutionCanonicalGenome<int>[] Candidates() => Enumerable.Range(0, 8).Select(i => new EvolutionCanonicalGenome<int>(i, "candidate-" + i)).ToArray();
    private static EvolutionResourceLedger Ledger(decimal limit = 200, int retained = 1024) => new("checkpoint-test", EvolutionResources.Of("cost_units", limit), retained);
    private sealed class Evaluator
    {
        internal int Calls;
        internal bool FailFirst;
        internal bool UnknownFirst;
        internal ValueTask<EvolutionFidelityEvaluationResult> Evaluate(int genome, EvolutionFidelityEvaluationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            if (UnknownFirst && genome == 0) throw new InvalidOperationException("No receipt available.");
            if (context.Resume is { } state)
            {
                var payload = state.CopyToken();
                Assert.Equal(new[] { (byte)genome, (byte)context.Replicate.Index, (byte)state.SourceLevel.ResourceLevel }, payload);
                Assert.Equal("candidate-" + genome, state.GenomeId);
            }
            if (context.Replicate.Purpose == EvolutionReplicationPurpose.Confirmation) Assert.Null(context.Resume);
            double cost = context.Level.ResourceLevel - (context.Resume?.SourceLevel.ResourceLevel ?? 0);
            var result = EvolutionTaskResult.Completed(FailFirst && genome == 0 ? 2 : genome / 10d + context.Replicate.Index * 0.001,
                new Dictionary<string, double>(), costUnits: cost);
            return new(new EvolutionFidelityEvaluationResult(result, new[] { (byte)genome, (byte)context.Replicate.Index, (byte)context.Level.ResourceLevel }, "state-v1"));
        }
    }
    private static EvolutionFidelityScheduler<int> Scheduler(EvolutionResourceLedger ledger, Evaluator evaluator, EvolutionFidelityPlan? plan = null, string searchVersion = "search") =>
        new(plan ?? Plan(), ledger, searchVersion, "confirmation", "state-v1", evaluator.Evaluate, evaluator.Evaluate);
    private static string Json(object report) => JsonSerializer.Serialize(report);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(14)]
    [InlineData(15)]
    [InlineData(16)]
    public async Task EverySettledPhaseResumesExactlyWithoutRedispatchOrRecharging(int cut)
    {
        var baseline = new Evaluator(); var baselineLedger = Ledger();
        var expected = await Scheduler(baselineLedger, baseline).RunAsync("run", Candidates(), 42);
        var first = new Evaluator(); var firstLedger = Ledger(); EvolutionFidelityCheckpoint? saved = null;
        var paused = await Scheduler(firstLedger, first).RunCheckpointedAsync("run", Candidates(), 42, (checkpoint, _) =>
        {
            saved = EvolutionFidelityCheckpoint.Parse(checkpoint.ToJson());
            Assert.Equal(checkpoint.ToJson(), saved.ToJson());
            Assert.DoesNotContain("PayloadBase64", Json(checkpoint));
            return new(checkpoint.SettledBatchCount < cut);
        });
        Assert.Equal(EvolutionFidelityStopReason.Paused, paused.StopReason); Assert.False(paused.IsComplete); Assert.Null(paused.BestConfirmed);
        Assert.NotNull(saved); Assert.Equal(cut, saved!.SettledBatchCount); Assert.Equal(first.Calls, firstLedger.Snapshot().Settled);
        var second = new Evaluator(); var secondLedger = Ledger(); secondLedger.RestoreState(saved.GetResourceState());
        var actual = await Scheduler(secondLedger, second).RunCheckpointedAsync("run", Candidates(), 42, (_, _) => new(true), saved);
        Assert.Equal(baseline.Calls, first.Calls + second.Calls);
        Assert.Equal(Json(expected), Json(actual));
        Assert.Equal(baselineLedger.CaptureState(), secondLedger.CaptureState());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedAndUnknownBatchesSurvivePauseWithoutBeingPromoted(bool unknown)
    {
        var first = new Evaluator { FailFirst = !unknown, UnknownFirst = unknown }; var ledger = Ledger(); EvolutionFidelityCheckpoint? saved = null;
        var paused = await Scheduler(ledger, first).RunCheckpointedAsync("failed", Candidates(), 1, (checkpoint, _) => { saved = checkpoint; return new(false); });
        Assert.Single(paused.Batches); Assert.False(paused.Batches[0].Measurements.IsComplete);
        var resumedLedger = Ledger(); resumedLedger.RestoreState(saved!.GetResourceState());
        var resumed = await Scheduler(resumedLedger, new Evaluator()).RunCheckpointedAsync("failed", Candidates(), 1, (_, _) => new(true), saved);
        Assert.True(resumed.IsComplete); Assert.DoesNotContain(resumed.Promotions, promotion => promotion.GenomeId == "candidate-0");
        Assert.Equal(Json(paused.Batches[0]), Json(resumed.Batches[0]));
        Assert.Equal(unknown ? 1 : 0, resumed.Resources.Unknown);
    }

    private static async Task<EvolutionFidelityCheckpoint> Checkpoint(int cut = 3, int retained = 1024)
    {
        EvolutionFidelityCheckpoint? saved = null;
        await Scheduler(Ledger(retained: retained), new Evaluator()).RunCheckpointedAsync("run", Candidates(), 42,
            (checkpoint, _) => { saved = checkpoint; return new(checkpoint.SettledBatchCount < cut); });
        return saved!;
    }

    [Fact]
    public async Task ReceiptValidationUsesTombstonesWhenDetailRetentionIsZero()
    {
        var checkpoint = await Checkpoint(retained: 0); var ledger = Ledger(retained: 0); ledger.RestoreState(checkpoint.GetResourceState());
        Assert.Empty(ledger.Snapshot().Receipts);
        var report = await Scheduler(ledger, new Evaluator()).RunCheckpointedAsync("run", Candidates(), 42, (_, _) => new(true), checkpoint);
        Assert.True(report.IsComplete); Assert.Equal(32, report.Resources.Settled); Assert.Equal(32, report.Resources.DroppedReceipts);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("seed")]
    [InlineData("cohort")]
    [InlineData("plan")]
    [InlineData("version")]
    [InlineData("caps")]
    [InlineData("ledger")]
    public async Task ChangedInputsOrUncoordinatedLedgerRejectBeforeDispatch(string changed)
    {
        var checkpoint = await Checkpoint(); var ledger = Ledger(changed == "caps" ? 201 : 200); ledger.RestoreState(checkpoint.GetResourceState());
        if (changed == "ledger") { using var extra = ledger.TryReserve("external", EvolutionResourceStage.Setup, EvolutionResources.Of("cost_units", 1), EvolutionResources.Of("cost_units", 1)); extra!.Complete(EvolutionResources.Of("cost_units", 1)); }
        var evaluator = new Evaluator(); string before = ledger.CaptureState();
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Scheduler(ledger, evaluator, changed == "plan" ? Plan(0) : null, changed == "version" ? "other" : "search")
            .RunCheckpointedAsync(changed == "run" ? "other" : "run", changed == "cohort" ? Enumerable.Reverse(Candidates()).ToArray() : Candidates(),
                changed == "seed" ? 43UL : 42UL, (_, _) => new(true), checkpoint));
        Assert.Equal(0, evaluator.Calls); Assert.Equal(before, ledger.CaptureState());
    }

    private static EvolutionFidelityCheckpoint Mutate(EvolutionFidelityCheckpoint checkpoint, Action<JsonObject> mutate)
    {
        var envelope = JsonNode.Parse(checkpoint.ToJson())!.AsObject();
        var payload = JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!.AsObject(); mutate(payload);
        string json = payload.ToJsonString(); envelope["Payload"] = json;
        envelope["Checksum"] = EvolutionHash.Combine(new[] { "fidelity-checkpoint-v1", json });
        return EvolutionFidelityCheckpoint.Parse(envelope.ToJsonString());
    }

    [Theory]
    [InlineData("cost")]
    [InlineData("identity")]
    [InlineData("chronology")]
    [InlineData("token")]
    [InlineData("source")]
    [InlineData("missing-state")]
    [InlineData("count")]
    [InlineData("status")]
    [InlineData("replicate")]
    public async Task SemanticallyCorruptButRechecksummedPayloadsRejectWithoutNewWork(string corruption)
    {
        var original = await Checkpoint(); var checkpoint = Mutate(original, payload =>
        {
            var first = payload["Batches"]![0]!.AsObject(); var sample = first["Samples"]![0]!.AsObject();
            var token = payload["States"]!["candidate-0"]![0]!.AsObject();
            switch (corruption)
            {
                case "cost": sample["Charged"] = 99; break;
                case "identity": first["BatchIdentity"] = "fake"; break;
                case "chronology": var rows = payload["Batches"]!.AsArray(); var swapped = rows[0]!.DeepClone(); rows[0] = rows[1]!.DeepClone(); rows[1] = swapped; break;
                case "token": token["PayloadBase64"] = Convert.ToBase64String(new byte[] { 99 }); break;
                case "source": token["SourceSampleIdentity"] = "fake"; break;
                case "missing-state": payload["States"]!.AsObject().Remove("candidate-0"); break;
                case "count": first["AcceptedTokens"] = int.MaxValue; break;
                case "status": sample["Status"] = -1; break;
                case "replicate": payload["States"]!["candidate-0"]!.AsArray().RemoveAt(0); break;
            }
        });
        var ledger = Ledger(); ledger.RestoreState(original.GetResourceState()); var evaluator = new Evaluator(); string before = ledger.CaptureState();
        await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Scheduler(ledger, evaluator).RunCheckpointedAsync("run", Candidates(), 42, (_, _) => new(true), checkpoint));
        Assert.Equal(0, evaluator.Calls); Assert.Equal(before, ledger.CaptureState());
    }

    [Fact]
    public async Task SinkFailureOrMutationStopsBeforeNextBatchAndKeepsCharges()
    {
        var evaluator = new Evaluator(); var ledger = Ledger();
        await Assert.ThrowsAsync<IOException>(async () => await Scheduler(ledger, evaluator).RunCheckpointedAsync("run", Candidates(), 42,
            (_, _) => throw new IOException("store unavailable")));
        Assert.Equal(2, evaluator.Calls); Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
        evaluator = new Evaluator(); ledger = Ledger();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Scheduler(ledger, evaluator).RunCheckpointedAsync("run", Candidates(), 42, (_, _) =>
        {
            using var extra = ledger.TryReserve("sink", EvolutionResourceStage.Setup, EvolutionResources.Of("cost_units", 1), EvolutionResources.Of("cost_units", 1));
            extra!.Complete(EvolutionResources.Of("cost_units", 1)); return new(false);
        }));
        Assert.Equal(2, evaluator.Calls); Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task EnvelopeRejectsChecksumMismatchDuplicateUnknownAndOversizedInput()
    {
        var checkpoint = await Checkpoint();
        Assert.Throws<ArgumentException>(() => EvolutionFidelityCheckpoint.Parse(checkpoint.ToJson().Replace("candidate-0", "tampered")));
        Assert.Throws<ArgumentException>(() => EvolutionFidelityCheckpoint.Parse("{\"SchemaVersion\":1," + checkpoint.ToJson().Substring(1)));
        Assert.Throws<ArgumentException>(() => EvolutionFidelityCheckpoint.Parse(new string('x', 16 * 1024 * 1024 + 1)));
        Assert.Throws<JsonException>(() => Mutate(checkpoint, payload => payload["Unknown"] = true));
    }

    [Fact]
    public async Task CancellationAtASavedBoundaryRetainsChargesAndCanResumeCoherently()
    {
        var ledger = Ledger(); var evaluator = new Evaluator(); EvolutionFidelityCheckpoint? saved = null;
        using var cancellation = new CancellationTokenSource();
        var canceled = await Scheduler(ledger, evaluator).RunCheckpointedAsync("run", Candidates(), 42, (checkpoint, _) =>
        { saved = checkpoint; cancellation.Cancel(); return new(true); }, cancellationToken: cancellation.Token);
        Assert.Equal(EvolutionFidelityStopReason.Canceled, canceled.StopReason); Assert.Null(canceled.BestConfirmed);
        Assert.Equal(2, evaluator.Calls); Assert.Equal(2, canceled.Resources.Spent["cost_units"]);
        var resumed = await Scheduler(ledger, evaluator).RunCheckpointedAsync("run", Candidates(), 42, (_, _) => new(true), saved);
        Assert.True(resumed.IsComplete); Assert.Equal(32, evaluator.Calls);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Scheduler(Ledger(), new Evaluator()).RunCheckpointedAsync("run", Candidates(), 42,
            (_, _) => new(true), cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ExternalPendingWorkCannotBeCapturedAsASettledCheckpoint()
    {
        var ledger = Ledger(); var evaluator = new Evaluator(); int checkpoints = 0;
        using var external = ledger.TryReserve("external", EvolutionResourceStage.Setup, EvolutionResources.Of("cost_units", 1), EvolutionResources.Of("cost_units", 1));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Scheduler(ledger, evaluator).RunCheckpointedAsync("run", Candidates(), 42,
            (_, _) => { checkpoints++; return new(false); }));
        Assert.Equal(0, checkpoints); Assert.Equal(2, evaluator.Calls);
        Assert.Equal(1, ledger.Snapshot().Reserved["cost_units"]); Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
    }
}
