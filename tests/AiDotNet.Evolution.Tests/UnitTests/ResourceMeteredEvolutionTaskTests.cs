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

    [Theory]
    [InlineData(1e-300)]
    [InlineData(1e30)]
    public async Task Unrepresentable_cost_is_not_a_free_success(double cost)
    {
        var ledger = Ledger(10); var observer = new EvaluationRecordingObserver();
        var inner = new ReceiptTask(cost);
        await Engine(new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 3m }), Options(1), observer)
            .RunAsync(new[] { new TestGenome(1) });
        var outcome = Assert.Single(observer.Evaluations);
        Assert.Equal(EvolutionEvaluationStatus.Failed, outcome.Status);
        Assert.Equal(3, outcome.Cost.CostUnits); Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(1, ledger.Snapshot().Unknown);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "resource_cost_unrepresentable");
    }

    [Fact]
    public async Task Nested_budget_denial_is_dispatched_unknown_work_not_preflight_denial()
    {
        var ledger = Ledger(10); var observer = new EvaluationRecordingObserver();
        var inner = new ReceiptTask(0, true);
        await Engine(new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 3m }), Options(1), observer)
            .RunAsync(new[] { new TestGenome(1) });
        var outcome = Assert.Single(observer.Evaluations);
        Assert.Equal(1, inner.Calls); Assert.Equal(EvolutionEvaluationStatus.Failed, outcome.Status);
        Assert.Equal(3, outcome.Cost.CostUnits); Assert.Equal(1, ledger.Snapshot().Unknown);
        Assert.DoesNotContain(outcome.Diagnostics, diagnostic => diagnostic.Code == "resource_budget_reached");
    }

    [Fact]
    public async Task Maximum_violation_retains_actual_cost_but_cannot_promote_the_result()
    {
        var ledger = Ledger(10); var observer = new EvaluationRecordingObserver();
        var result = await Engine(new ResourceMeteredEvolutionTask<TestGenome>(new ReceiptTask(4), ledger, new[] { 3m }), Options(1), observer)
            .RunAsync(new[] { new TestGenome(1) });
        var outcome = Assert.Single(observer.Evaluations);
        Assert.Equal(EvolutionEvaluationStatus.Failed, outcome.Status);
        Assert.Equal(4, outcome.Cost.CostUnits); Assert.Equal(4, ledger.Snapshot().Spent["cost_units"]);
        Assert.True(ledger.Snapshot().MaximumViolated); Assert.Equal(0, ledger.Snapshot().Unknown);
        Assert.Equal(EvolutionResourceOutcome.Failed, Assert.Single(ledger.Snapshot().Receipts).Outcome);
        Assert.Null(result.Best);
    }

    [Fact]
    public async Task Unknown_attempt_cost_remains_visible_after_a_successful_retry()
    {
        var ledger = Ledger(10); var observer = new EvaluationRecordingObserver();
        var inner = new ReceiptTask(2, produce: count => count == 1 ? throw new InvalidOperationException("no receipt") :
            EvolutionTaskResult.Completed(1, new Dictionary<string, double> { ["x"] = 1 }, costUnits: 2));
        var options = Options(1); options.MaxRetries = 1;
        await Engine(new ResourceMeteredEvolutionTask<TestGenome>(inner, ledger, new[] { 3m }), options, observer).RunAsync(new[] { new TestGenome(1) });
        var outcome = Assert.Single(observer.Evaluations);
        Assert.Equal(EvolutionEvaluationStatus.Completed, outcome.Status); Assert.Equal(2, outcome.Cost.AttemptCount);
        Assert.Equal(5, outcome.Cost.CostUnits); Assert.Equal(5, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(1, ledger.Snapshot().Unknown);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "resource_cost_unknown");
    }

    [Fact]
    public async Task Fatal_producer_error_propagates_after_conservative_settlement()
    {
        var ledger = Ledger(10);
        var task = new ResourceMeteredEvolutionTask<TestGenome>(new ReceiptTask(0, produce: _ => throw new OutOfMemoryException("simulated")), ledger, new[] { 3m });
        await Assert.ThrowsAsync<OutOfMemoryException>(() => Engine(task, Options(1)).RunAsync(new[] { new TestGenome(1) }));
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(1, ledger.Snapshot().Unknown);
    }

    private sealed class ReceiptTask(double cost, bool throwBudget = false, Func<int, EvolutionTaskResult>? produce = null) : IEvolutionTask<TestGenome>
    {
        private readonly SyntheticEvolutionTask _canonical = new();
        public string Id => "receipt-task";
        public string VersionHash => "receipt-task-v1";
        public string EvaluatorVersionHash => VersionHash;
        public int Calls { get; private set; }
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            _canonical.CanonicalizeAsync(genome, cancellationToken);
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (throwBudget) throw new EvolutionResourceBudgetException("inner-operation");
            if (produce is not null) return new(produce(Calls));
            return new(EvolutionTaskResult.Completed(1, new Dictionary<string, double> { ["x"] = 1 }, costUnits: cost));
        }
    }
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
