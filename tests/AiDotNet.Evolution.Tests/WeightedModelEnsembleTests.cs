using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-50 (#163): a proposal's model is drawn from the configured weights using the run's own
/// stream, so the mixture is what was asked for and a replay draws the same models in the same
/// order.
/// </summary>
public sealed class WeightedModelEnsembleTests
{
    /// <summary>
    /// The story's metric: over 10,000 draws every model's empirical frequency is within two
    /// percentage points of its weight.
    /// </summary>
    [Fact]
    public async Task Empirical_model_frequency_matches_the_configured_weights()
    {
        var sources = new[] { new StubSource("fast"), new StubSource("balanced"), new StubSource("deep") };
        double[] weights = { 0.70, 0.25, 0.05 };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble",
            sources.Select((source, i) => new EvolutionModelWeight<int>(source, weights[i])).ToArray());
        var random = new StableRandom(seed: 20261013);

        const int draws = 10_000;
        var counts = new int[sources.Length];
        for (int i = 0; i < draws; i++)
        {
            // Which source's OWN counter advanced is the drawn model. Reading the ensemble's
            // cumulative accounts instead would just name whichever model is ahead overall.
            int[] before = sources.Select(s => s.Proposed).ToArray();
            await ensemble.ProposeAsync(Context(random, i));
            for (int m = 0; m < sources.Length; m++)
            {
                if (sources[m].Proposed > before[m]) { counts[m]++; break; }
            }
        }

        for (int m = 0; m < sources.Length; m++)
        {
            double observed = (double)counts[m] / draws;
            double delta = Math.Abs(observed - weights[m]);
            Assert.True(delta <= 0.02,
                $"{sources[m].Id} was drawn {observed:P2} of the time against a weight of {weights[m]:P2}; "
                    + $"the gap {delta:P2} exceeds two percentage points.");
        }

        // The ensemble's own accounting must agree with the observed draws, or its per-model
        // metering would be reporting something other than what happened.
        var accounts = ensemble.Accounts;
        for (int m = 0; m < sources.Length; m++)
        {
            Assert.Equal(counts[m], accounts[m].Proposals);
        }
    }

    /// <summary>
    /// Replay determinism: the same seed draws the identical sequence, which is what lets a run be
    /// reproduced from its seed alone.
    /// </summary>
    [Fact]
    public async Task The_same_seed_draws_the_same_models_in_the_same_order()
    {
        string[] first = await DrawSequence(seed: 777, count: 200);
        string[] second = await DrawSequence(seed: 777, count: 200);
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Control arm: a different seed must draw a different sequence, otherwise the test above would
    /// pass on an ensemble that ignores its random stream entirely.
    /// </summary>
    [Fact]
    public async Task A_different_seed_draws_a_different_sequence()
    {
        string[] first = await DrawSequence(seed: 777, count: 200);
        string[] other = await DrawSequence(seed: 778, count: 200);
        Assert.NotEqual(first, other);
    }

    /// <summary>
    /// A failure is charged to the model that produced it and counted, so a model that throws is
    /// visible as the cause instead of being averaged into the run's error rate.
    /// </summary>
    [Fact]
    public async Task A_failure_is_charged_to_the_model_that_caused_it()
    {
        var healthy = new StubSource("healthy");
        var broken = new StubSource("broken") { Throw = new InvalidOperationException("model refused") };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble",
            new[]
            {
                new EvolutionModelWeight<int>(healthy, 1.0),
                new EvolutionModelWeight<int>(broken, 1.0),
            });

        var random = new StableRandom(seed: 4242);
        int failures = 0;
        for (int generation = 0; generation < 40; generation++)
        {
            try
            {
                await ensemble.ProposeAsync(Context(random, generation));
            }
            catch (InvalidOperationException)
            {
                failures++;
            }
        }

        var accounts = ensemble.Accounts.ToDictionary(a => a.ModelId, StringComparer.Ordinal);
        Assert.Equal(failures, accounts["broken"].Failures);
        Assert.Equal(0, accounts["healthy"].Failures);
        Assert.Equal(40, accounts["broken"].Proposals + accounts["healthy"].Proposals);
        Assert.True(failures > 0, "the broken model was never drawn, so nothing was proved.");
    }

    /// <summary>
    /// A cancellation is the run stopping, not a model failing, so it must not be charged to one.
    /// </summary>
    [Fact]
    public async Task A_cancellation_is_not_charged_to_a_model()
    {
        var source = new StubSource("only") { Throw = new OperationCanceledException() };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble", new[] { new EvolutionModelWeight<int>(source, 1.0) });

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await ensemble.ProposeAsync(Context(new StableRandom(1), generation: 0)));

        Assert.Equal(0, ensemble.Accounts.Single().Failures);
        Assert.Equal(1, ensemble.Accounts.Single().Proposals);
    }

    /// <summary>
    /// An outcome reaches the model that produced the proposal, and only that one.
    /// </summary>
    [Fact]
    public async Task An_outcome_reaches_only_the_model_that_produced_it()
    {
        var left = new StubSource("left");
        var right = new StubSource("right");
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble",
            new[] { new EvolutionModelWeight<int>(left, 1.0), new EvolutionModelWeight<int>(right, 1.0) });

        var random = new StableRandom(seed: 99);
        int beforeLeft = left.Proposed, beforeRight = right.Proposed;
        await ensemble.ProposeAsync(Context(random, generation: 7));

        // Which source's own counter advanced is the model that produced this proposal. Asserting
        // only that ONE outcome was delivered would pass even if every outcome went to `left`.
        StubSource producer = left.Proposed > beforeLeft ? left : right;
        StubSource other = ReferenceEquals(producer, left) ? right : left;
        _ = beforeRight;

        ensemble.Observe(EvaluationForGeneration(7), EvolutionArchiveInsertionResult.Inserted);

        Assert.Equal(1, producer.Observed);
        Assert.Equal(0, other.Observed);
    }

    /// <summary>
    /// An unknown or repeated outcome is refused rather than silently dropped or broadcast.
    /// </summary>
    [Fact]
    public async Task An_unknown_or_repeated_outcome_is_refused()
    {
        var ensemble = Build(("only", 1.0));
        var random = new StableRandom(seed: 5);
        await ensemble.ProposeAsync(Context(random, generation: 3));

        Assert.Throws<InvalidOperationException>(
            () => ensemble.Observe(EvaluationForGeneration(99), null));

        ensemble.Observe(EvaluationForGeneration(3), null);
        Assert.Throws<InvalidOperationException>(
            () => ensemble.Observe(EvaluationForGeneration(3), null));
    }

    /// <summary>
    /// The mixture is part of the identity: state captured from one set of weights must not be
    /// restored into another, because the replay would draw a different sequence.
    /// </summary>
    [Fact]
    public async Task State_round_trips_and_refuses_a_different_mixture()
    {
        var ensemble = Build(("fast", 0.80), ("deep", 0.20));
        var random = new StableRandom(seed: 31337);
        for (int generation = 0; generation < 5; generation++)
        {
            await ensemble.ProposeAsync(Context(random, generation));
        }

        string state = ensemble.CaptureState();

        var restored = Build(("fast", 0.80), ("deep", 0.20));
        restored.RestoreState(state);
        Assert.Equal(
            ensemble.Accounts.Select(a => (a.ModelId, a.Proposals, a.Failures)),
            restored.Accounts.Select(a => (a.ModelId, a.Proposals, a.Failures)));

        // The routing table came back too: an outcome for a generation proposed before the capture
        // is still routable afterwards.
        restored.Observe(EvaluationForGeneration(4), null);

        var different = Build(("fast", 0.50), ("deep", 0.50));
        Assert.Throws<InvalidOperationException>(() => different.RestoreState(state));
    }

    /// <summary>
    /// The configuration refuses what it cannot account for.
    /// </summary>
    [Fact]
    public void An_invalid_model_set_is_refused()
    {
        var source = new StubSource("a");
        Assert.Throws<ArgumentException>(
            () => new EvolutionWeightedModelEnsemble<int>("e", Array.Empty<EvolutionModelWeight<int>>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionModelWeight<int>(source, 0.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionModelWeight<int>(source, -1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionModelWeight<int>(source, double.NaN));
        Assert.Throws<ArgumentException>(() => new EvolutionWeightedModelEnsemble<int>(
            "e",
            new[]
            {
                new EvolutionModelWeight<int>(new StubSource("same"), 1.0),
                new EvolutionModelWeight<int>(new StubSource("same"), 1.0),
            }));
    }

    /// <summary>
    /// Weights that sum past the double range are refused: a non-finite total would make the draw
    /// compare an infinite point against finite bounds and pick the last model every time, silently
    /// ignoring the configured mixture.
    /// </summary>
    [Fact]
    public void Weights_that_do_not_sum_finitely_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWeightedModelEnsemble<int>(
            "ensemble",
            new[]
            {
                new EvolutionModelWeight<int>(new StubSource("huge-a"), double.MaxValue),
                new EvolutionModelWeight<int>(new StubSource("huge-b"), double.MaxValue),
            }));
    }

    /// <summary>
    /// A model that reports failure without throwing is still charged: the story charges a model
    /// that "fails or times out", which is about the outcome rather than the mechanism.
    /// </summary>
    [Fact]
    public async Task A_failed_outcome_without_an_exception_is_still_charged()
    {
        var source = new StubSource("reports-failure") { Outcome = EvolutionResourceOutcome.Failed };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble", new[] { new EvolutionModelWeight<int>(source, 1.0) });

        await ensemble.ProposeAsync(Context(new StableRandom(11), generation: 0));

        var account = ensemble.Accounts.Single();
        Assert.Equal(1, account.Proposals);
        Assert.Equal(1, account.Failures);
    }

    /// <summary>
    /// A null result breaks the interface's contract and is charged to the model that returned it,
    /// rather than passed upward to fail somewhere that cannot name the model.
    /// </summary>
    [Fact]
    public async Task A_null_result_is_charged_and_refused()
    {
        var source = new StubSource("returns-null") { ReturnNull = true };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble", new[] { new EvolutionModelWeight<int>(source, 1.0) });

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ensemble.ProposeAsync(Context(new StableRandom(12), generation: 0)));

        Assert.Equal(1, ensemble.Accounts.Single().Failures);
    }

    /// <summary>
    /// A generation that already has a proposal awaiting an outcome cannot be proposed again:
    /// overwriting the route would send the first outcome to the second model.
    /// </summary>
    [Fact]
    public async Task A_generation_cannot_hold_two_pending_proposals()
    {
        var ensemble = Build(("only", 1.0));
        var random = new StableRandom(seed: 13);
        await ensemble.ProposeAsync(Context(random, generation: 4));

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await ensemble.ProposeAsync(Context(random, generation: 4)));
    }

    /// <summary>
    /// A cancelled proposal releases its route, so the generation can be proposed again once the
    /// run resumes.
    /// </summary>
    [Fact]
    public async Task A_cancelled_proposal_releases_its_route()
    {
        var source = new StubSource("cancels") { Throw = new OperationCanceledException() };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble", new[] { new EvolutionModelWeight<int>(source, 1.0) });
        var random = new StableRandom(seed: 14);

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await ensemble.ProposeAsync(Context(random, generation: 2)));

        source.Throw = null;
        await ensemble.ProposeAsync(Context(random, generation: 2));
        Assert.Equal(0, ensemble.Accounts.Single().Failures);
    }

    /// <summary>
    /// A checkpoint whose pending table is missing is refused: an omitted table is not the same
    /// claim as an empty one, and accepting it would discard every unrouted proposal in silence.
    /// </summary>
    [Fact]
    public void A_state_without_a_pending_table_is_refused()
    {
        var ensemble = Build(("only", 1.0));
        string state = ensemble.CaptureState();
        string stripped = state.Replace("\"Pending\":[]", "\"Pending\":null");

        Assert.Throws<InvalidOperationException>(() => ensemble.RestoreState(stripped));
    }

    /// <summary>
    /// The documented precedence, exercised: the ensemble nests inside the resource meter, which
    /// nests inside the bandit portfolio. The portfolio decides whether this operator is asked; the
    /// ensemble decides which model answers; and the outcome still reaches the model that produced
    /// the proposal through both layers.
    /// </summary>
    [Fact]
    public async Task The_ensemble_composes_under_the_meter_and_the_portfolio()
    {
        var left = new StubSource("left");
        var right = new StubSource("right");
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble",
            new[] { new EvolutionModelWeight<int>(left, 1.0), new EvolutionModelWeight<int>(right, 1.0) });

        var ledger = new EvolutionResourceLedger(
            "ensemble-composition-test", EvolutionResources.Of("cost_units", 10_000m));
        var metered = new ResourceMeteredVariationOperator<int>(
            ensemble, ledger, EvolutionResources.Of("cost_units", 5m), "cost-units-v1");
        var portfolio = new AdaptiveVariationPortfolio<int>(new[] { metered });

        int beforeLeft = left.Proposed, beforeRight = right.Proposed;
        await portfolio.ProposeAsync(Context(new StableRandom(seed: 2026), generation: 1));

        StubSource producer = left.Proposed > beforeLeft ? left : right;
        StubSource other = ReferenceEquals(producer, left) ? right : left;
        _ = beforeRight;
        Assert.Equal(1, producer.Proposed - (ReferenceEquals(producer, left) ? beforeLeft : beforeRight));

        portfolio.Observe(EvaluationForGeneration(1), EvolutionArchiveInsertionResult.Inserted);

        // Through the meter and the portfolio, the outcome still lands on the model that wrote the
        // proposal - and only on it.
        Assert.Equal(1, producer.Observed);
        Assert.Equal(0, other.Observed);
    }

    private static EvolutionWeightedModelEnsemble<int> Build(params (string Id, double Weight)[] models)
        => new(
            "ensemble",
            models.Select(m => new EvolutionModelWeight<int>(new StubSource(m.Id), m.Weight)).ToArray());

    private static async Task<string[]> DrawSequence(int seed, int count)
    {
        var sources = new[] { new StubSource("a"), new StubSource("b"), new StubSource("c") };
        var ensemble = new EvolutionWeightedModelEnsemble<int>(
            "ensemble",
            new[]
            {
                new EvolutionModelWeight<int>(sources[0], 0.5),
                new EvolutionModelWeight<int>(sources[1], 0.3),
                new EvolutionModelWeight<int>(sources[2], 0.2),
            });

        var random = new StableRandom((ulong)seed);
        var sequence = new string[count];
        for (int i = 0; i < count; i++)
        {
            int before0 = sources[0].Proposed, before1 = sources[1].Proposed, before2 = sources[2].Proposed;
            await ensemble.ProposeAsync(Context(random, i));
            sequence[i] = sources[0].Proposed > before0 ? "a" : sources[1].Proposed > before1 ? "b" : "c";
            _ = before2;
        }

        return sequence;
    }

    private static EvolutionVariationContext<int> Context(StableRandom random, long generation)
    {
        var measured = EvaluationForGeneration(0);
        var parent = new EvolutionArchiveEntry<int>(
            new EvolutionCellKey(new[] { 0 }),
            new EvolutionCandidate<int>(0, new EvolutionCanonicalGenome<int>(1, "g0"), measured.Lineage),
            measured);
        return new EvolutionVariationContext<int>(
            parent, Array.Empty<EvolutionArchiveEntry<int>>(), random, generation, island: 0);
    }

    private static EvolutionEvaluation EvaluationForGeneration(long generation)
        => new(generation, "g" + generation, EvolutionEvaluationStatus.Completed, 0.5,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = 1 },
            Array.Empty<double>(), Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 2),
            new EvolutionLineage(null, null, "test", null, generation, 0, (ulong)generation),
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "evaluator", "config");

    private sealed class StubSource : ICostedEvolutionProposalSource<int>
    {
        private string _state = "{}";

        public StubSource(string id) => Id = id;

        public string Id { get; }

        public string VersionHash => "stub-v1";

        public Exception? Throw { get; set; }

        public EvolutionResourceOutcome Outcome { get; set; } = EvolutionResourceOutcome.Completed;

        public bool ReturnNull { get; set; }

        public int Proposed { get; private set; }

        public int Observed { get; private set; }

        public ValueTask<EvolutionResourceResult<int>> ProposeAsync(
            EvolutionVariationContext<int> context, CancellationToken cancellationToken = default)
        {
            Proposed++;
            if (Throw is not null) throw Throw;
            if (ReturnNull) return new ValueTask<EvolutionResourceResult<int>>((EvolutionResourceResult<int>)null!);
            // An explicit cost_units receipt: ResourceMeteredVariationOperator refuses a proposal
            // whose receipt omits the unit it meters, so a stub without one is not a well-formed
            // costed source.
            return new ValueTask<EvolutionResourceResult<int>>(new EvolutionResourceResult<int>(
                Proposed, EvolutionResources.Of("cost_units", 2m), Outcome));
        }

        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
            => Observed++;

        public string CaptureState() => _state;

        public void RestoreState(string state) => _state = state;
    }
}
