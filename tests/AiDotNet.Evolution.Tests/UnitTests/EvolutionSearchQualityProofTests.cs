using System.Globalization;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// Deterministic, equal-budget proof that the engine provides useful search pressure on a generated family of
/// landscapes. These are algorithm-quality checks rather than wall-clock benchmarks: every strategy receives the
/// same evaluator-call budget and every random decision comes from <see cref="StableRandom"/>.
/// </summary>
public sealed class EvolutionSearchQualityProofTests
{
    private const int GenomeWidth = 48;
    private const int EvaluationBudget = 4_096;
    private static readonly ulong[] ProofSeeds = { 11UL, 29UL, 47UL, 83UL, 131UL };

    public static IEnumerable<object[]> Landscapes()
    {
        foreach (SearchLandscape landscape in Enum.GetValues(typeof(SearchLandscape)))
            yield return new object[] { landscape };
    }

    [Theory]
    [MemberData(nameof(Landscapes))]
    public async Task EvolutionBeatsUniformRandomAtTheSameEvaluationBudget(SearchLandscape landscape)
    {
        SearchQualityAggregate evolution = new();
        SearchQualityAggregate random = new();

        foreach (ulong seed in ProofSeeds)
        {
            SearchQualityObservation evolved = await RunEvolutionAsync(landscape, seed);
            SearchQualityObservation sampled = RunUniformRandom(landscape, seed, evolved.EvaluationCount);
            evolution.Add(evolved);
            random.Add(sampled);

            Assert.InRange(evolved.EvaluationCount, 1, EvaluationBudget);
            Assert.Equal(evolved.EvaluationCount, evolved.UniqueEvaluationCount);
            Assert.Equal(evolved.EvaluationCount, sampled.EvaluationCount);
            Assert.Equal(sampled.EvaluationCount, sampled.UniqueEvaluationCount);
        }

        LandscapeDefinition definition = LandscapeGenerator.Get(landscape);
        Assert.True(
            evolution.MeanBestQuality >= random.MeanBestQuality + definition.RequiredMeanQualityAdvantage,
            $"{landscape}: evolution mean {evolution.MeanBestQuality:R}, random mean " +
            $"{random.MeanBestQuality:R}, required advantage {definition.RequiredMeanQualityAdvantage:R}.");
        Assert.True(
            evolution.MeanCoverage >= random.MeanCoverage + definition.RequiredMeanCoverageAdvantage,
            $"{landscape}: evolution coverage {evolution.MeanCoverage:R}, random coverage " +
            $"{random.MeanCoverage:R}, required advantage {definition.RequiredMeanCoverageAdvantage:R}.");
        Assert.True(
            evolution.TargetHitCount > random.TargetHitCount,
            $"{landscape}: evolution hit the declared target {evolution.TargetHitCount} times and random hit it " +
            $"{random.TargetHitCount} times.");
        Assert.True(
            evolution.MeanEvaluationsToTarget < random.MeanEvaluationsToTarget,
            $"{landscape}: evolution mean time-to-target {evolution.MeanEvaluationsToTarget:R}, random " +
            $"{random.MeanEvaluationsToTarget:R}.");
    }

    private static async Task<SearchQualityObservation> RunEvolutionAsync(SearchLandscape landscape, ulong seed)
    {
        LandscapeDefinition definition = LandscapeGenerator.Get(landscape);
        var task = new LandscapeTask(definition);
        var options = new EvolutionEngineOptions
        {
            RunId = string.Concat("search-quality-", ((int)landscape).ToString(CultureInfo.InvariantCulture)),
            Seed = seed,
            MaxEvaluationAttempts = EvaluationBudget,
            MaxProposals = EvaluationBudget * 16,
            MaxGenerations = EvaluationBudget * 16,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            IslandCount = 1,
            MigrationInterval = 0,
            MigrantsPerIsland = 1,
            InspirationCount = 0
        };
        var engine = new EvolutionEngine<BitGenome>(
            task,
            new SingleBitVariation(GenomeWidth),
            _ => CreateArchive(),
            options,
            selection: new BestEliteSelection());

        EvolutionRunResult<BitGenome> result = await engine.RunAsync(new[] { new BitGenome(0) });
        EvolutionArchiveEntry<BitGenome> best = result.Best ??
            throw new InvalidOperationException("The generated landscape produced no completed evaluation.");
        return new SearchQualityObservation(
            best.Evaluation.Quality ?? double.NegativeInfinity,
            result.Islands[0].Count,
            checked((int)result.Counters.EvaluationAttempts),
            task.UniqueEvaluationCount,
            task.EvaluationsToTarget.HasValue,
            task.EvaluationsToTarget ?? EvaluationBudget + 1);
    }

