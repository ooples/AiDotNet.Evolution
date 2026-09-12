using BenchmarkDotNet.Attributes;

namespace AiDotNet.Evolution.Performance;

/// <summary>The evaluator-only lower bound, with only the factors it applies: worker count, evaluator delay and genome size.
/// Dispatch, checkpoints, archive capacity and islands are engine factors and would produce identical repeated cases here.</summary>
[MemoryDiagnoser]
public class EvaluatorBaselineBenchmarks
{
    private const int Budget = 256;
    private EvolutionSearchSpace _space = null!;
    private EvolutionSearchGenome[] _inputs = null!;

    [Params(1, 4)] public int Workers { get; set; } = 1;
    [Params(0, 1)] public int EvaluatorDelayMilliseconds { get; set; }
    [Params(2, 8)] public int Dimensions { get; set; } = 8;

    [GlobalSetup]
    public void Setup()
    {
        var builder = new EvolutionSearchSpaceBuilder();
        for (int i = 0; i < Dimensions; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
        _space = builder.Build();
        _inputs = Enumerable.Range(0, Budget).Select(i => _space.Sample(StableRandom.CreateStream(42, (ulong)i))).ToArray();
    }

    /// <summary>Evaluates precomputed genomes with parallel scheduling; a lower bound, not an evolutionary algorithm.</summary>
    [Benchmark(OperationsPerInvoke = Budget)]
    public async Task EvaluationOnly()
    {
        await Parallel.ForEachAsync(_inputs, new ParallelOptions { MaxDegreeOfParallelism = Workers }, async (genome, token) =>
        {
            EvolutionTaskResult result = await EvaluateAsync(genome, EvaluatorDelayMilliseconds, token);
            if (result.Status != EvolutionEvaluationStatus.Completed) throw new InvalidOperationException("Evaluator baseline failed.");
        });
    }

    /// <summary>The shared authored sphere evaluator used by both the engine and this control.</summary>
    internal static async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionSearchGenome genome, int delayMilliseconds, CancellationToken token)
    {
        if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);
        double loss = genome.Values.Values.Sum(value => value.Number * value.Number);
        return EvolutionTaskResult.Completed(-loss, new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1);
    }
}
