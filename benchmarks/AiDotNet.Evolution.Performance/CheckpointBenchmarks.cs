using BenchmarkDotNet.Attributes;

namespace AiDotNet.Evolution.Performance;

/// <summary>Checkpoint checksum, load and restore scaling with real engine-generated state.</summary>
[MemoryDiagnoser]
public class CheckpointBenchmarks
{
    private EvolutionSearchSpace _space = null!;
    private EvolutionSearchTask _task = null!;
    private EvolutionSearchGenome _seed = null!;
    private EvolutionCheckpoint _checkpoint = null!;
    private InMemoryEvolutionCheckpointStore _store = null!;
    private string _stateHash = null!;
    [Params(32, 256, 2048)] public int Evaluations { get; set; }
    [Params(1, 4)] public int Islands { get; set; } = 1;
    [Params(1, 8)] public int Dimensions { get; set; } = 1;

    [GlobalSetup]
    public async Task Setup()
    {
        var builder = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, 1));
        for (int dimension = 1; dimension < Dimensions; dimension++) builder.Add(EvolutionParameter.Real("d" + dimension, 0, 1));
        _space = builder.Build();
        _task = new EvolutionSearchTask(_space, "checkpoint", "v1", "v1", (genome, _, _) => new ValueTask<EvolutionTaskResult>(
            EvolutionTaskResult.Completed(genome.Number("x"), new Dictionary<string, double> { ["x"] = genome.Number("x") }, costUnits: 1)));
        _seed = _space.Sample(StableRandom.CreateStream(1, 0)); _store = new InMemoryEvolutionCheckpointStore(1);
        EvolutionRunResult<EvolutionSearchGenome> result = await Engine(_store, false).RunAsync(new[] { _seed });
        _stateHash = result.StateHash;
        _checkpoint = await _store.LoadLatestAsync("checkpoint-perf") ?? throw new InvalidOperationException("Missing fixture checkpoint.");
        if (result.Counters.EvaluationAttempts != Evaluations)
            throw new InvalidOperationException($"Checkpoint fixture expected {Evaluations} evaluations, got {result.Counters.EvaluationAttempts}: {result.StopReason}.");
    }

    private EvolutionEngine<EvolutionSearchGenome> Engine(IEvolutionCheckpointStore store, bool resume) => new(
        _task, new SearchSpaceRestart(_space), _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, 1000) }),
        new EvolutionEngineOptions
        {
            RunId = "checkpoint-perf",
            Seed = 1,
            MaxEvaluationAttempts = Evaluations,
            MaxProposals = Evaluations * 2,
            MaxGenerations = Evaluations * 2,
            ProposalBatchSize = 8,
            IslandCount = Islands,
            MigrationInterval = 0,
            Resume = resume
        }, checkpointStore: store, genomeCodec: _space);

    [Benchmark]
    public EvolutionCheckpoint ChecksumEnvelope() => new(_checkpoint.RunId, _checkpoint.Sequence, _checkpoint.CompatibilityHash,
        _checkpoint.Payload, _checkpoint.SchemaVersion, _checkpoint.Quality, _checkpoint.QualityDirection);

    [Benchmark]
    public Task<EvolutionCheckpoint?> LoadClone() => _store.LoadLatestAsync("checkpoint-perf");

    [Benchmark]
    public async Task<string> RestoreWithoutNewEvaluations()
    {
        // A fresh store prevents previous benchmark invocations from changing the fixture.
        var store = new InMemoryEvolutionCheckpointStore(1); await store.SaveAsync(_checkpoint);
        EvolutionRunResult<EvolutionSearchGenome> result = await Engine(store, true).RunAsync(new[] { _seed });
        if (result.StateHash != _stateHash || result.Counters.EvaluationAttempts != Evaluations)
            throw new InvalidOperationException("Checkpoint restore changed semantic state.");
        return result.StateHash;
    }
}
