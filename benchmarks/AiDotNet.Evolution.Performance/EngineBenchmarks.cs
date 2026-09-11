using BenchmarkDotNet.Attributes;

namespace AiDotNet.Evolution.Performance;

/// <summary>Whole-run orchestration cost. Its lower-bound control lives in <see cref="EvaluatorBaselineBenchmarks"/>,
/// which declares only the factors an evaluator-only loop actually applies.</summary>
[MemoryDiagnoser]
public class EngineBenchmarks
{
    private const int Budget = 256;
    private EvolutionSearchSpace _space = null!;
    private EvolutionSearchGenome[] _inputs = null!;
    private EvolutionSearchTask _task = null!;
    private EvolutionDescriptorDefinition[] _descriptors = null!;

    [Params(1, 4)] public int Workers { get; set; } = 1;
    [Params(EvolutionDispatchMode.Batch, EvolutionDispatchMode.Continuous)] public EvolutionDispatchMode Dispatch { get; set; } = EvolutionDispatchMode.Batch;
    [Params(0, 1)] public int EvaluatorDelayMilliseconds { get; set; }
    [Params(false, true)] public bool Checkpoint { get; set; }
    [Params(2, 8)] public int Dimensions { get; set; } = 8;
    [Params(100, 1000)] public int ArchiveCells { get; set; } = 100;
    [Params(1, 4)] public int Islands { get; set; } = 1;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new EvolutionSearchSpaceBuilder();
        for (int i = 0; i < Dimensions; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
        _space = builder.Build();
        _inputs = Enumerable.Range(0, Budget).Select(i => _space.Sample(StableRandom.CreateStream(42, (ulong)i))).ToArray();
        _descriptors = new[] { new EvolutionDescriptorDefinition("x", -5, 5, ArchiveCells) };
        _task = new EvolutionSearchTask(_space, "overhead", "v1", "delay-" + EvaluatorDelayMilliseconds,
            (genome, _, token) => EvaluatorBaselineBenchmarks.EvaluateAsync(genome, EvaluatorDelayMilliseconds, token));
    }

    [Benchmark(OperationsPerInvoke = Budget)]
    public async Task<string> RunEngine()
    {
        var engine = new EvolutionEngine<EvolutionSearchGenome>(_task, new SearchSpaceRestart(_space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(_descriptors), new EvolutionEngineOptions
            {
                RunId = "overhead",
                Seed = 42,
                MaxEvaluationAttempts = Budget,
                MaxProposals = Budget * 2,
                ProposalBatchSize = 8,
                MaxDegreeOfParallelism = Workers,
                IslandCount = Islands,
                Dispatch = Dispatch,
                MaxInFlight = 8,
                MigrationInterval = 0,
                CheckpointInterval = Checkpoint ? 16 : 0
            }, checkpointStore: Checkpoint ? new InMemoryEvolutionCheckpointStore(1) : null, genomeCodec: _space);
        EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(_inputs.Take(8));
        if (result.Counters.EvaluationAttempts != Budget || result.Best is null)
            throw new InvalidOperationException("Benchmark did not execute its complete evaluator budget.");
        return result.StateHash;
    }
}
