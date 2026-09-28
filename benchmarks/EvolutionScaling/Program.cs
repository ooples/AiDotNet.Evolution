using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;

// V1-72: per-evaluation engine overhead as a function of archive size, null evaluator, one process per size.
// Usage: EvolutionScaling <elites 16..1000000> <measured evaluations 64..1000000> [workers 1..64]
// The archive is one descriptor with exactly <elites> bins. The operator sweeps the bins in order, so the first <elites>
// evaluations fill it completely; every later proposal lands in an occupied cell and replaces its holder about half the
// time. The cost reported is the steady-state time per evaluation over the <measured> evaluations after the fill.
if (args.Length is < 2 or > 3 || !int.TryParse(args[0], out int elites) || !int.TryParse(args[1], out int measured) ||
    elites is < 16 or > 1_000_000 || measured is < 64 or > 1_000_000)
{
    Console.Error.WriteLine("Usage: EvolutionScaling <elites 16..1000000> <measured evaluations 64..1000000> [workers 1..64]");
    return 2;
}
int workers = args.Length == 3 && int.TryParse(args[2], out int parsed) && parsed is >= 1 and <= 64 ? parsed : 1;
var builder = new EvolutionSearchSpaceBuilder();
builder.Add(EvolutionParameter.Real("x", 0, 1));
builder.Add(EvolutionParameter.Real("y", 0, 1));
EvolutionSearchSpace space = builder.Build();
var task = new EvolutionSearchTask(space, "scaling", "v1", "null-v1", (genome, _, _) =>
    new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(genome.Number("y"),
        new Dictionary<string, double> { ["x"] = genome.Number("x") }, costUnits: 1)));
int total = elites + measured;
var clock = new EvaluationClock(total + 1);
var engine = new EvolutionEngine<EvolutionSearchGenome>(task, new CellSweep(space, elites),
    _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1, elites) }),
    new EvolutionEngineOptions
    {
        RunId = "scaling", Seed = 42, MaxEvaluationAttempts = total, MaxProposals = total * 2, MaxGenerations = total * 2,
        ProposalBatchSize = 8, MaxDegreeOfParallelism = workers, MigrationInterval = 0, CheckpointInterval = 0
    }, observer: clock);
EvolutionSearchGenome seed = space.CreateGenome(new Dictionary<string, EvolutionParameterValue>
{
    ["x"] = EvolutionParameterValue.Numeric(0.5 / elites), ["y"] = EvolutionParameterValue.Numeric(0.5)
});
EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(new[] { seed });
int occupied = result.Islands.Sum(island => island.Entries.Count);
if (clock.Count < total) { Console.Error.WriteLine($"Only {clock.Count} of {total} evaluations were observed."); return 1; }
if (occupied != elites) { Console.Error.WriteLine($"The archive held {occupied} elites, not {elites}."); return 1; }
double seconds = (clock.At(total) - clock.At(elites)) / (double)Stopwatch.Frequency;
Console.WriteLine(JsonSerializer.Serialize(new
{
    System = "aidotnet", Elites = occupied, Measured = measured, Workers = workers,
    MicrosecondsPerEvaluation = seconds * 1e6 / measured,
    FillSeconds = (clock.At(elites) - clock.At(1)) / (double)Stopwatch.Frequency
}));
return 0;

/// <summary>Proposes bin k of the descriptor for proposal k, then keeps sweeping, so the archive fills in exactly
/// <c>elites</c> evaluations. Every random draw comes from the proposal's own stream.</summary>
internal sealed class CellSweep(EvolutionSearchSpace space, int cells) : IVariationOperator<EvolutionSearchGenome>
{
    public string Id => "cell-sweep";
    public string VersionHash => "cell-sweep-v1";

    public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context,
        CancellationToken cancellationToken = default)
    {
        long cell = context.Generation % cells;
        return new ValueTask<EvolutionSearchGenome>(space.CreateGenome(new Dictionary<string, EvolutionParameterValue>
        {
            ["x"] = EvolutionParameterValue.Numeric((cell + 0.5) / cells),
            ["y"] = EvolutionParameterValue.Numeric(context.Random.NextDouble())
        }));
    }
}

/// <summary>Records the timestamp of the n-th completed evaluation; one array write per event.</summary>
internal sealed class EvaluationClock(int capacity) : IEvolutionObserver<EvolutionSearchGenome>
{
    private readonly long[] _ticks = new long[capacity];
    public int Count { get; private set; }
    public long At(int evaluation) => _ticks[evaluation - 1];

    public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        if (evolutionEvent.Kind == EvolutionEventKind.Evaluated && Count < _ticks.Length) _ticks[Count++] = Stopwatch.GetTimestamp();
        return default;
    }
}
