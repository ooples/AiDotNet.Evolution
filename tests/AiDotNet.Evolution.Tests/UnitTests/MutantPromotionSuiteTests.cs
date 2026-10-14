using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// US-03 (#21): a candidate whose outputs are wrong cannot become the winner, whatever its scalar score.
/// </summary>
/// <remarks>
/// <para>
/// The story's first criterion is about <b>promotion</b>, not about scoring: an evaluator is allowed to hand
/// a fast-but-wrong candidate a wonderful quality, and the engine still must not report it as the run's
/// best. The metric the issue asks for is a count - false promotions of seeded mutants, target zero - so
/// this is written as a sweep over mutants rather than one example, and the count is asserted rather than
/// the shape of any single run.
/// </para>
/// <para>
/// A mutant here is the realistic shape: it reports the <b>highest possible quality</b> and a non-zero
/// constraint violation, which is how an evaluator says "this answer is wrong". The honest candidate
/// reports a worse quality and no violation. If promotion were decided on the scalar alone, every single
/// mutant would win, which is what makes the control arm below load-bearing.
/// </para>
/// </remarks>
public sealed class MutantPromotionSuiteTests
{
    /// <summary>A task where the mutant scores perfectly and declares its own wrongness.</summary>
    private sealed class MutantTask : IEvolutionTask<TestGenome>
    {
        private readonly int _mutantValue;
        private readonly bool _declareViolation;

        public MutantTask(int mutantValue, bool declareViolation)
        {
            _mutantValue = mutantValue;
            _declareViolation = declareViolation;
        }

        public string Id => "us03-mutant";
        public string VersionHash => "us03-mutant-v1";
        public string EvaluatorVersionHash => "us03-mutant-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome,
            CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome,
                genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            bool isMutant = value == _mutantValue;
            var descriptors = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                ["x"] = Math.Max(0, Math.Min(100, value))
            };
            // The mutant's score is the best any candidate can report, so a promotion decided on quality
            // alone promotes it every time.
            double quality = isMutant ? 1.0 : 0.1 + (value % 5) * 0.01;
            double[]? violations = isMutant && _declareViolation ? new[] { 1.0 } : null;
            return new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed, quality,
                descriptors: descriptors, constraintViolations: violations));
        }
    }

    private sealed class SweepVariation : IVariationOperator<TestGenome>
    {
        private int _next;

        public string Id => "us03-sweep";
        public string VersionHash => "us03-sweep-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ValueTask<TestGenome>(new TestGenome(2 + Interlocked.Increment(ref _next)));
        }
    }

    private static EvolutionEngineOptions Options() => new()
    {
        RunId = "us03-mutant-suite",
        Seed = 17,
        // The evaluation budget, not a retry count: at 1 the run stops after the seed and the sweep below
        // never reaches its mutant - which is how the first version of this suite measured its own fixture.
        MaxEvaluationAttempts = 24,
        MaxProposals = 12,
        MaxGenerations = 12,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        MigrationInterval = 0,
        MigrantsPerIsland = 1,
        CheckpointInterval = 0
    };

    private static MapElitesArchive<TestGenome> Archive() => new(new[]
    {
        new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
    });

    private static async Task<(bool Promoted, double? BestQuality)> RunAsync(int mutantValue, bool declareViolation)
    {
        EvolutionRunResult<TestGenome> result = await new EvolutionEngine<TestGenome>(
            new MutantTask(mutantValue, declareViolation), new SweepVariation(), _ => Archive(), Options())
            .RunAsync(new[] { new TestGenome(1), new TestGenome(2) });
        EvolutionArchiveEntry<TestGenome>? best = result.Best;
        return (best is not null && best.Candidate.CanonicalGenome.Genome.Value == mutantValue,
            best?.Evaluation.Quality);
    }

    [Fact]
    public async Task No_seeded_mutant_is_ever_promoted_however_good_its_scalar_score_is()
    {
        // The metric US-03 asks for: false promotions of seeded mutants, target 0. Swept rather than
        // exemplified, because one passing example cannot distinguish a gate from a coincidence of
        // ordering.
        var promoted = new List<int>();
        foreach (int mutantValue in new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 })
        {
            (bool wasPromoted, double? bestQuality) = await RunAsync(mutantValue, declareViolation: true);
            if (wasPromoted) promoted.Add(mutantValue);
            // And the winner is never carrying the mutant's perfect score under another identity.
            Assert.True(bestQuality is null || bestQuality.Value < 1.0,
                "the winner reported the mutant's own quality of 1.0");
        }

        Assert.Empty(promoted);
    }

    [Fact]
    public async Task The_same_candidate_wins_when_it_stops_declaring_itself_wrong()
    {
        // The control arm. Without it the sweep above would pass for an uninteresting reason - a mutant
        // that could never win on score anyway, or a seed the engine never reaches - and the suite would
        // measure its own fixture rather than the gate.
        (bool promoted, double? bestQuality) = await RunAsync(mutantValue: 4, declareViolation: false);

        Assert.True(promoted, "the identical candidate did not win even with no violation declared, so the "
            + "sweep above cannot be attributed to the constraint gate");
        Assert.Equal(1.0, bestQuality.GetValueOrDefault(), 10);
    }
}
