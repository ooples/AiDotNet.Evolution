using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;

// V1-74: checkpoint write and resume cost at realistic archive sizes. Usage: EvolutionCheckpoint <elites> <directory>
if (args.Length != 2 || !int.TryParse(args[0], out int elites) || elites is < 16 or > 40_000)
{
    Console.Error.WriteLine("Usage: EvolutionCheckpoint <elites 16..40000> <new-directory>");
    return 2;
}
string directory = Path.GetFullPath(args[1]);
if (Directory.Exists(directory)) { Console.Error.WriteLine("The directory must not exist."); return 2; }
int side = (int)Math.Ceiling(Math.Sqrt(elites));
var descriptors = new[] { new EvolutionDescriptorDefinition("a", 0, side, side), new EvolutionDescriptorDefinition("b", 0, side, side) };

// A 4 KB program per candidate; the evaluator places candidate k in cell (k mod side, k div side), so every
// evaluation lands in a new cell and the archive holds exactly `elites` programs at the end.
var task = new ProgramEvolutionTask(new DelegateProgramFitnessEvaluator((genome, context, _) =>
{
    long k = context.EvaluationId;
    return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(k,
        new Dictionary<string, double> { ["a"] = k % side + 0.5, ["b"] = k / side + 0.5 }, costUnits: 1));
}, "checkpoint-bench", "checkpoint-bench-v1"));
var store = new TimedStore(new DirectoryEvolutionCheckpointStore(directory));
EvolutionEngineOptions Options(bool resume) => new()
{
    RunId = "checkpoint-bench", Seed = 7, MaxEvaluationAttempts = elites, MaxProposals = elites * 2, MaxGenerations = elites,
    ProposalBatchSize = 64, MaxDegreeOfParallelism = 1, MigrationInterval = 0, CheckpointInterval = Math.Max(1, elites / 16), Resume = resume
};
var seeds = new[] { Program4k(0) };
var run = await new EvolutionEngine<ProgramGenome>(task, new Distinct4kPrograms(), _ => new MapElitesArchive<ProgramGenome>(descriptors),
    Options(false), checkpointStore: store, genomeCodec: new ProgramGenomeCodec()).RunAsync(seeds);
int archived = run.Islands.Sum(island => island.Count);

// Resume: a fresh engine restores the final checkpoint; with its budget already spent it stops at once, so the
// elapsed time is the restore itself (reading, validating and rebuilding the archive).
var resume = Stopwatch.StartNew();
var resumed = await new EvolutionEngine<ProgramGenome>(task, new Distinct4kPrograms(), _ => new MapElitesArchive<ProgramGenome>(descriptors),
    Options(true), checkpointStore: new DirectoryEvolutionCheckpointStore(directory), genomeCodec: new ProgramGenomeCodec()).RunAsync(seeds);
resume.Stop();

double[] writes = store.WriteMilliseconds.OrderBy(value => value).ToArray();
long bytes = new DirectoryInfo(directory).EnumerateFiles("checkpoint-*.json").OrderBy(file => file.Name).Last().Length;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Elites = archived, Checkpoints = writes.Length,
    WriteP50Ms = writes[writes.Length / 2], WriteP95Ms = writes[(int)Math.Ceiling(writes.Length * 0.95) - 1],
    FinalWriteMs = store.WriteMilliseconds[^1], FinalCheckpointBytes = bytes,
    BytesPerElite = (double)bytes / Math.Max(1, archived), ResumeMs = resume.Elapsed.TotalMilliseconds,
    ResumedStateMatches = resumed.StateHash == run.StateHash
}));
return resumed.StateHash == run.StateHash ? 0 : 1;

static ProgramGenome Program4k(long k) =>
    new("# candidate " + k + "\n" + string.Concat(Enumerable.Range(0, 64).Select(i => "value_" + i + " = " + (k * 64 + i) + "  # padding to about 4 KB\n")),
        ProgramLanguage.Python);

internal sealed class Distinct4kPrograms : IVariationOperator<ProgramGenome>
{
    public string Id => "distinct-4k";
    public string VersionHash => "distinct-4k-v1";
    public ValueTask<ProgramGenome> ProposeAsync(EvolutionVariationContext<ProgramGenome> context, CancellationToken cancellationToken = default) =>
        new(new ProgramGenome("# candidate " + context.Generation + "\n" + string.Concat(Enumerable.Range(0, 64)
            .Select(i => "value_" + i + " = " + (context.Generation * 64 + i) + "  # padding to about 4 KB\n")), ProgramLanguage.Python));
}

internal sealed class TimedStore(IEvolutionCheckpointStore inner) : IEvolutionCheckpointStore
{
    public List<double> WriteMilliseconds { get; } = new();
    public async Task SaveAsync(EvolutionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        await inner.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        WriteMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
    public Task<EvolutionCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default) =>
        inner.LoadLatestAsync(runId, cancellationToken);
}
