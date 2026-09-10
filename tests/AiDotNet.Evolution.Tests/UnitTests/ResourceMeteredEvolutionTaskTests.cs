using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class ResourceMeteredEvolutionTaskTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefundingCascadeAttemptsNeverRefundsScreeningResources(bool chargeRejectedAttempts)
    {
        var inner = new StagedEvolutionTask();
        var ledger = Ledger(20);
        var task = new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 1m, 2m });
        var options = Options(4);
        options.Cascade.Enabled = true;
        options.Cascade.Thresholds = new[] { 3d };
        options.Cascade.ChargeRejectedStagesToBudget = chargeRejectedAttempts;
        EvolutionRunResult<TestGenome> result = await Engine(task, options).RunAsync(Enumerable.Range(1, 4).Select(value => new TestGenome(value)));
        Assert.Equal(4, inner.StageCalls(0)); Assert.Equal(2, inner.StageCalls(1));
        Assert.Equal(chargeRejectedAttempts ? 4 : 2, result.Counters.EvaluationAttempts);
        Assert.Equal(8, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(6, ledger.Snapshot().Settled);
        Assert.Equal(4, ledger.Snapshot().Receipts.Count(receipt => receipt.Stage == EvolutionResourceStage.Screening));
    }

    [Fact]
    public async Task RetriedFailuresAndSuccessfulAttemptsBothConsumeResources()
    {
        var inner = new FailOnceEvolutionTask();
        var ledger = Ledger(10);
        var task = new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 2m });
        var options = Options(1); options.MaxRetries = 1;
        EvolutionRunResult<TestGenome> result = await Engine(task, options).RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(2, inner.Calls); Assert.Equal(2, result.Counters.EvaluationAttempts);
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(new[] { 1, 2 }, ledger.Snapshot().Receipts.Select(receipt => receipt.Attempt));
        Assert.Equal(new[] { EvolutionResourceOutcome.Failed, EvolutionResourceOutcome.Completed }, ledger.Snapshot().Receipts.Select(receipt => receipt.Outcome));
    }

    [Fact]
    public async Task CacheHitDoesNotCreateAnotherResourceCharge()
    {
        var inner = new FailOnceEvolutionTask();
        var ledger = Ledger(10);
        var task = new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 2m });
        var options = Options(2); options.ProposalBatchSize = 1; options.MaxRetries = 1;
        var observer = new EvaluationRecordingObserver();
        await Engine(task, options, observer).RunAsync(new[] { new TestGenome(1), new TestGenome(1) });
        Assert.Equal(2, inner.Calls); Assert.Equal(2, ledger.Snapshot().Settled);
        Assert.Equal(EvolutionCacheStatus.Hit, observer.Evaluations.Last().CacheStatus);
    }

    [Fact]
    public async Task DeniedReservationNeverCallsEvaluatorAndIsVisibleInDiagnostics()
    {
        var inner = new SyntheticEvolutionTask();
        var ledger = Ledger(0);
        var task = new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 1m });
        var observer = new EvaluationRecordingObserver();
        await Engine(task, Options(1), observer).RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(0, inner.Calls); Assert.Equal(0, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(1, ledger.Snapshot().Denied);
        Assert.Contains(Assert.Single(observer.Evaluations).Diagnostics, diagnostic => diagnostic.Code == "resource_budget_reached");
    }

    [Fact]
    public async Task ThrowingEvaluatorKeepsItsConservativeCharge()
    {
        var inner = new SyntheticEvolutionTask(throwOnValue: 1);
        var ledger = Ledger(10);
        var task = new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 3m });
        await Engine(task, Options(1)).RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(1, inner.Calls); Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(1, ledger.Snapshot().Unknown);
    }

    [Fact]
    public async Task CompatibleEngineAndLedgerBoundaryResumePreservesAccounting()
    {
        var checkpoint = new InMemoryEvolutionCheckpointStore();
        var firstLedger = Ledger(10);
        var options = Options(1); options.ProposalBatchSize = 1; options.MaxRetries = 1;
        await Engine(new ResourceMeteredEvolutionTask<TestGenome>(new FailOnceEvolutionTask(), firstLedger, new[] { 2m }), options,
            store: checkpoint).RunAsync(new[] { new TestGenome(1) });
        var resumedLedger = Ledger(20); resumedLedger.RestoreState(firstLedger.CaptureState());
        var resumedOptions = Options(4); resumedOptions.ProposalBatchSize = 1; resumedOptions.Resume = true; resumedOptions.MaxRetries = 1;
        await Engine(new ResourceMeteredEvolutionTask<TestGenome>(new FailOnceEvolutionTask(), resumedLedger, new[] { 2m }), resumedOptions,
            store: checkpoint).RunAsync(new[] { new TestGenome(1) });
        var completeLedger = Ledger(20);
        var completeOptions = Options(4); completeOptions.ProposalBatchSize = 1; completeOptions.MaxRetries = 1;
        await Engine(new ResourceMeteredEvolutionTask<TestGenome>(new FailOnceEvolutionTask(), completeLedger, new[] { 2m }), completeOptions,
            store: new InMemoryEvolutionCheckpointStore()).RunAsync(new[] { new TestGenome(1) });
        Assert.Equal(12, resumedLedger.Snapshot().Spent["cost_units"]);
        Assert.Equal(completeLedger.CaptureState(), resumedLedger.CaptureState());
        Assert.Equal(resumedLedger.Snapshot().Admitted, resumedLedger.Snapshot().Settled);
    }

    [Fact]
    public void InvalidMaximaAndUnknownResourcesAreRejectedBeforeExecution()
    {
        Assert.Throws<ArgumentException>(() => new ResourceMeteredEvolutionTask<TestGenome>(new StagedEvolutionTask(), Ledger(10), new[] { 1m }));
        Assert.Throws<ArgumentException>(() => new ResourceMeteredEvolutionTask<TestGenome>(new SyntheticEvolutionTask(), Ledger(10), new[] { -1m }));
        Assert.Throws<ArgumentException>(() => new ResourceMeteredEvolutionTask<TestGenome>(new SyntheticEvolutionTask(),
            new EvolutionResourceLedger("test", EvolutionResources.Of("tokens", 1)), new[] { 1m }));
        var first = new ResourceMeteredEvolutionTask<TestGenome>(new SyntheticEvolutionTask(), Ledger(10), new[] { 1m });
        var second = new ResourceMeteredEvolutionTask<TestGenome>(new SyntheticEvolutionTask(), Ledger(20), new[] { 1m });
        var changed = new ResourceMeteredEvolutionTask<TestGenome>(new SyntheticEvolutionTask(), Ledger(10), new[] { 2m });
        Assert.Equal(first.VersionHash, second.VersionHash);
        Assert.NotEqual(first.VersionHash, changed.VersionHash);
    }

    private static EvolutionResourceLedger Ledger(decimal cap) => new("metered-test", EvolutionResources.Of("cost_units", cap));
    private static EvolutionEngineOptions Options(int proposals) => new()
    {
        RunId = "metered-test",
        MaxProposals = proposals,
        MaxGenerations = proposals,
        MaxEvaluationAttempts = 20,
        ProposalBatchSize = 4,
        MaxDegreeOfParallelism = 4,
        MigrationInterval = 0
    };
    private static EvolutionEngine<TestGenome> Engine(IEvolutionTask<TestGenome> task, EvolutionEngineOptions options,
        IEvolutionObserver<TestGenome>? observer = null, IEvolutionCheckpointStore? store = null) =>
        new(task, new IncrementVariation(), _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }),
            options, observer: observer, checkpointStore: store, genomeCodec: store is null ? null : new TestGenomeCodec());
}
