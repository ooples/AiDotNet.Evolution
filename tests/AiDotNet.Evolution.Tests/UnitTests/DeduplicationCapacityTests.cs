using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// Covers <see cref="EvolutionEngineOptions.DeduplicationCapacity"/> (V1-73): the seen set and the evaluation cache grow
/// with every distinct evaluation, so a long run can bound them, forgetting the oldest committed genome first.
/// </summary>
public sealed class DeduplicationCapacityTests
{
    // Proposes genomes 1..cycle over and over, so every proposal after the first cycle repeats an earlier genome.
    private sealed class CyclingVariation(int cycle) : IVariationOperator<TestGenome>
    {
        public string Id => "cycling";
        public string VersionHash => "cycling-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome((int)(context.Generation % cycle) + 1));
    }

    private static EvolutionEngineOptions Options(int capacity, int budget, int checkpointInterval = 0) => new()
    {
        RunId = "dedup-capacity",
        Seed = 7,
        MaxEvaluationAttempts = budget,
        MaxProposals = 400,
        MaxGenerations = 400,
        ProposalBatchSize = 4,
        MaxDegreeOfParallelism = 1,
        MigrationInterval = 0,
        CheckpointInterval = checkpointInterval,
        DeduplicationCapacity = capacity
    };

    private static EvolutionEngine<TestGenome> Engine(SyntheticEvolutionTask task, EvolutionEngineOptions options,
        IEvolutionCheckpointStore? store = null) => new(task, new CyclingVariation(12),
        // Two cells, so at most two of the twelve genomes are elites at a time; an elite is never forgotten, so a wider
        // archive would hold every genome and nothing could be.
        _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 2) }), options,
        checkpointStore: store, genomeCodec: store is null ? null : new TestGenomeCodec());

    [Fact]
    public async Task WithoutACapacityEveryRepeatIsADuplicateAndWithOneAForgottenGenomeIsEvaluatedAgain()
    {
        var unbounded = new SyntheticEvolutionTask();
        EvolutionRunResult<TestGenome> remembered = await Engine(unbounded, Options(0, 60)).RunAsync(new[] { new TestGenome(0) });
        var bounded = new SyntheticEvolutionTask();
        EvolutionRunResult<TestGenome> forgetting = await Engine(bounded, Options(4, 60)).RunAsync(new[] { new TestGenome(0) });

        // The seed plus the 12 distinct genomes, and nothing else, reach the evaluator when everything is remembered.
        Assert.Equal(13, unbounded.Calls);
        // Remembering only four, a genome comes back round long after it was forgotten and is evaluated again.
        Assert.True(bounded.Calls > 40, $"only {bounded.Calls} evaluations ran");
        Assert.NotEqual(remembered.StateHash, forgetting.StateHash);
    }

    [Fact]
    public async Task ACapacityIsPartOfTheRunIdentityOnlyWhenSet()
    {
        string unset = Engine(new SyntheticEvolutionTask(), Options(0, 60)).CompatibilityHash;
        string four = Engine(new SyntheticEvolutionTask(), Options(4, 60)).CompatibilityHash;
        Assert.NotEqual(unset, four);
        Assert.NotEqual(four, Engine(new SyntheticEvolutionTask(), Options(5, 60)).CompatibilityHash);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(16)]
    [InlineData(28)]
    public async Task AResumedRunForgetsInTheSameOrderAsAnUninterruptedOne(int stopAfter)
    {
        EvolutionRunResult<TestGenome> uninterrupted = await Engine(new SyntheticEvolutionTask(), Options(4, 80, 4),
            new InMemoryEvolutionCheckpointStore()).RunAsync(new[] { new TestGenome(0) });

        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(new SyntheticEvolutionTask(), Options(4, stopAfter, 4), store).RunAsync(new[] { new TestGenome(0) });
        EvolutionEngineOptions resumed = Options(4, 80, 4);
        resumed.Resume = true;
        EvolutionRunResult<TestGenome> continued = await Engine(new SyntheticEvolutionTask(), resumed, store)
            .RunAsync(new[] { new TestGenome(0) });

        Assert.Equal(uninterrupted.StateHash, continued.StateHash);
    }

    [Fact]
    public async Task ACheckpointFromACappedRunCannotBeResumedWithoutTheCap()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(new SyntheticEvolutionTask(), Options(4, 20, 4), store).RunAsync(new[] { new TestGenome(0) });
        EvolutionEngineOptions uncapped = Options(0, 40, 4);
        uncapped.Resume = true;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Engine(new SyntheticEvolutionTask(), uncapped, store).RunAsync(new[] { new TestGenome(0) }));
    }

    [Fact]
    public void ANegativeCapacityIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine(new SyntheticEvolutionTask(), Options(-1, 10)));
    }
}
