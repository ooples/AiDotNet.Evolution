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
        await ensemble.ProposeAsync(Context(random, generation: 7));
        ensemble.Observe(EvaluationForGeneration(7), EvolutionArchiveInsertionResult.Inserted);

        int observed = left.Observed + right.Observed;
        Assert.Equal(1, observed);
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

        public int Proposed { get; private set; }

        public int Observed { get; private set; }

        public ValueTask<EvolutionResourceResult<int>> ProposeAsync(
            EvolutionVariationContext<int> context, CancellationToken cancellationToken = default)
        {
            Proposed++;
            if (Throw is not null) throw Throw;
            return new ValueTask<EvolutionResourceResult<int>>(
                new EvolutionResourceResult<int>(Proposed, EvolutionResources.Empty));
        }

        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
            => Observed++;

        public string CaptureState() => _state;

        public void RestoreState(string state) => _state = state;
    }
}
