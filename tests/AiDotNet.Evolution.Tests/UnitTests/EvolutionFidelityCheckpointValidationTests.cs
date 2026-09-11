using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionFidelityCheckpointValidationTests
{
    public enum CheckpointCorruption
    {
        CompletedBatchMissingReplicate,
        IncompleteBatchAcceptsContinuation,
        ConfirmationAtSearchLevel,
        ConfirmationAcceptsContinuation,
        ConfirmationRejectsContinuation,
        ConfirmationHasPriorSample,
        MissingStatus,
        UndefinedStatus,
        MissingCharge,
        NegativeCharge,
        ChargeExceedsMaximum,
        MissingUnknownFlag,
        NegativeReportedCost,
        MissingReportedCost,
        ReportedCostExceedsDecimalRange,
        ReportedCostUnderflowsDecimal,
        ReportedCostDiffersFromReceipt,
        UnknownFlagDiffersFromReceipt,
        AcceptedSampleFailed,
        AcceptedSampleMissingQuality,
        AcceptedSampleQualityBelowMinimum,
        AcceptedSampleQualityAboveMaximum,
        ReusedOrigin,
        OriginHasMultipleSamples,
        OriginHasWrongSampleIdentity,
        OriginScopeChangesWithinBatch
    }

    public static TheoryData<CheckpointCorruption> Corruptions
    {
        get
        {
            var cases = new TheoryData<CheckpointCorruption>();
            foreach (CheckpointCorruption corruption in Enum.GetValues(typeof(CheckpointCorruption))) cases.Add(corruption);
            return cases;
        }
    }

    private sealed class RunFixture
    {
        private readonly double _cost;
        private readonly double _quality;

        internal RunFixture(double cost = 1, double quality = 0.5)
        {
            _cost = cost;
            _quality = quality;
            Ledger = new("ordered-checkpoint-validation", EvolutionResources.Of("cost_units", 100));
            var plan = new EvolutionFidelityPlan(new[] { new EvolutionFidelityLevel("low", 1, 1), new("full", 2, 1) }, 0, 1);
            Scheduler = new(plan, Ledger, "search", "confirmation", "state-v1", Evaluate, Evaluate);
        }

        internal EvolutionResourceLedger Ledger { get; }
        internal EvolutionFidelityScheduler<int> Scheduler { get; }
        internal int Calls { get; private set; }
        internal int CheckpointWrites { get; private set; }
        internal EvolutionFidelityCheckpoint? Saved { get; private set; }

        private ValueTask<EvolutionFidelityEvaluationResult> Evaluate(int genome, EvolutionFidelityEvaluationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            return new(new EvolutionFidelityEvaluationResult(EvolutionTaskResult.Completed(_quality,
                new Dictionary<string, double>(), costUnits: _cost)));
        }

        internal ValueTask<EvolutionFidelityReport<int>> Run(EvolutionFidelityCheckpoint? checkpoint = null, int pauseAfter = int.MaxValue) =>
            Scheduler.RunCheckpointedAsync("ordered-validation", Candidates(), 42, (saved, _) =>
            {
                CheckpointWrites++;
                Saved = saved;
                return new(saved.SettledBatchCount < pauseAfter);
            }, checkpoint);
    }

    private static EvolutionCanonicalGenome<int>[] Candidates() => new[]
    {
        new EvolutionCanonicalGenome<int>(0, "candidate-0"),
        new EvolutionCanonicalGenome<int>(1, "candidate-1")
    };

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task RechecksummedInvalidLastBatchRejectsBeforeDispatchOrMutationAndAllowsValidRetry(CheckpointCorruption corruption)
    {
        // A zero-charge receipt makes the positive-double-to-decimal underflow case meaningful:
        // dropping only the underflow check would otherwise accept the forged zero-charge sample.
        double cost = corruption == CheckpointCorruption.ReportedCostUnderflowsDecimal ? 0 : 1;
        var first = new RunFixture(cost);
        var paused = await first.Run(pauseAfter: 5);
        Assert.Equal(EvolutionFidelityStopReason.Paused, paused.StopReason);
        Assert.Equal(5, paused.Batches.Count);
        Assert.Equal(EvolutionReplicationPurpose.Confirmation, paused.Batches[4].Purpose);
        var original = first.Saved ?? throw new InvalidOperationException("The pause must produce a checkpoint.");
        var data = original.ReadData();
        var batches = data.Batches ?? throw new InvalidOperationException("The checkpoint must contain batches.");
        var last = batches[batches.Length - 1];
        var samples = last.Samples ?? throw new InvalidOperationException("The complete batch must contain samples.");
        var prior = last.PriorSamples ?? throw new InvalidOperationException("The batch must contain prior-sample slots.");
        Assert.Equal(2, samples.Length);
        Assert.Equal(0, last.AcceptedTokens);
        Assert.Equal(0, last.RejectedTokens);
        var sample = samples[1];
        string sampleId = paused.Batches[4].Measurements.Samples[1].Context.SampleIdentity;

        switch (corruption)
        {
            case CheckpointCorruption.CompletedBatchMissingReplicate:
                last.Samples = samples.Take(1).ToArray(); last.PriorSamples = prior.Take(1).ToArray(); break;
            case CheckpointCorruption.IncompleteBatchAcceptsContinuation:
                last.StopReason = EvolutionReplicationStopReason.InvalidMeasurement; last.AcceptedTokens = 1; break;
            case CheckpointCorruption.ConfirmationAtSearchLevel: last.Rung = 0; break;
            case CheckpointCorruption.ConfirmationAcceptsContinuation: last.AcceptedTokens = 1; break;
            case CheckpointCorruption.ConfirmationRejectsContinuation: last.RejectedTokens = 1; break;
            case CheckpointCorruption.ConfirmationHasPriorSample: prior[1] = "forged-source"; break;
            case CheckpointCorruption.MissingStatus: sample.Status = null; break;
            case CheckpointCorruption.UndefinedStatus: sample.Status = (EvolutionEvaluationStatus)(-1); break;
            case CheckpointCorruption.MissingCharge: sample.Charged = null; break;
            case CheckpointCorruption.NegativeCharge: sample.Charged = -1; break;
            case CheckpointCorruption.ChargeExceedsMaximum: sample.Charged = 2; break;
            case CheckpointCorruption.MissingUnknownFlag: sample.Unknown = null; break;
            case CheckpointCorruption.NegativeReportedCost: sample.Reported = -1; break;
            case CheckpointCorruption.MissingReportedCost: sample.Reported = null; break;
            case CheckpointCorruption.ReportedCostExceedsDecimalRange: sample.Reported = double.MaxValue; break;
            case CheckpointCorruption.ReportedCostUnderflowsDecimal:
                Assert.Equal(0m, sample.Charged); sample.Reported = double.Epsilon; break;
            case CheckpointCorruption.ReportedCostDiffersFromReceipt: sample.Reported = 0.5; break;
            case CheckpointCorruption.UnknownFlagDiffersFromReceipt: sample.Unknown = true; break;
            case CheckpointCorruption.AcceptedSampleFailed: sample.Status = EvolutionEvaluationStatus.Failed; break;
            case CheckpointCorruption.AcceptedSampleMissingQuality: sample.Quality = null; break;
            case CheckpointCorruption.AcceptedSampleQualityBelowMinimum: sample.Quality = -0.1; break;
            case CheckpointCorruption.AcceptedSampleQualityAboveMaximum: sample.Quality = 1.1; break;
            case CheckpointCorruption.ReusedOrigin:
                sample.OriginJson = Origin(new[] { sampleId }, kind: EvolutionMeasurementOriginKind.PersistentReuse).ToJson(); break;
            case CheckpointCorruption.OriginHasMultipleSamples:
                sample.OriginJson = Origin(new[] { sampleId, "another-observation" }).ToJson(); break;
            case CheckpointCorruption.OriginHasWrongSampleIdentity:
                sample.OriginJson = Origin(new[] { "another-observation" }).ToJson(); break;
            case CheckpointCorruption.OriginScopeChangesWithinBatch:
                samples[0].OriginJson = Origin(new[] { paused.Batches[4].Measurements.Samples[0].Context.SampleIdentity }).ToJson();
                sample.OriginJson = Origin(new[] { sampleId }, scope: "other-scope").ToJson(); break;
            default: throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        // Recreate and parse the checksum-valid envelope, so a checksum rejection cannot mask
        // a missing semantic guard in ReadCheckpoint.
        var forged = EvolutionFidelityCheckpoint.Parse(new EvolutionFidelityCheckpoint(data).ToJson());
        Assert.NotEqual(original.Checksum, forged.Checksum);
        var resumed = new RunFixture(cost);
        resumed.Ledger.RestoreState(original.GetResourceState());
        string ledgerBefore = resumed.Ledger.CaptureState();
        var exception = await Assert.ThrowsAsync<ArgumentException>(async () => await resumed.Run(forged));
        Assert.Equal("checkpoint", exception.ParamName);
        Assert.Equal(0, resumed.Calls);
        Assert.Equal(0, resumed.CheckpointWrites);
        Assert.Equal(ledgerBefore, resumed.Ledger.CaptureState());

        // Reuse the same scheduler and ledger after rejection; no partially restored prefix
        // or moments may leak into the accepted continuation.
        var actual = await resumed.Run(original);
        var uninterrupted = new RunFixture(cost);
        var expected = await uninterrupted.Run();
        Assert.True(actual.IsComplete);
        Assert.Equal(uninterrupted.Calls, first.Calls + resumed.Calls);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(uninterrupted.Ledger.CaptureState(), resumed.Ledger.CaptureState());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public async Task InclusiveQualityAndChargeBoundariesRetainExactRestartEvidence(double cost, double quality)
    {
        var first = new RunFixture(cost, quality);
        var paused = await first.Run(pauseAfter: 5);
        Assert.Equal(EvolutionFidelityStopReason.Paused, paused.StopReason);
        var checkpoint = first.Saved ?? throw new InvalidOperationException("The pause must produce a checkpoint.");
        var resumed = new RunFixture(cost, quality);
        resumed.Ledger.RestoreState(checkpoint.GetResourceState());
        var actual = await resumed.Run(checkpoint);
        var uninterrupted = new RunFixture(cost, quality);
        var expected = await uninterrupted.Run();
        Assert.True(actual.IsComplete);
        Assert.Equal(uninterrupted.Calls, first.Calls + resumed.Calls);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(uninterrupted.Ledger.CaptureState(), resumed.Ledger.CaptureState());
    }

    [Fact]
    public async Task ValidMeasuredOriginsAreRestoredWithoutRedispatchOrRechargingTheirSamples()
    {
        var first = new RunFixture();
        var paused = await first.Run(pauseAfter: 5);
        var original = first.Saved ?? throw new InvalidOperationException("The pause must produce a checkpoint.");
        var data = original.ReadData();
        var batches = data.Batches ?? throw new InvalidOperationException("The checkpoint must contain batches.");
        var samples = batches[4].Samples ?? throw new InvalidOperationException("The complete batch must contain samples.");
        for (int i = 0; i < samples.Length; i++)
            samples[i].OriginJson = Origin(new[] { paused.Batches[4].Measurements.Samples[i].Context.SampleIdentity }).ToJson();
        var checkpoint = EvolutionFidelityCheckpoint.Parse(new EvolutionFidelityCheckpoint(data).ToJson());
        var resumed = new RunFixture();
        resumed.Ledger.RestoreState(original.GetResourceState());
        var actual = await resumed.Run(checkpoint);
        Assert.True(actual.IsComplete);
        Assert.Equal(2, resumed.Calls);
        Assert.Equal(12, actual.Resources.Settled);
        Assert.Equal(12m, actual.Resources.Spent["cost_units"]);
        for (int i = 0; i < samples.Length; i++)
        {
            var restoredOrigin = actual.Batches[4].Measurements.Samples[i].MeasurementOrigin;
            Assert.NotNull(restoredOrigin);
            Assert.Equal(samples[i].OriginJson, restoredOrigin.ToJson());
        }
    }

    private static EvolutionMeasurementOrigin Origin(IEnumerable<string> sampleIds, string scope = "scope",
        EvolutionMeasurementOriginKind kind = EvolutionMeasurementOriginKind.Measured) =>
        new(EvolutionHash.Combine(new[] { scope }), "ordered-validation", "evaluation", sampleIds,
            DateTimeOffset.FromUnixTimeSeconds(1), 1, "cost_units", "statistics-v1", kind);
}