    private static SearchQualityObservation RunUniformRandom(
        SearchLandscape landscape,
        ulong seed,
        int evaluationBudget)
    {
        LandscapeDefinition definition = LandscapeGenerator.Get(landscape);
        StableRandom random = StableRandom.CreateStream(seed, 0x554E49464F524DUL);
        var seen = new HashSet<ulong>();
        var occupied = new HashSet<int>();
        double best = double.NegativeInfinity;
        int? evaluationsToTarget = null;
        int evaluations = 0;
        while (evaluations < evaluationBudget)
        {
            ulong bits = random.NextUInt64() & BitGenome.Mask(GenomeWidth);
            if (!seen.Add(bits)) continue;
            evaluations++;
            double quality = definition.Evaluate(bits);
            occupied.Add(BitGenome.PopulationCount(bits));
            if (quality > best) best = quality;
            if (!evaluationsToTarget.HasValue && quality >= definition.TargetQuality)
                evaluationsToTarget = evaluations;
        }

        return new SearchQualityObservation(
            best,
            occupied.Count,
            evaluations,
            seen.Count,
            evaluationsToTarget.HasValue,
            evaluationsToTarget ?? EvaluationBudget + 1);
    }

    private static MapElitesArchive<BitGenome> CreateArchive() => new(new[]
    {
        new EvolutionDescriptorDefinition(
            LandscapeTask.PopulationCountDescriptor,
            0,
            GenomeWidth,
            GenomeWidth + 1,
            EvolutionOutOfRangePolicy.Reject)
    });

    public enum SearchLandscape
    {
        OneMax = 0,
        WeightedOneMax = 1,
        LeadingOnes = 2
    }

    private sealed class LandscapeDefinition
    {
        internal LandscapeDefinition(
            SearchLandscape kind,
            Func<ulong, double> evaluate,
            double targetQuality,
            double requiredMeanQualityAdvantage,
            double requiredMeanCoverageAdvantage)
        {
            Kind = kind;
            Evaluate = evaluate;
            TargetQuality = targetQuality;
            RequiredMeanQualityAdvantage = requiredMeanQualityAdvantage;
            RequiredMeanCoverageAdvantage = requiredMeanCoverageAdvantage;
        }

        internal SearchLandscape Kind { get; }
        internal Func<ulong, double> Evaluate { get; }
        internal double TargetQuality { get; }
        internal double RequiredMeanQualityAdvantage { get; }
        internal double RequiredMeanCoverageAdvantage { get; }
    }

    /// <summary>Single source of truth for all proof landscapes and their predeclared acceptance criteria.</summary>
    private static class LandscapeGenerator
    {
        internal static LandscapeDefinition Get(SearchLandscape landscape) => landscape switch
        {
            SearchLandscape.OneMax => new LandscapeDefinition(
                landscape,
                bits => BitGenome.PopulationCount(bits),
                targetQuality: GenomeWidth,
                requiredMeanQualityAdvantage: 7,
                requiredMeanCoverageAdvantage: 10),
            SearchLandscape.WeightedOneMax => new LandscapeDefinition(
                landscape,
                WeightedOneMax,
                targetQuality: WeightedOneMax(BitGenome.Mask(GenomeWidth)),
                requiredMeanQualityAdvantage: 100,
                requiredMeanCoverageAdvantage: 10),
            SearchLandscape.LeadingOnes => new LandscapeDefinition(
                landscape,
                LeadingOnes,
                targetQuality: GenomeWidth - 2,
                requiredMeanQualityAdvantage: 10,
                requiredMeanCoverageAdvantage: 8),
            _ => throw new ArgumentOutOfRangeException(nameof(landscape))
        };

        private static double WeightedOneMax(ulong bits)
        {
            double quality = 0;
            for (int bit = 0; bit < GenomeWidth; bit++)
                if ((bits & (1UL << bit)) != 0) quality += bit + 1;
            return quality;
        }

        private static double LeadingOnes(ulong bits)
        {
            int count = 0;
            while (count < GenomeWidth && (bits & (1UL << count)) != 0) count++;
            return count;
        }
    }

