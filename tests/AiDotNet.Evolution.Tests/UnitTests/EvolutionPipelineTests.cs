using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionPipelineTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(17)]
    public async Task SmallEvaluationBudgetsNeverOverAdmit(int budget)
    {
        var variation = new ProbeVariation(true);
        var options = Options(); options.MaxEvaluationAttempts = budget;
        var engine = Engine(new ProbeTask(variation), variation, options);
        var run = await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(budget, run.Counters.EvaluationAttempts); Assert.Equal(budget, run.Counters.Proposals);
        Assert.Equal(EvolutionStopReason.EvaluationBudgetReached, run.StopReason);
        Assert.Equal(Math.Max(0, budget - 1), variation.Observed.Count);
    }

    [Fact]
    public async Task OpportunisticModeRecordsItsActualLearningCommitOrder()
    {
        var variation = new ProbeVariation(true);
        var options = Options(); options.ExecutionMode = EvolutionExecutionMode.Opportunistic;
        var engine = Engine(new ProbeTask(variation, reversed: true), variation, options);
        await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(EvolutionExecutionMode.Opportunistic, engine.PipelineReport!.ExecutionMode);
        var committed = engine.PipelineReport.Schedule.Where(entry => entry.Kind == EvolutionPipelineScheduleKind.Commit && entry.Generation > 0);
        Assert.Equal(variation.Observed, committed.Select(entry => entry.Generation));
        Assert.Equal(16, variation.Observed.Distinct().Count());
    }

    [Fact]
    public async Task RateGateSpacesDispatchPermitsAndCancellationDoesNotLeakTheGate()
    {
        using var gate = new EvolutionPipelineRateGate(TimeSpan.FromMilliseconds(20));
        var timer = Stopwatch.StartNew();
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        await gate.WaitAsync(CancellationToken.None);
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(40));
        using var cancellation = new CancellationTokenSource();
        Task waiting = gate.WaitAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await gate.WaitAsync(guard.Token);
        using var disabled = new EvolutionPipelineRateGate(TimeSpan.Zero);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disabled.WaitAsync(cancellation.Token));
    }

    [Fact]
    public async Task BothPipelineStagesCanBeRateLimitedWithoutChangingLogicalResults()
    {
        string? expected = null;
        foreach (bool limited in new[] { false, true })
        {
            var variation = new ProbeVariation(true);
            var options = Options(); options.MaxProposals = 5;
            if (limited)
            {
                options.Pipeline.MinimumProposalStartInterval = TimeSpan.FromMilliseconds(10);
                options.Pipeline.MinimumEvaluationStartInterval = TimeSpan.FromMilliseconds(10);
            }
            var engine = Engine(new ProbeTask(variation), variation, options);
            var result = await engine.RunAsync(new[] { new TestGenome(1) });
            string genomes = string.Join(",", result.Islands.SelectMany(island => island.Entries).Select(entry => entry.Evaluation.GenomeId));
            expected ??= genomes; Assert.Equal(expected, genomes);
            Assert.Equal(4, engine.PipelineReport!.ProposalCalls); Assert.Equal(5, engine.PipelineReport.EvaluationCalls);
        }
    }

    [Fact]
    public async Task IndependentProposalAndEvaluatorSlotsOverlapWithoutRacingLearning()
    {
        var variation = new ProbeVariation(concurrent: true, proveOverlap: true);
        var task = new ProbeTask(variation);
        var engine = Engine(task, variation, Options());
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = await engine.RunAsync(new[] { new TestGenome(1) }, guard.Token);
        Assert.Equal(17, run.Counters.Proposals);
        Assert.True(variation.Peak >= 4);
        Assert.True(task.SawGenerationOverlap);
        Assert.Equal(16, variation.Observed.Count);
        var report = Assert.IsType<EvolutionPipelineReport>(engine.PipelineReport);
        Assert.Equal("pipeline-test", report.RunId); Assert.Equal(engine.CompatibilityHash, report.CompatibilityHash);
        Assert.Equal(0, report.FirstEvaluationId); Assert.Equal(8, report.WaveSize); Assert.Equal(2, report.ProposalQueueCapacity);
        Assert.Equal(2, report.EvaluationQueueCapacity); Assert.Equal(0, report.AbortedWaves);
        Assert.Equal(16, report.ProposalCalls); Assert.Equal(17, report.EvaluationCalls);
        Assert.InRange(report.ProposalQueuePeak, 0, 2); Assert.InRange(report.EvaluationQueuePeak, 0, 2);
        Assert.InRange(report.ProposalRunningPeak, 2, 4); Assert.InRange(report.EvaluationRunningPeak, 1, 3);
        Assert.True(report.IsScheduleComplete); Assert.Equal(34, report.Schedule.Count);
        Assert.True(report.ProposalBusySeconds > 0); Assert.True(report.EvaluationBusySeconds > 0);
    }

    [Fact]
    public async Task OrdinaryStatefulOperatorsRemainSerializedAndSnapshotsDoNotMove()
    {
        var variation = new ProbeVariation(concurrent: false);
        var engine = Engine(new ProbeTask(variation), variation, Options());
        await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(1, variation.Peak); Assert.Equal(1, engine.PipelineReport!.ProposalWorkers);
        Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value), variation.Observed);
        foreach (var context in variation.Contexts.Values)
        {
            Assert.IsType<EvolutionArchiveSnapshot<TestGenome>>(context.Archive);
            Assert.NotNull(context.ProposalIdentity); Assert.Equal(context.Generation, context.EvaluationId);
            Assert.Equal(context.Generation <= 8 ? 1 : 9, context.Archive!.Version);
            Assert.DoesNotContain(context.Archive.Entries, entry => entry.Evaluation.EvaluationId >= context.EvaluationId);
        }
        Assert.Equal(16, variation.Contexts.Values.Select(context => context.ProposalIdentity).Distinct().Count());
    }

    [Fact]
    public async Task DeterministicScheduleFeedbackAndStateIgnoreEvaluatorTimingAndWorkerCount()
    {
        string? hash = null, schedule = null;
        foreach (int workers in new[] { 1, 2, 4 })
        {
            var variation = new ProbeVariation(true);
            var options = Options(); options.MaxDegreeOfParallelism = workers;
            var engine = Engine(new ProbeTask(variation, reversed: workers == 2), variation, options);
            var run = await engine.RunAsync(new[] { new TestGenome(1) });
            hash ??= run.StateHash; schedule ??= JsonSerializer.Serialize(engine.PipelineReport!.Schedule);
            Assert.Equal(hash, run.StateHash); Assert.Equal(schedule, JsonSerializer.Serialize(engine.PipelineReport!.Schedule));
            Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value), variation.Observed);
        }
    }

    [Fact]
    public async Task ProposalIdentityChangesWhenRecordedParentEvidenceChangesEvenWithTheSameArchiveVersion()
    {
        var identities = new List<string>();
        foreach (double observedOffset in new[] { 0d, 1d })
        {
            var variation = new ProbeVariation(true);
            var options = Options(); options.MaxProposals = 2;
            await Engine(new ProbeTask(variation, observedOffset: observedOffset), variation, options).RunAsync(new[] { new TestGenome(1) });
            Assert.Equal(1, variation.Contexts[1].Archive!.Version);
            identities.Add(variation.Contexts[1].ProposalIdentity!);
        }
        Assert.NotEqual(identities[0], identities[1]);
    }

    [Fact]
    public async Task RetryRoundsRespectTheSeparateEvaluatorQueueBound()
    {
        var variation = new ProbeVariation(true);
        var options = Options(); options.MaxProposals = 9; options.MaxEvaluationAttempts = 30; options.MaxRetries = 1;
        options.MaxDegreeOfParallelism = 2; options.Pipeline.EvaluationQueueCapacity = 1;
        var engine = Engine(new ProbeTask(variation, failFirst: true), variation, options);
        var run = await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(18, run.Counters.EvaluationAttempts); Assert.Equal(9, run.Counters.CompletedEvaluations);
        Assert.Equal(18, engine.PipelineReport!.EvaluationCalls);
        Assert.InRange(engine.PipelineReport.EvaluationQueuePeak, 0, 1);
        Assert.Equal(8, variation.Observed.Count);
    }

    [Fact]
    public async Task CancellationDrainsProposalWorkersBeforeCheckpointAndNeverObservesUncommittedWork()
    {
        var variation = new ProbeVariation(true, cancelAtProposal: true);
        var store = new InMemoryEvolutionCheckpointStore();
        var engine = Engine(new ProbeTask(variation), variation, Options(), store);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task run = engine.RunAsync(new[] { new TestGenome(1) }, cancellation.Token);
        await variation.FirstProposal.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, variation.Active); Assert.Empty(variation.Observed);
        var report = Assert.IsType<EvolutionPipelineReport>(engine.PipelineReport);
        Assert.Equal(1, report.AbortedWaves);
        Assert.Equal(1, report.CanceledTaskDrains);
        Assert.Equal(0, report.FaultedTaskDrains);
        Assert.Equal("canceled", Assert.Single(report.Schedule, entry => entry.Kind == EvolutionPipelineScheduleKind.WaveAborted).Identity);
        Assert.NotNull(await store.LoadLatestAsync("pipeline-test"));
    }

    [Fact]
    public async Task TightRetryBudgetsDoNotFavorDifferentCandidatesAtDifferentWorkerCounts()
    {
        string? expected = null;
        foreach (int workers in new[] { 1, 4 })
        {
            var variation = new ProbeVariation(true);
            var options = Options(); options.MaxProposals = 5; options.MaxEvaluationAttempts = 10; options.MaxRetries = 2;
            options.MaxDegreeOfParallelism = workers; options.Pipeline.WaveSize = 4; options.Pipeline.EvaluationQueueCapacity = 1;
            var engine = Engine(new ProbeTask(variation, hardFailures: true), variation, options);
            var run = await engine.RunAsync(new[] { new TestGenome(1) });
            expected ??= run.StateHash;
            Assert.Equal(expected, run.StateHash);
            Assert.Equal(10, run.Counters.EvaluationAttempts);
        }
    }

    [Fact]
    public async Task WaveBoundaryCheckpointResumesWithTheUninterruptedState()
    {
        var wholeVariation = new ProbeVariation(true);
        var expected = await Engine(new ProbeTask(wholeVariation), wholeVariation, Options()).RunAsync(new[] { new TestGenome(1) });
        var store = new InMemoryEvolutionCheckpointStore();
        var firstVariation = new ProbeVariation(true);
        var firstOptions = Options(); firstOptions.MaxProposals = 9;
        await Engine(new ProbeTask(firstVariation), firstVariation, firstOptions, store).RunAsync(new[] { new TestGenome(1) });
        var resumedVariation = new ProbeVariation(true);
        var resumedOptions = Options(); resumedOptions.Resume = true; resumedOptions.MaxDegreeOfParallelism = 1;
        var resumedEngine = Engine(new ProbeTask(resumedVariation), resumedVariation, resumedOptions, store);
        var resumed = await resumedEngine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(expected.StateHash, resumed.StateHash);
        Assert.Equal(9, resumedEngine.PipelineReport!.FirstEvaluationId);
        Assert.Equal(Enumerable.Range(1, 16).Select(value => (long)value), resumedVariation.Observed);
    }

    [Fact]
    public async Task ScheduleRetentionIsBoundedAndExplicitlyReportsLostReplayEvidence()
    {
        var variation = new ProbeVariation(true);
        var options = Options(); options.Pipeline.MaximumScheduleRecords = 3;
        var engine = Engine(new ProbeTask(variation), variation, options);
        await engine.RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(3, engine.PipelineReport!.Schedule.Count); Assert.Equal(31, engine.PipelineReport.DroppedScheduleRecords);
        Assert.False(engine.PipelineReport.IsScheduleComplete);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(100)]
    public async Task SharedResourceReservationsArePreAdmittedIndependentlyOfCallbackRefundTiming(int cap)
    {
        string? expectedState = null, expectedReceipts = null;
        foreach (int workers in new[] { 1, 4 })
        {
            var innerVariation = new ProbeVariation(true);
            var source = new CostedSource(innerVariation);
            var ledger = new EvolutionResourceLedger("pipeline-resource", EvolutionResources.Of("cost_units", cap));
            var variation = new ResourceMeteredVariationOperator<TestGenome>(source, ledger, EvolutionResources.Of("cost_units", 1), "work-v1");
            var task = new ResourceMeteredEvolutionTask<TestGenome>(new ProbeTask(innerVariation, reversed: workers == 4), ledger, new[] { 2m });
            var options = Options(); options.MaxProposals = 9; options.MaxDegreeOfParallelism = workers;
            var engine = Engine(task, variation, options);
            var run = await engine.RunAsync(new[] { new TestGenome(1) });
            var resources = ledger.Snapshot();
            expectedState ??= run.StateHash;
            string receipts = JsonSerializer.Serialize(resources.Receipts.OrderBy(receipt => receipt.OperationId, StringComparer.Ordinal));
            expectedReceipts ??= receipts;
            Assert.Equal(expectedState, run.StateHash); Assert.Equal(expectedReceipts, receipts);
            Assert.Equal(resources.Admitted, resources.Settled); Assert.Equal(0m, resources.Reserved["cost_units"]);
            Assert.Equal(0, resources.Unknown); Assert.False(resources.MaximumViolated);
            Assert.InRange(resources.Spent["cost_units"], 0, cap);
            Assert.Equal(4, engine.PipelineReport!.ProposalWorkers);
            Assert.Equal(cap == 100 ? 11m : 2.5m, resources.Spent["cost_units"]);
            Assert.Equal(cap == 100 ? 8 : 2, source.Calls);
            Assert.Equal(source.Calls, innerVariation.Observed.Count);
            Assert.Equal(0, innerVariation.Active);
        }
    }

    [Fact]
    public void PreAdmittedUndispatchedWorkRefundsZeroButTakenWorkWithoutAReceiptStaysUnknown()
    {
        var ledger = new EvolutionResourceLedger("phase-contract", EvolutionResources.Of("cost_units", 10));
        using (var phase = new EvolutionPipelineResourcePhase(ledger))
        {
            Assert.True(phase.Reserve("not-dispatched", EvolutionResourceStage.Proposal, EvolutionResources.Of("cost_units", 3)));
            Assert.True(phase.Reserve("dispatched", EvolutionResourceStage.Evaluation, EvolutionResources.Of("cost_units", 4)));
            Assert.NotNull(phase.Take("dispatched"));
            Assert.Throws<InvalidOperationException>(() => phase.Take("dispatched"));
            Assert.Throws<InvalidOperationException>(() => phase.Take("unplanned"));
        }
        var report = ledger.Snapshot();
        Assert.Equal(4m, report.Spent["cost_units"]); Assert.Equal(0m, report.Reserved["cost_units"]);
        Assert.Equal(1, report.Unknown); Assert.Equal(2, report.Settled);
        Assert.Equal(0m, Assert.Single(report.Receipts, receipt => receipt.OperationId == "not-dispatched").Charged["cost_units"]);
    }

    [Fact]
    public void ResourcePhaseRejectsUnplannedUseAndReconcilesOthersAfterExternalConflict()
    {
        var ledger = new EvolutionResourceLedger("phase-conflict", EvolutionResources.Of("cost_units", 10));
        var phase = new EvolutionPipelineResourcePhase(ledger);
        phase.Reserve("conflict", EvolutionResourceStage.Proposal, EvolutionResources.Of("cost_units", 3));
        phase.Reserve("other", EvolutionResourceStage.Proposal, EvolutionResources.Of("cost_units", 2));
        Assert.Throws<InvalidOperationException>(() => phase.Reserve("other", EvolutionResourceStage.Proposal, EvolutionResources.Of("cost_units", 1)));
        ledger.GetPendingReservation("conflict").Complete(EvolutionResources.Of("cost_units", 1));
        Assert.Throws<InvalidOperationException>(() => phase.Dispose());
        phase.Dispose();
        Assert.Equal(1m, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(0m, ledger.Snapshot().Reserved["cost_units"]);
        Assert.Throws<InvalidOperationException>(() => phase.Take("other"));
        Assert.Throws<InvalidOperationException>(() => phase.Reserve("closed", EvolutionResourceStage.Proposal, EvolutionResources.Of("cost_units", 1)));
    }

    [Fact]
    public void CostedAdapterRejectsCheckpointOrFeedbackDuringAnUndispatchedPhase()
    {
        var source = new CostedSource(new ProbeVariation(true));
        var ledger = new EvolutionResourceLedger("phase-checkpoint", EvolutionResources.Of("cost_units", 10));
        var variation = new ResourceMeteredVariationOperator<TestGenome>(source, ledger, EvolutionResources.Of("cost_units", 1), "work-v1");
        string state = variation.CaptureState();
        variation.BeginPipelinePhase();
        Assert.True(variation.ReservePipelineProposal(1));
        Assert.Throws<InvalidOperationException>(() => variation.CaptureState());
        Assert.Throws<InvalidOperationException>(() => variation.RestoreState(state));
        Assert.Throws<InvalidOperationException>(() => variation.GetProposalCost(1));
        Assert.Throws<InvalidOperationException>(() => variation.BeginPipelinePhase());
        variation.EndPipelinePhase();
        Assert.Equal(state, variation.CaptureState()); Assert.Equal(0, source.Calls);
        Assert.Equal(0m, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(0m, ledger.Snapshot().Reserved["cost_units"]);
    }

    [Fact]
    public async Task ProposalConcurrencyCapabilityIsPinnedAndOnlyChangesPipelineCompatibility()
    {
        var variation = new ProbeVariation(true);
        var pipeline = Engine(new ProbeTask(variation), variation, Options());
        variation.SupportsDeterministicConcurrency = false;
        Assert.NotEqual(pipeline.CompatibilityHash, Engine(new ProbeTask(variation), variation, Options()).CompatibilityHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.RunAsync(new[] { new TestGenome(1) }));
        Assert.Empty(variation.Contexts);
        var options = Options(); options.Dispatch = EvolutionDispatchMode.Batch;
        string serialized = Engine(new ProbeTask(variation), variation, options).CompatibilityHash;
        variation.SupportsDeterministicConcurrency = true;
        Assert.Equal(serialized, Engine(new ProbeTask(variation), variation, options).CompatibilityHash);
    }

    [Fact]
    public async Task MeteredCancellationSettlesEveryReservationWithoutRefundingDispatchedProposals()
    {
        var inner = new ProbeVariation(true, cancelAtProposal: true);
        var source = new CostedSource(inner);
        var ledger = new EvolutionResourceLedger("pipeline-cancel", EvolutionResources.Of("cost_units", 100));
        var variation = new ResourceMeteredVariationOperator<TestGenome>(source, ledger, EvolutionResources.Of("cost_units", 1), "work-v1");
        var task = new ResourceMeteredEvolutionTask<TestGenome>(new ProbeTask(inner), ledger, new[] { 2m });
        var store = new InMemoryEvolutionCheckpointStore();
        var engine = Engine(task, variation, Options(), store);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Task run = engine.RunAsync(new[] { new TestGenome(1) }, cancellation.Token);
        await inner.FirstProposal.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        var resources = ledger.Snapshot();
        Assert.Equal(resources.Admitted, resources.Settled); Assert.Equal(0m, resources.Reserved["cost_units"]);
        Assert.InRange(source.Calls, 1, 4); Assert.Equal(source.Calls, resources.Unknown);
        Assert.Equal(1m + source.Calls, resources.Spent["cost_units"]);
        Assert.Empty(inner.Observed); Assert.Equal(0, inner.Active);
        Assert.NotNull(await store.LoadLatestAsync("pipeline-test"));
        Assert.NotNull(variation.CaptureState());
    }

    [Fact]
    public async Task CascadeRefundsUndispatchedStagesAndBoundsTheFullReservationPlan()
    {
        var ledger = new EvolutionResourceLedger("pipeline-cascade", EvolutionResources.Of("cost_units", 100));
        var task = new ResourceMeteredEvolutionTask<TestGenome>(new PipelineCascadeTask(), ledger, new[] { 1m, 2m });
        var options = Options(); options.MaxProposals = 3;
        options.Cascade.Enabled = true; options.Cascade.Thresholds = new[] { 1d };
        var result = await Engine(task, new ProbeVariation(true), options).RunAsync(new[] { new TestGenome(1), new TestGenome(2), new TestGenome(3) });
        var resources = ledger.Snapshot();
        Assert.Equal(2, result.Counters.EvaluationAttempts); Assert.Equal(2, result.Counters.CompletedEvaluations);
        Assert.Equal(2.75m, resources.Spent["cost_units"]); Assert.Equal(0m, resources.Reserved["cost_units"]);
        Assert.Equal(6, resources.Settled); Assert.Equal(0, resources.Unknown);
        Assert.Equal(0m, Assert.Single(resources.Receipts, receipt => receipt.OperationId == "evaluation/1/attempt/1/stage/1").Charged["cost_units"]);
        var oversized = new ResourceMeteredEvolutionTask<TestGenome>(new PipelineCascadeTask(256), ledger, Enumerable.Repeat(1m, 256));
        options.Cascade.Thresholds = Enumerable.Repeat(1d, 255).ToArray(); options.Pipeline.WaveSize = 257;
        Assert.Throws<ArgumentException>(() => Engine(oversized, new ProbeVariation(true), options));
    }

    [Theory]
    [InlineData("canonical-failure", 0, 0)]
    [InlineData("missing-receipt", 2, 1)]
    [InlineData("maximum-exceeded", 3, 0)]
    public async Task InvalidMeteredCandidatesNeverPromoteAndKeepConservativeCharges(string mode, int spent, int unknown)
    {
        var ledger = new EvolutionResourceLedger("pipeline-bad-receipt", EvolutionResources.Of("cost_units", 10));
        var task = new ResourceMeteredEvolutionTask<TestGenome>(new InvalidReceiptTask(mode), ledger, new[] { 2m });
        var options = Options(); options.MaxProposals = 1;
        var result = await Engine(task, new ProbeVariation(true), options).RunAsync(new[] { new TestGenome(1) });
        Assert.Empty(result.Islands.SelectMany(island => island.Entries));
        var resources = ledger.Snapshot();
        Assert.Equal((decimal)spent, resources.Spent["cost_units"]); Assert.Equal(unknown, resources.Unknown);
        Assert.Equal(0m, resources.Reserved["cost_units"]); Assert.Equal(resources.Admitted, resources.Settled);
        Assert.Equal(mode == "maximum-exceeded", resources.MaximumViolated);
    }

    [Fact]
    public async Task MeteredRetryAdmissionIgnoresWorkerCompletionRefundOrder()
    {
        string? expected = null, receipts = null;
        foreach (int workers in new[] { 1, 4 })
        {
            var variation = new ProbeVariation(true);
            var ledger = new EvolutionResourceLedger("pipeline-retry-cost", EvolutionResources.Of("cost_units", 8));
            var task = new ResourceMeteredEvolutionTask<TestGenome>(new ProbeTask(variation, failFirst: true, reversed: workers == 4), ledger, new[] { 2m });
            var options = Options(); options.MaxProposals = 5; options.MaxRetries = 1; options.MaxDegreeOfParallelism = workers;
            var result = await Engine(task, variation, options).RunAsync(new[] { new TestGenome(1) });
            expected ??= result.StateHash; Assert.Equal(expected, result.StateHash);
            var resources = ledger.Snapshot();
            string actual = JsonSerializer.Serialize(resources.Receipts.OrderBy(receipt => receipt.OperationId, StringComparer.Ordinal));
            receipts ??= actual; Assert.Equal(receipts, actual);
            Assert.Equal(resources.Admitted, resources.Settled); Assert.Equal(0m, resources.Reserved["cost_units"]);
            Assert.Equal(0, resources.Unknown);
        }
    }

    [Fact]
    public void OptionsAreOwnedValidatedAndDoNotChangeDisabledModeCompatibility()
    {
        var options = Options(); var snapshot = options.SnapshotAndValidate();
        string original = snapshot.GetConfigurationHash(); options.Pipeline.WaveSize = 3;
        Assert.Equal(8, snapshot.Pipeline.WaveSize); Assert.NotEqual(original, options.GetConfigurationHash());
        options.Dispatch = EvolutionDispatchMode.Batch; string batch = options.GetConfigurationHash();
        options.Pipeline.WaveSize = 32; Assert.Equal(batch, options.GetConfigurationHash());
        foreach (Action<EvolutionEngineOptions> corrupt in new Action<EvolutionEngineOptions>[]
        {
            value => value.Pipeline.WaveSize = 0, value => value.Pipeline.WaveSize = 4097,
            value => value.Pipeline.MaxProposalConcurrency = 257, value => value.Pipeline.ProposalQueueCapacity = 0,
            value => value.Pipeline.EvaluationQueueCapacity = 257, value => value.Pipeline.MaximumScheduleRecords = -1,
            value => value.Pipeline.MinimumProposalStartInterval = TimeSpan.FromTicks(-1),
            value => value.Pipeline.MinimumEvaluationStartInterval = TimeSpan.FromMinutes(2),
            value => value.MaxInFlightPerIsland = 1, value => value.MaxInFlight = 4, value => value.Pipeline = null!
        })
        { var invalid = Options(); corrupt(invalid); Assert.ThrowsAny<ArgumentException>(() => invalid.SnapshotAndValidate()); }
    }

    private static EvolutionEngineOptions Options() => new()
    {
        RunId = "pipeline-test",
        Seed = 37,
        Dispatch = EvolutionDispatchMode.Pipeline,
        MaxProposals = 17,
        MaxEvaluationAttempts = 100,
        MaxGenerations = 100,
        MaxDegreeOfParallelism = 3,
        CheckpointInterval = 0,
        MigrationInterval = 0,
        Pipeline = new EvolutionPipelineOptions { WaveSize = 8, MaxProposalConcurrency = 4, ProposalQueueCapacity = 2, EvaluationQueueCapacity = 2 }
    };

    private static EvolutionEngine<TestGenome> Engine(IEvolutionTask<TestGenome> task, IVariationOperator<TestGenome> variation,
        EvolutionEngineOptions options, IEvolutionCheckpointStore? store = null) => new(task, variation,
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 2000, 100, EvolutionOutOfRangePolicy.Clamp) }),
            options, checkpointStore: store, genomeCodec: new TestGenomeCodec());

    private sealed class ProbeVariation(bool concurrent, bool proveOverlap = false, bool cancelAtProposal = false)
        : IDeterministicConcurrentVariationOperator<TestGenome>, IOutcomeAwareVariationOperator<TestGenome>
    {
        private readonly TaskCompletionSource<bool> _four = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> FirstEvaluation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> FirstProposal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentDictionary<long, EvolutionVariationContext<TestGenome>> Contexts = new();
        public readonly List<long> Observed = new();
        private int _active, _peak, _calls;
        public string Id => "pipeline-probe";
        public string VersionHash => "pipeline-probe-v1";
        public bool SupportsDeterministicConcurrency { get; set; } = concurrent;
        public int Peak => Volatile.Read(ref _peak);
        public int Active => Volatile.Read(ref _active);
        public async ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            int active = Interlocked.Increment(ref _active);
            lock (Contexts) _peak = Math.Max(_peak, active);
            Contexts[context.Generation] = context; FirstProposal.TrySetResult(true);
            if (Interlocked.Increment(ref _calls) >= 4) _four.TrySetResult(true);
            try
            {
                if (cancelAtProposal) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (proveOverlap && context.Generation <= 4)
                {
                    await Wait(_four.Task, cancellationToken);
                    if (context.Generation == 2) await Wait(FirstEvaluation.Task, cancellationToken);
                }
                await Task.Delay((int)(context.Generation % 3) + 1, cancellationToken);
                return new TestGenome(1000 + (int)context.Generation * 25);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
        { Assert.Equal(0, Active); Observed.Add(evaluation.Lineage.Generation); }
        public string CaptureState() { Assert.Equal(0, Active); return JsonSerializer.Serialize(Observed); }
        public void RestoreState(string state) { Assert.Equal(0, Active); Observed.Clear(); Observed.AddRange(JsonSerializer.Deserialize<List<long>>(state)!); }
    }

    private sealed class ProbeTask(ProbeVariation variation, bool reversed = false, bool failFirst = false, bool hardFailures = false, double observedOffset = 0) : IEvolutionTask<TestGenome>
    {
        public string Id => "pipeline-task";
        public string VersionHash => "v1";
        public string EvaluatorVersionHash => "v1";
        public bool SawGenerationOverlap;
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(CultureInfo.InvariantCulture)));
        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            if (candidate.Lineage.Generation == 1)
            { SawGenerationOverlap = variation.Active > 0; variation.FirstEvaluation.TrySetResult(true); }
            await Task.Delay(reversed ? 5 - (int)(context.EvaluationId % 4) : 1 + (int)(context.EvaluationId % 4), cancellationToken);
            if (failFirst && context.AttemptCount == 1) return EvolutionTaskResult.Failed("retry", "first attempt");
            if (hardFailures && candidate.Lineage.Generation > 0 && context.AttemptCount <= 2) return EvolutionTaskResult.Failed("retry", "two failed attempts");
            return EvolutionTaskResult.Completed(candidate.CanonicalGenome.Genome.Value + observedOffset,
                new Dictionary<string, double> { ["x"] = candidate.CanonicalGenome.Genome.Value }, costUnits: 1);
        }
    }

    private sealed class CostedSource(ProbeVariation inner) : IDeterministicConcurrentCostedEvolutionProposalSource<TestGenome>
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public string Id => "costed-pipeline-probe";
        public string VersionHash => "costed-pipeline-concurrent-v1";
        public bool SupportsDeterministicConcurrency => true;
        public async ValueTask<EvolutionResourceResult<TestGenome>> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            TestGenome genome = await inner.ProposeAsync(context, cancellationToken);
            return new EvolutionResourceResult<TestGenome>(genome, EvolutionResources.Of("cost_units", 0.25m));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => inner.Observe(evaluation, insertionResult);
        public string CaptureState() => inner.CaptureState();
        public void RestoreState(string state) => inner.RestoreState(state);
    }

    private sealed class PipelineCascadeTask(int stages = 2) : ICascadeEvolutionTask<TestGenome>
    {
        public string Id => "pipeline-cascade";
        public string VersionHash => "v1";
        public string EvaluatorVersionHash => "v1";
        public int StageCount => stages;
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Cascade must use its stages.");
        public ValueTask<EvolutionTaskResult> EvaluateStageAsync(int stage, EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default) =>
            new(EvolutionTaskResult.Completed(stage == 0 ? candidate.CanonicalGenome.Genome.Value % 2 : candidate.CanonicalGenome.Genome.Value,
                new Dictionary<string, double> { ["x"] = candidate.CanonicalGenome.Genome.Value }, costUnits: stage == 0 ? 0.25 : 1));
    }

    private sealed class InvalidReceiptTask(string mode) : IEvolutionTask<TestGenome>
    {
        public string Id => "pipeline-invalid-receipt";
        public string VersionHash => "v1";
        public string EvaluatorVersionHash => "v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            mode == "canonical-failure" ? throw new InvalidOperationException("Invalid genome.") : new(new EvolutionCanonicalGenome<TestGenome>(genome, "receipt-seed"));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default) =>
            mode == "missing-receipt" ? throw new InvalidOperationException("Receipt lost after dispatch.") :
            new(EvolutionTaskResult.Completed(1, new Dictionary<string, double> { ["x"] = 1 }, costUnits: 3));
    }

    private static async Task Wait(Task task, CancellationToken cancellationToken)
    {
        await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested(); await task;
    }
}
