using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionSearchPresetTests
{
    private static double Offset(double value, long ulps) => BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(value) + ulps);

    private static AdaptiveVariationPortfolio<EvolutionSearchGenome> Portfolio(EvolutionSearchSpace space,
        double explorationProbability, params IVariationOperator<EvolutionSearchGenome>[] operators) => new(operators, explorationProbability);

    [Fact]
    public void DefaultRemainsSimpleAndEveryFactoryCallOwnsItsOperator()
    {
        var space = EvolutionSearchSpaceTests.Mixed();
        Assert.IsType<SearchSpaceMutation>(EvolutionSearchPresets.Create(space));
        var first = EvolutionSearchPresets.Create(space, EvolutionSearchPreset.AdaptiveMixed);
        var second = EvolutionSearchPresets.Create(space, EvolutionSearchPreset.AdaptiveMixed);
        Assert.NotSame(first, second); Assert.Equal(first.VersionHash, second.VersionHash);
        Assert.NotEqual(first.VersionHash, EvolutionSearchPresets.Create(space, EvolutionSearchPreset.UniformMixed).VersionHash);
    }

    // Pins the catalog itself, not just its types: the operator list and its order, each preset's exploration
    // probability and the mutation defaults. Version hashes cover all three, so a swapped exploration value, a
    // dropped operator, a reordered catalog or a retuned mutation changes the identity these assertions compare.
    [Fact]
    public void PresetCatalogPinsOperatorOrderExplorationProbabilityAndMutationDefaults()
    {
        var space = EvolutionSearchSpaceTests.Mixed();
        var mutation = EvolutionSearchPresets.Create(space);
        Assert.Equal("typed-mutation-0.2-0.1", mutation.Id);
        Assert.Equal(new SearchSpaceMutation(space, 0.2, 0.1).VersionHash, mutation.VersionHash);
        Assert.NotEqual(new SearchSpaceMutation(space, 1, 1).VersionHash, mutation.VersionHash);
        Assert.NotEqual(new SearchSpaceMutation(space, 0.2, 0.2).VersionHash, mutation.VersionHash);

        foreach ((EvolutionSearchPreset preset, double exploration, double other) in new[]
        {
            (EvolutionSearchPreset.UniformMixed, 1d, 0.1),
            (EvolutionSearchPreset.AdaptiveMixed, 0.1, 1d)
        })
        {
            var portfolio = Assert.IsType<AdaptiveVariationPortfolio<EvolutionSearchGenome>>(EvolutionSearchPresets.Create(space, preset));
            Assert.Equal(new[] { "typed-mutation-0.2-0.1", "typed-uniform-crossover", "typed-restart" },
                portfolio.Statistics.Select(statistics => statistics.OperatorId).ToArray());
            Assert.Equal(Portfolio(space, exploration, new SearchSpaceMutation(space), new SearchSpaceCrossover(space), new SearchSpaceRestart(space)).VersionHash,
                portfolio.VersionHash);
            // Swapped exploration probabilities between the two mixed presets.
            Assert.NotEqual(Portfolio(space, other, new SearchSpaceMutation(space), new SearchSpaceCrossover(space), new SearchSpaceRestart(space)).VersionHash,
                portfolio.VersionHash);
            // Restart dropped from the catalog.
            Assert.NotEqual(Portfolio(space, exploration, new SearchSpaceMutation(space), new SearchSpaceCrossover(space)).VersionHash,
                portfolio.VersionHash);
            // Catalog reordered.
            Assert.NotEqual(Portfolio(space, exploration, new SearchSpaceCrossover(space), new SearchSpaceMutation(space), new SearchSpaceRestart(space)).VersionHash,
                portfolio.VersionHash);
            // Mutation built with non-default probability and scale.
            Assert.NotEqual(Portfolio(space, exploration, new SearchSpaceMutation(space, 1, 1), new SearchSpaceCrossover(space), new SearchSpaceRestart(space)).VersionHash,
                portfolio.VersionHash);
        }
    }

    // Behavioral counterpart: the declared exploration probabilities must actually govern operator selection,
    // so that swapping 1 and 0.1 between the two mixed presets changes observable allocation, not only a hash.
    [Fact]
    public async Task MixedPresetExplorationProbabilitiesGovernObservedOperatorShares()
    {
        var space = EvolutionSearchSpaceTests.Mixed();
        var parent = space.Sample(StableRandom.CreateStream(7, 0)); var donor = space.Sample(StableRandom.CreateStream(8, 0));
        var uniform = Assert.IsType<AdaptiveVariationPortfolio<EvolutionSearchGenome>>(EvolutionSearchPresets.Create(space, EvolutionSearchPreset.UniformMixed));
        var adaptive = Assert.IsType<AdaptiveVariationPortfolio<EvolutionSearchGenome>>(EvolutionSearchPresets.Create(space, EvolutionSearchPreset.AdaptiveMixed));
        const int proposals = 300;
        for (ulong i = 1; i <= proposals; i++)
        {
            await uniform.ProposeAsync(EvolutionSearchSpaceTests.Context(parent, donor, i, (long)i));
            await adaptive.ProposeAsync(EvolutionSearchSpaceTests.Context(parent, donor, i, (long)i));
        }
        // Uniform allocation after the initial trial of each operator: no operator may dominate.
        Assert.All(uniform.Statistics, statistics => Assert.InRange(statistics.Proposals, proposals * 0.25, proposals * 0.45));
        // Epsilon-greedy at 0.1: without rewards the portfolio exploits its first arm almost always.
        Assert.True(adaptive.Statistics[0].Proposals > proposals * 0.8,
            "adaptive exploitation share was " + adaptive.Statistics[0].Proposals.ToString());
        Assert.Equal(proposals, uniform.Statistics.Sum(statistics => statistics.Proposals));
        Assert.Equal(proposals, adaptive.Statistics.Sum(statistics => statistics.Proposals));
    }

    [Theory]
    [InlineData(EvolutionSearchPreset.Mutation)]
    [InlineData(EvolutionSearchPreset.UniformMixed)]
    [InlineData(EvolutionSearchPreset.AdaptiveMixed)]
    public async Task MixedPresetsProduceValidOwnedDeterministicCandidates(EvolutionSearchPreset preset)
    {
        var space = EvolutionSearchSpaceTests.Mixed();
        var parent = space.Sample(StableRandom.CreateStream(7, 0)); var donor = space.Sample(StableRandom.CreateStream(8, 0));
        string original = space.Serialize(parent);
        var first = EvolutionSearchPresets.Create(space, preset); var second = EvolutionSearchPresets.Create(space, preset);
        for (ulong i = 0; i < 64; i++)
        {
            var left = await first.ProposeAsync(EvolutionSearchSpaceTests.Context(parent, donor, i, (long)i + 1));
            var right = await second.ProposeAsync(EvolutionSearchSpaceTests.Context(parent, donor, i, (long)i + 1));
            Assert.Equal(left.Identity, right.Identity); Assert.Equal(left.Identity, space.Validate(left).Identity);
            Assert.NotSame(parent, left); Assert.NotSame(parent.Values, left.Values);
        }
        Assert.Equal(original, space.Serialize(parent));
    }

    [Fact]
    public void UnsupportedChoicesFailBeforeWorkAndSchemaDirectionChangeCmaIdentity()
    {
        var space = EvolutionSearchSpaceTests.Continuous(2);
        Assert.Throws<ArgumentNullException>(() => EvolutionSearchPresets.Create(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionSearchPresets.Create(space, (EvolutionSearchPreset)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionSearchPresets.Create(space, direction: (EvolutionOptimizationDirection)99));
        Assert.Throws<ArgumentException>(() => EvolutionSearchPresets.Create(EvolutionSearchSpaceTests.Mixed(), EvolutionSearchPreset.DiagonalCma));
        var maximize = EvolutionSearchPresets.Create(space, EvolutionSearchPreset.DiagonalCma);
        var minimize = EvolutionSearchPresets.Create(space, EvolutionSearchPreset.DiagonalCma, EvolutionOptimizationDirection.Minimize);
        Assert.NotEqual(maximize.VersionHash, minimize.VersionHash);
        Assert.NotEqual(maximize.VersionHash, EvolutionSearchPresets.Create(EvolutionSearchSpaceTests.Continuous(3), EvolutionSearchPreset.DiagonalCma).VersionHash);
        Assert.IsType<DiagonalCmaEmitter>(maximize);
    }

    [Theory]
    [InlineData(EvolutionSearchPreset.Mutation)]
    [InlineData(EvolutionSearchPreset.UniformMixed)]
    [InlineData(EvolutionSearchPreset.AdaptiveMixed)]
    [InlineData(EvolutionSearchPreset.DiagonalCma)]
    public async Task PresetStateResumesWithExactWorkerIndependentTrajectory(EvolutionSearchPreset preset)
    {
        // The space carries an ordinary logarithmic domain and one whose bounds are 130 ulps apart, which is
        // just past the point where the superseded rule collapsed sampling to two values.
        double narrowMinimum = 1e100;
        var rate = EvolutionParameter.Logarithmic("rate", 1e-3, 10);
        var narrow = EvolutionParameter.Logarithmic("narrow", narrowMinimum, Offset(narrowMinimum, 130));
        var space = new EvolutionSearchSpaceBuilder()
            .Add(EvolutionParameter.Real("x0", -5, 5)).Add(EvolutionParameter.Real("x1", -5, 5))
            .Add(rate).Add(narrow).Build();
        var seed = space.Sample(StableRandom.CreateStream(7, 0));
        var store = new InMemoryEvolutionCheckpointStore();
        var narrowValues = new HashSet<double>();
        async Task<EvolutionRunResult<EvolutionSearchGenome>> Run(int budget, int workers, bool resume, IEvolutionCheckpointStore checkpoints)
        {
            var task = new EvolutionSearchTask(space, "preset-contract", "v1", "v1", (genome, _, _) =>
            {
                narrowValues.Add(genome.Number("narrow"));
                return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
                    -(genome.Number("x0") * genome.Number("x0") + genome.Number("x1") * genome.Number("x1"))
                    - Math.Abs(rate.Normalize(genome.Values["rate"]) - 0.7)
                    - Math.Abs(narrow.Normalize(genome.Values["narrow"]) - 0.2),
                    new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1));
            });
            var engine = new EvolutionEngine<EvolutionSearchGenome>(task, EvolutionSearchPresets.Create(space, preset),
                _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 10) }),
                new EvolutionEngineOptions
                {
                    RunId = "preset-contract",
                    Seed = 9,
                    MaxEvaluationAttempts = budget,
                    MaxProposals = budget * 20,
                    MaxGenerations = budget * 20,
                    ProposalBatchSize = 1,
                    MaxDegreeOfParallelism = workers,
                    Resume = resume,
                    InspirationCount = 2,
                    MigrationInterval = 0
                }, checkpointStore: checkpoints, genomeCodec: space);
            return await engine.RunAsync(new[] { seed });
        }
        await Run(16, 1, false, store);
        var resumed = await Run(32, 4, true, store);
        narrowValues.Clear();
        var uninterrupted = await Run(32, 1, false, new InMemoryEvolutionCheckpointStore());
        Assert.Equal(32, resumed.Counters.EvaluationAttempts);
        Assert.Empty(resumed.RetainedFailures);
        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
        Assert.Equal(uninterrupted.Best!.Evaluation.Quality, resumed.Best!.Evaluation.Quality);
        // The near-collapsed domain must remain searchable and in range across checkpoint and restore.
        Assert.True(narrowValues.Count > 2, "the near-collapsed domain took " + narrowValues.Count.ToString() + " values");
        Assert.All(narrowValues, value => Assert.InRange(value, narrowMinimum, Offset(narrowMinimum, 130)));
    }
}
