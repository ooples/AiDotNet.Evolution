using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// US-06 (#24): re-measuring unchanged code cannot produce a gain.
/// </summary>
/// <remarks>
/// The story's metric is a count - false gains from re-measuring unchanged code, target zero. The hazard is
/// specific: a noisy evaluator asked twice about the same program returns two different numbers, and if the
/// second is luckier the search records an improvement that no code change earned. The evaluation cache is
/// what prevents it, by returning the first measurement rather than taking a second sample.
/// </remarks>
public sealed class RemeasurementGainSuiteTests
{
    /// <summary>A task whose quality rises on every call, so re-measurement always looks like a gain.</summary>
    private sealed class DriftingNoiseTask : IEvolutionTask<TestGenome>
    {
        private int _calls;

        public string Id => "us06-drift";
        public string VersionHash => "us06-drift-v1";
        public string EvaluatorVersionHash => "us06-drift-evaluator-v1";

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) =>
            // Identity is the genome's own value, so re-proposing a value is re-measuring unchanged code.
            new(new EvolutionCanonicalGenome<TestGenome>(genome,
                genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            return new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed, 0.10 * call,
                descriptors: new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = 5 }));
        }
    }

    /// <summary>Alternates between two identities, so each is forgotten before its next turn.</summary>
    /// <remarks>
    /// One repeated identity is not enough to reach re-measurement at all: the deduplication set refuses
    /// it as Duplicate, and with a capacity of one nothing ever pushes it out. Alternating A, B, A, B is
    /// what evicts A before A comes round again, which is the only way the engine evaluates unchanged code
    /// twice - and therefore the only regime in which this metric can be measured.
    /// </remarks>
    private sealed class AlternatingVariation : IVariationOperator<TestGenome>
    {
        private int _next;

        public string Id => "us06-alternating";
        public string VersionHash => "us06-alternating-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<TestGenome>(new TestGenome(Interlocked.Increment(ref _next) % 2 == 0 ? 5 : 6));
        }
    }

    // DeduplicationCapacity = 1 is what makes re-measurement possible at all: at 0 the deduplication set
    // never forgets, so a proposal with an identity already evaluated is refused as Duplicate before any
    // evaluator is called. That is the FIRST line of defence against a re-measurement gain, and it made
    // the control arm below pass for the wrong reason - no gain appeared with the cache off either,
    // because nothing was ever re-measured. With a capacity of 1 the oldest committed genome is forgotten
    // and the same identity is evaluated again, which is the regime this metric is about.
    private static EvolutionEngineOptions Options(bool cache, int deduplicationCapacity) => new()
    {
        RunId = "us06-remeasurement",
        Seed = 23,
        MaxEvaluationAttempts = 10,
        MaxProposals = 10,
        MaxGenerations = 10,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        MigrationInterval = 0,
        MigrantsPerIsland = 1,
        CheckpointInterval = 0,
        EnableEvaluationCache = cache,
        DeduplicateFailedCandidates = false,
        DeduplicationCapacity = deduplicationCapacity
    };

    private static MapElitesArchive<TestGenome> Archive() => new(new[]
    {
        new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
    });

    private static async Task<(int Improvements, int EvaluatorCalls, double? Best)> RunAsync(
        bool cache, int deduplicationCapacity)
    {
        var task = new DriftingNoiseTask();
        var observer = new EvaluationRecordingObserver();
        EvolutionRunResult<TestGenome> result = await new EvolutionEngine<TestGenome>(
                task, new AlternatingVariation(), _ => Archive(), Options(cache, deduplicationCapacity), observer: observer)
            .RunAsync(new[] { new TestGenome(5) });

        // An improvement credited to re-measurement: a later evaluation of the same identity reporting a
        // better quality than the first one did.
        var firstSeen = new Dictionary<string, double>(StringComparer.Ordinal);
        int improvements = 0;
        foreach (EvolutionEvaluation evaluation in observer.Evaluations)
        {
            if (evaluation.Quality is not { } quality) continue;
            if (!firstSeen.TryGetValue(evaluation.GenomeId, out double first)) firstSeen[evaluation.GenomeId] = quality;
            else if (quality > first) improvements++;   // the SAME identity measured better than before
        }
        return (improvements, task.Calls, result.Best?.Evaluation.Quality);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Re_measuring_unchanged_code_produces_no_gain_at_the_default_capacity(bool cache)
    {
        // The metric US-06 asks for, in the configuration the engine ships with. At DeduplicationCapacity
        // 0 the seen set never forgets, so a proposal whose identity was already evaluated is refused as
        // Duplicate before any evaluator runs. The evaluator here reports a better quality on every call,
        // so a single extra sample would show up as a gain - and none appears, with the cache on OR off,
        // which locates the protection in deduplication rather than in the cache.
        (int improvements, int calls, double? best) = await RunAsync(cache, deduplicationCapacity: 0);

        Assert.Equal(0, improvements);
        Assert.Equal(2, calls); // the two distinct identities, each measured exactly once
        Assert.Equal(0.20, best.GetValueOrDefault(), 10);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_bounded_deduplication_capacity_lets_re_measurement_manufacture_a_gain(bool cache)
    {
        // The boundary, measured rather than assumed, because it is the opposite of what the option's
        // documentation leads a reader to expect. DeduplicationCapacity exists to bound memory on a long
        // run, and forgetting a genome evicts it from the seen set AND the evaluation cache together. So
        // a forgotten identity is evaluated again, a noisy evaluator returns a luckier number, and the
        // run records an improvement that no code change earned.
        //
        // Measured: 8 of 10 evaluations, with the cache both on and off - the cache cannot help, because
        // the same eviction that allows the re-proposal has already discarded its cached measurement.
        //
        // Whether that is acceptable is a judgement for the owner: the alternative is retaining cache
        // entries for genomes the run has forgotten, which defeats the bound the capacity exists to
        // provide. This test pins the behaviour so the trade-off is visible rather than discovered.
        (int improvements, int calls, _) = await RunAsync(cache, deduplicationCapacity: 1);

        Assert.True(improvements > 0,
            "a bounded capacity no longer re-measures, so US-06's metric is now unconditional and this "
            + "test should be replaced by that stronger claim: improvements=" + improvements);
        Assert.True(calls > 2, "nothing was re-measured, so this measures the fixture: calls=" + calls);
    }
}