    private readonly record struct BitGenome(ulong Bits)
    {
        internal static ulong Mask(int width) => width == 64 ? ulong.MaxValue : (1UL << width) - 1UL;

        internal static int PopulationCount(ulong value)
        {
            int count = 0;
            while (value != 0)
            {
                value &= value - 1;
                count++;
            }
            return count;
        }
    }

    private sealed class LandscapeTask : IEvolutionTask<BitGenome>
    {
        internal const string PopulationCountDescriptor = "population-count";
        private readonly LandscapeDefinition _definition;
        private readonly HashSet<ulong> _seen = new();
        private int _evaluations;

        internal LandscapeTask(LandscapeDefinition definition) => _definition = definition;

        public string Id => string.Concat("quality-proof-", ((int)_definition.Kind).ToString(CultureInfo.InvariantCulture));
        public string VersionHash => "quality-proof-task-v1";
        public string EvaluatorVersionHash => "quality-proof-evaluator-v1";
        internal int UniqueEvaluationCount => _seen.Count;
        internal int? EvaluationsToTarget { get; private set; }

        public ValueTask<EvolutionCanonicalGenome<BitGenome>> CanonicalizeAsync(
            BitGenome genome,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong canonical = genome.Bits & BitGenome.Mask(GenomeWidth);
            return new ValueTask<EvolutionCanonicalGenome<BitGenome>>(
                new EvolutionCanonicalGenome<BitGenome>(
                    new BitGenome(canonical),
                    canonical.ToString("X12", CultureInfo.InvariantCulture)));
        }

        public ValueTask<EvolutionTaskResult> EvaluateAsync(
            EvolutionCandidate<BitGenome> candidate,
            EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong bits = candidate.CanonicalGenome.Genome.Bits;
            if (!_seen.Add(bits))
                throw new InvalidOperationException("The engine evaluated the same canonical genome twice.");
            int evaluation = ++_evaluations;
            double quality = _definition.Evaluate(bits);
            if (!EvaluationsToTarget.HasValue && quality >= _definition.TargetQuality)
                EvaluationsToTarget = evaluation;
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
                quality,
                new Dictionary<string, double>
                {
                    [PopulationCountDescriptor] = BitGenome.PopulationCount(bits)
                }));
        }
    }

    private sealed class SingleBitVariation : IVariationOperator<BitGenome>
    {
        private readonly int _width;

        internal SingleBitVariation(int width) => _width = width;

        public string Id => "quality-proof-single-bit";
        public string VersionHash => "quality-proof-single-bit-v1";

        public ValueTask<BitGenome> ProposeAsync(
            EvolutionVariationContext<BitGenome> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong bits = context.Parent.Candidate.CanonicalGenome.Genome.Bits;
            return new ValueTask<BitGenome>(new BitGenome(bits ^ (1UL << context.Random.NextInt(_width))));
        }
    }

    private sealed class BestEliteSelection : ISelectionPolicy<BitGenome>
    {
        public string Id => "quality-proof-best-elite";
        public string VersionHash => "quality-proof-best-elite-v1";

        public EvolutionSelection<BitGenome>? Select(
            IEvolutionArchive<BitGenome> archive,
            StableRandom random,
            int inspirationCount)
        {
            if (archive is null) throw new ArgumentNullException(nameof(archive));
            if (random is null) throw new ArgumentNullException(nameof(random));
            if (inspirationCount < 0) throw new ArgumentOutOfRangeException(nameof(inspirationCount));
            EvolutionArchiveEntry<BitGenome>? best = archive.Best;
            return best is null
                ? null
                : new EvolutionSelection<BitGenome>(best, Array.Empty<EvolutionArchiveEntry<BitGenome>>());
        }
    }

    private readonly record struct SearchQualityObservation(
        double BestQuality,
        int Coverage,
        int EvaluationCount,
        int UniqueEvaluationCount,
        bool TargetReached,
        int EvaluationsToTarget);

    private sealed class SearchQualityAggregate
    {
        private readonly List<SearchQualityObservation> _observations = new();

        internal double MeanBestQuality => _observations.Average(item => item.BestQuality);
        internal double MeanCoverage => _observations.Average(item => item.Coverage);
        internal int TargetHitCount => _observations.Count(item => item.TargetReached);
        internal double MeanEvaluationsToTarget => _observations.Average(item => item.EvaluationsToTarget);

        internal void Add(SearchQualityObservation observation) => _observations.Add(observation);
    }
}
