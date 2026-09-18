using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionSurrogateTests
{
    private static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);
    private static EvolutionResourceLedger Ledger(decimal limit = 100) => new("surrogate-tests", Cost(limit));
    private static readonly EvolutionCanonicalGenome<int>[] Pool = { new(1, "a"), new(2, "b"), new(3, "c") };
    private static ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<int>>>> Propose(CancellationToken _) =>
        new(new EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<int>>>(Pool, Cost(3)));
    private static EvolutionSurrogateObservation<int> Observation(int id, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize,
        EvolutionCacheStatus cache = EvolutionCacheStatus.Miss, int attempts = 1, double violation = 0, string task = "task", string? evaluationGenome = null,
        EvolutionMeasurementOrigin? origin = null)
    {
        var lineage = new EvolutionLineage(null, null, "test", null, 0, 0, (ulong)id);
        var candidate = new EvolutionCandidate<int>(id, new(id, "measured-" + id), lineage);
        var evaluation = new EvolutionEvaluation(id, evaluationGenome ?? candidate.CanonicalGenome.Id, EvolutionEvaluationStatus.Completed, id,
            direction, new Dictionary<string, double>(), Array.Empty<double>(), new[] { violation },
            new EvolutionEvaluationCost(TimeSpan.Zero, attempts, 1), lineage, cache, Array.Empty<EvolutionDiagnostic>(), task, "eval", "config");
        return new(candidate, origin is null ? evaluation : evaluation.WithMeasurementOrigin(origin));
    }
    private static EvolutionSurrogateObservation<int>[] Observations(EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize) =>
        new[] { Observation(0, direction), Observation(1, direction) };
    private static StableRandom AcquisitionRandom()
    {
        for (ulong seed = 0; ; seed++)
        {
            var random = new StableRandom(seed); random.NextInt(3);
            if (random.NextDouble() >= 0.05) return new StableRandom(seed);
        }
    }
    private static EvolutionSurrogateSelector<int> Selector(Backend backend, EvolutionResourceLedger ledger, double explore = 0.05,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize, double optimism = 1) =>
        new(backend, ledger, "task", "eval", Cost(3), Cost(2), Cost(1), minimumSamples: 2, explorationProbability: explore, optimism: optimism, direction: direction);

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize, "c")]
    [InlineData(EvolutionOptimizationDirection.Minimize, "a")]
    public async Task Acquisition_is_direction_aware_and_charges_every_stage(EvolutionOptimizationDirection direction, string expected)
    {
        var ledger = Ledger(); var backend = new Backend();
        var result = await Selector(backend, ledger, direction: direction).SelectAsync("decision", Observations(direction), AcquisitionRandom(), Propose);
        Assert.Equal(EvolutionSurrogateSelectionReason.Acquisition, result.Reason); Assert.Equal(expected, result.Candidate.Id);
        Assert.Equal(3, result.Predictions.Count); Assert.Equal(3, result.PoolSize); Assert.Equal("fitted-v1", result.ModelVersionHash);
        Assert.Equal(6, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(0.05, result.ExplorationProbability);
        Assert.Equal(new[] { EvolutionResourceStage.Proposal, EvolutionResourceStage.SurrogateTraining, EvolutionResourceStage.SurrogateInference }, ledger.Snapshot().Receipts.Select(r => r.Stage));
        Assert.Equal(2, backend.TrainingCount);
    }

    [Fact]
    public async Task Optimism_and_exact_ties_have_explicit_selection_semantics()
    {
        var backend = new Backend { Predictions = new[] { new EvolutionSurrogatePrediction("c", 3, 0, true), new("b", 2, 10, true), new("a", 1, 0, true) } };
        var result = await Selector(backend, Ledger()).SelectAsync("optimistic", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal("b", result.Candidate.Id);
        backend.Predictions = Pool.Select(p => new EvolutionSurrogatePrediction(p.Id, 1, 0, true)).ToArray();
        result = await Selector(backend, Ledger()).SelectAsync("ties", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal("a", result.Candidate.Id);
    }

    [Theory]
    [InlineData(true, EvolutionSurrogateSelectionReason.Exploration)]
    [InlineData(false, EvolutionSurrogateSelectionReason.InsufficientData)]
    public async Task Exploration_and_insufficient_data_skip_model_costs(bool enough, EvolutionSurrogateSelectionReason expected)
    {
        var ledger = Ledger(); var backend = new Backend();
        var observations = enough ? Observations() : new[] { Observation(0) };
        var result = await Selector(backend, ledger, explore: 1).SelectAsync("fallback", observations, AcquisitionRandom(), Propose);
        Assert.Equal(expected, result.Reason); Assert.Null(result.ModelVersionHash); Assert.Empty(result.Predictions);
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(0, backend.Fits);
    }

    [Theory]
    [InlineData(false, EvolutionSurrogateSelectionReason.UnreliableModel, 5)]
    [InlineData(true, EvolutionSurrogateSelectionReason.UnfamiliarPool, 6)]
    public async Task Weak_models_and_unfamiliar_pools_use_uniform_fallback(bool reliable, EvolutionSurrogateSelectionReason expected, int spent)
    {
        var backend = new Backend { Reliable = reliable, Predictions = Pool.Select(p => new EvolutionSurrogatePrediction(p.Id, p.Genome, 0, false)).ToArray() };
        var ledger = Ledger(); var random = AcquisitionRandom(); string fallback = Pool[random.NextInt(3)].Id;
        var result = await Selector(backend, ledger).SelectAsync("fallback", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal(expected, result.Reason); Assert.Equal(fallback, result.Candidate.Id); Assert.Equal(spent, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(reliable ? 3 : 0, result.Predictions.Count);
    }

    [Theory]
    [InlineData(true, EvolutionSurrogateSelectionReason.TrainingFailure, 5)]
    [InlineData(false, EvolutionSurrogateSelectionReason.InferenceFailure, 6)]
    public async Task Model_exceptions_fall_back_after_unknown_cost_settlement(bool fitting, EvolutionSurrogateSelectionReason expected, int spent)
    {
        var backend = new Backend { FitError = fitting ? new InvalidOperationException() : null, InferenceError = fitting ? null : new InvalidOperationException() };
        var ledger = Ledger();
        var result = await Selector(backend, ledger).SelectAsync("unknown", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal(expected, result.Reason); Assert.Equal(spent, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(1, ledger.Snapshot().Unknown); Assert.All(ledger.Snapshot().Reserved.Values, value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(true, EvolutionSurrogateSelectionReason.TrainingFailure, 3.5)]
    [InlineData(false, EvolutionSurrogateSelectionReason.InferenceFailure, 5.5)]
    public async Task Returned_failure_costs_are_not_replaced_with_unknown_maxima(bool fitting, EvolutionSurrogateSelectionReason expected, double spent)
    {
        var backend = new Backend();
        if (fitting) { backend.FitOutcome = EvolutionResourceOutcome.Failed; backend.FitCost = 0.5m; }
        else { backend.InferenceOutcome = EvolutionResourceOutcome.Rejected; backend.InferenceCost = 0.5m; }
        var ledger = Ledger();
        var result = await Selector(backend, ledger).SelectAsync("rejected", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal(expected, result.Reason); Assert.Equal((decimal)spent, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(0, ledger.Snapshot().Unknown);
    }

    [Fact]
    public async Task Nested_backend_budget_denial_is_dispatched_failure_not_free_preflight_denial()
    {
        var ledger = Ledger(); var backend = new Backend { FitError = new EvolutionResourceBudgetException("nested") };
        var result = await Selector(backend, ledger).SelectAsync("nested", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal(EvolutionSurrogateSelectionReason.TrainingFailure, result.Reason);
        Assert.Equal(5, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(1, ledger.Snapshot().Unknown);
    }

    [Theory]
    [InlineData(4, EvolutionSurrogateSelectionReason.TrainingBudgetDenied, 3)]
    [InlineData(5, EvolutionSurrogateSelectionReason.InferenceBudgetDenied, 5)]
    public async Task Optional_stage_denials_preserve_an_unscored_fallback(int cap, EvolutionSurrogateSelectionReason expected, int spent)
    {
        var ledger = Ledger(cap); var backend = new Backend();
        var result = await Selector(backend, ledger).SelectAsync("budget", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal(expected, result.Reason); Assert.Equal(spent, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(0, ledger.Snapshot().Unknown);
    }

    [Fact]
    public async Task Proposal_denial_and_reuse_never_dispatch_unaccounted_work()
    {
        var backend = new Backend(); var denied = Ledger(2);
        await Assert.ThrowsAsync<EvolutionResourceBudgetException>(async () => await Selector(backend, denied).SelectAsync("denied", Observations(), AcquisitionRandom(), Propose));
        Assert.Equal(0, denied.Snapshot().Spent["cost_units"]);
        var ledger = Ledger(); var selector = Selector(backend, ledger);
        await selector.SelectAsync("once", Observations(), AcquisitionRandom(), Propose);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await selector.SelectAsync("once", Observations(), AcquisitionRandom(), Propose));
        Assert.Equal(6, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task Maximum_violation_cancellation_and_fatal_errors_do_not_become_successful_fallback()
    {
        var exceeded = Ledger();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await Selector(new Backend { FitCost = 3 }, exceeded).SelectAsync("overrun", Observations(), AcquisitionRandom(), Propose));
        Assert.True(exceeded.Snapshot().MaximumViolated); Assert.Equal(6, exceeded.Snapshot().Spent["cost_units"]);
        foreach (Exception error in new Exception[] { new OperationCanceledException(), new OutOfMemoryException() })
        {
            var ledger = Ledger();
            await Assert.ThrowsAsync(error.GetType(), async () => await Selector(new Backend { FitError = error }, ledger).SelectAsync("abort", Observations(), AcquisitionRandom(), Propose));
            Assert.Equal(5, ledger.Snapshot().Spent["cost_units"]); Assert.Equal(1, ledger.Snapshot().Unknown);
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var unused = Ledger();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Selector(new Backend(), unused).SelectAsync("pre", Observations(), AcquisitionRandom(), Propose, cancellation.Token));
        Assert.Equal(0, unused.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task Malformed_predictions_and_overflow_are_explicitly_rejected()
    {
        var cases = new[] {
            Array.Empty<EvolutionSurrogatePrediction>(),
            new[] { new EvolutionSurrogatePrediction("a", 1, 0, true), new("a", 2, 0, true), new("c", 3, 0, true) },
            new[] { new EvolutionSurrogatePrediction("a", 1, 0, true), new("b", 2, 0, true), new("wrong", 3, 0, true) },
            Pool.Select(p => new EvolutionSurrogatePrediction(p.Id, double.MaxValue, double.MaxValue, true)).ToArray() };
        foreach (var predictions in cases)
        {
            var ledger = Ledger(); var result = await Selector(new Backend { Predictions = predictions }, ledger).SelectAsync("invalid", Observations(), AcquisitionRandom(), Propose);
            Assert.Equal(EvolutionSurrogateSelectionReason.InvalidPredictions, result.Reason); Assert.Equal(6, ledger.Snapshot().Spent["cost_units"]);
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionSurrogatePrediction("a", double.NaN, 0, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionSurrogatePrediction("a", 0, -1, true));
    }

    [Fact]
    public async Task Invalid_pool_is_charged_but_invalid_training_never_dispatches()
    {
        var ledger = Ledger();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Selector(new Backend(), ledger).SelectAsync("duplicate", Observations(), AcquisitionRandom(),
            _ => new(new EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<int>>>(new[] { Pool[0], Pool[0] }, Cost(3)))));
        Assert.Equal(3, ledger.Snapshot().Spent["cost_units"]);
        foreach (var records in new[] { new[] { Observation(0), Observation(0) }, new[] { Observation(0, task: "other") }, Enumerable.Range(0, 257).Select(i => Observation(i)).ToArray() })
        {
            var unused = Ledger();
            await Assert.ThrowsAnyAsync<ArgumentException>(async () => await Selector(new Backend(), unused).SelectAsync("invalid", records, AcquisitionRandom(), Propose));
            Assert.Equal(0, unused.Snapshot().Spent["cost_units"]);
        }
    }

    [Fact]
    public void Training_records_reject_cache_hits_infeasibility_missing_work_and_identity_mismatch()
    {
        Assert.Throws<ArgumentException>(() => Observation(0, cache: EvolutionCacheStatus.Hit));
        Assert.Throws<ArgumentException>(() => Observation(0, attempts: 0));
        Assert.Throws<ArgumentException>(() => Observation(0, violation: 1));
        Assert.Throws<ArgumentException>(() => Observation(0, evaluationGenome: "different"));
    }

    [Fact]
    public async Task Inputs_and_explicit_random_stream_reproduce_selection_without_hidden_learning()
    {
        var first = await Selector(new Backend(), Ledger()).SelectAsync("replay", Observations(), AcquisitionRandom(), Propose);
        var second = await Selector(new Backend(), Ledger()).SelectAsync("replay", Observations(), AcquisitionRandom(), Propose);
        Assert.Equal(first.Candidate.Id, second.Candidate.Id); Assert.Equal(first.OperationIdentity, second.OperationIdentity);
        Assert.Equal(first.TrainingIdentity, second.TrainingIdentity); Assert.Equal(first.ModelVersionHash, second.ModelVersionHash);
        Assert.Equal(first.Predictions.Select(p => p.Mean), second.Predictions.Select(p => p.Mean));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_declared_samples_cannot_be_counted_as_independent_training_records(bool differentScope)
    {
        var first = Origin("shared", "first");
        var second = Origin("shared", "second", differentScope ? 'b' : 'a');
        var ledger = Ledger(); var backend = new Backend();
        await Assert.ThrowsAsync<ArgumentException>(async () => await Selector(backend, ledger).SelectAsync("overlap",
            new[] { Observation(0, origin: first), Observation(1, origin: second) }, AcquisitionRandom(), Propose));
        Assert.Equal(0, backend.Fits); Assert.Equal(0, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task Training_identity_includes_original_measurement_sample_identity()
    {
        var first = await Selector(new Backend(), Ledger()).SelectAsync("same-labels",
            new[] { Observation(0, origin: Origin("sample-a", "a")), Observation(1, origin: Origin("sample-b", "b")) }, AcquisitionRandom(), Propose);
        var second = await Selector(new Backend(), Ledger()).SelectAsync("same-labels",
            new[] { Observation(0, origin: Origin("sample-c", "c")), Observation(1, origin: Origin("sample-d", "d")) }, AcquisitionRandom(), Propose);
        Assert.NotEqual(first.TrainingIdentity, second.TrainingIdentity);
        Assert.NotEqual(first.OperationIdentity, second.OperationIdentity);
    }

    private static EvolutionMeasurementOrigin Origin(string shared, string unique, char scope = 'a') => new(new string(scope, 64), "source", unique,
        new[] { shared, unique }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, "calls", "test-v1");

    private sealed class Backend : IEvolutionSurrogateTrainer<int>, IEvolutionSurrogateModel<int>
    {
        public string VersionHash => "fitted-v1";
        public bool Reliable = true;
        public bool IsReliable => Reliable;
        public int Fits, TrainingCount;
        public Exception? FitError, InferenceError;
        public decimal FitCost = 2, InferenceCost = 1;
        public EvolutionResourceOutcome FitOutcome = EvolutionResourceOutcome.Completed, InferenceOutcome = EvolutionResourceOutcome.Completed;
        public IReadOnlyList<EvolutionSurrogatePrediction> Predictions = Pool.Select(p => new EvolutionSurrogatePrediction(p.Id, p.Genome, 0, true)).ToArray();
        public ValueTask<EvolutionResourceResult<IEvolutionSurrogateModel<int>>> FitAsync(IReadOnlyList<EvolutionSurrogateObservation<int>> observations, CancellationToken cancellationToken = default)
        {
            Fits++; TrainingCount = observations.Count;
            if (FitError is not null) throw FitError;
            return new(new EvolutionResourceResult<IEvolutionSurrogateModel<int>>(this, Cost(FitCost), FitOutcome));
        }
        public ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>> PredictAsync(IReadOnlyList<EvolutionCanonicalGenome<int>> candidates, CancellationToken cancellationToken = default)
        {
            if (InferenceError is not null) throw InferenceError;
            return new(new EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>(Predictions, Cost(InferenceCost), InferenceOutcome));
        }
    }
}
