using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionSearchPresetTests
{
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
        var space = EvolutionSearchSpaceTests.Continuous(2);
        var seed = space.Sample(StableRandom.CreateStream(7, 0));
        var store = new InMemoryEvolutionCheckpointStore();
        async Task<EvolutionRunResult<EvolutionSearchGenome>> Run(int budget, int workers, bool resume, IEvolutionCheckpointStore checkpoints)
        {
            var task = new EvolutionSearchTask(space, "preset-contract", "v1", "v1", (genome, _, _) => new ValueTask<EvolutionTaskResult>(
                EvolutionTaskResult.Completed(-genome.Values.Values.Sum(value => value.Number * value.Number),
                    new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1)));
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
        var uninterrupted = await Run(32, 1, false, new InMemoryEvolutionCheckpointStore());
        Assert.Equal(32, resumed.Counters.EvaluationAttempts);
        Assert.Empty(resumed.RetainedFailures);
        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
        Assert.Equal(uninterrupted.Best!.Evaluation.Quality, resumed.Best!.Evaluation.Quality);
    }
}
