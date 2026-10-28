using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;

// V1-74: checkpoint write and resume cost at realistic archive sizes.
// Usage: EvolutionCheckpoint <elites> <directory> [auto|inline|segmented]
//        EvolutionCheckpoint resume <elites> <directory> [auto|inline|segmented]
// The format defaults to Auto, what a run gets without configuring anything (E-1 measured it at 10,000 elites). The
// resume form only resumes a directory an earlier run left, so a resume can be timed and profiled on its own (E-1). Each
// resume saves a checkpoint of its own, so time it against a fresh copy of the directory every launch.
bool resumeOnly = args.Length > 0 && args[0] == "resume";
string[] arguments = resumeOnly ? args[1..] : args;
EvolutionCheckpointFormat format = EvolutionCheckpointFormat.Auto;
if (arguments.Length is < 2 or > 3 || !int.TryParse(arguments[0], out int elites) || elites is < 16 or > 40_000 ||
    (arguments.Length == 3 && !Enum.TryParse(arguments[2], ignoreCase: true, out format)))
{
    Console.Error.WriteLine("Usage: EvolutionCheckpoint [resume] <elites 16..40000> <directory> [auto|inline|segmented]");
    return 2;
}
string directory = Path.GetFullPath(arguments[1]);
if (Directory.Exists(directory) != resumeOnly)
{
    Console.Error.WriteLine(resumeOnly ? "The directory must hold an earlier run." : "The directory must not exist.");
    return 2;
}
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
EvolutionEngineOptions Options(bool resume) => new()
{
    RunId = "checkpoint-bench", Seed = 7, MaxEvaluationAttempts = elites, MaxProposals = elites * 2, MaxGenerations = elites,
    ProposalBatchSize = 64, MaxDegreeOfParallelism = 1, MigrationInterval = 0, CheckpointInterval = Math.Max(1, elites / 16), Resume = resume,
    CheckpointFormat = format
};
var seeds = new[] { Program4k(0) };
if (resumeOnly)
{
    // Three resumes in one process: the first also pays for JIT compilation of the restore path, the later ones show
    // the steady cost a profile should attribute.
    for (int repeat = 0; repeat < 3; repeat++)
    {
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        var only = Stopwatch.StartNew();
        var restored = await new EvolutionEngine<ProgramGenome>(task, new Distinct4kPrograms(), _ => new MapElitesArchive<ProgramGenome>(descriptors),
            Options(true), checkpointStore: new DirectoryEvolutionCheckpointStore(directory), genomeCodec: new ProgramGenomeCodec()).RunAsync(seeds);
        only.Stop();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Format = format.ToString(), Repeat = repeat, Elites = restored.Islands.Sum(island => island.Count),
            ResumeMs = only.Elapsed.TotalMilliseconds,
            CpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpuBefore).TotalMilliseconds,
            AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore, restored.StateHash
        }));
    }
    return 0;
}
var store = new TimedStore(new DirectoryEvolutionCheckpointStore(directory));
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
// What a resume reads: the newest snapshot of the uninterrupted run (before the resumed run saved again) and the segments
// it names. The segment directory also holds the resumed run's own segments, so only the named ones are counted.
long bytes = store.FinalSnapshotBytes;
long segmentBytes = store.FinalSegmentBytes;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Format = format.ToString(), Elites = archived, Checkpoints = writes.Length,
    WriteP50Ms = writes[writes.Length / 2], WriteP95Ms = writes[(int)Math.Ceiling(writes.Length * 0.95) - 1],
    FinalWriteMs = store.WriteMilliseconds[^1], FinalCheckpointBytes = bytes, FinalSegmentBytes = segmentBytes,
    FinalSegments = store.FinalSegmentCount,
    BytesPerElite = (double)(bytes + segmentBytes) / Math.Max(1, archived), ResumeMs = resume.Elapsed.TotalMilliseconds,
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

// A save's write latency is the store's SaveAsync plus every segment written for it: a segmented save streams its
// changes into a segment before it saves the checkpoint that names the segment, so both are the save's I/O.
internal sealed class TimedStore(DirectoryEvolutionCheckpointStore inner) : IEvolutionCheckpointSegmentStore
{
    private double _segmentMilliseconds;
    private readonly Dictionary<long, long> _segmentBytes = new();
    public List<double> WriteMilliseconds { get; } = new();
    public long FinalSnapshotBytes { get; private set; }
    public long FinalSegmentBytes { get; private set; }
    public int FinalSegmentCount { get; private set; }

    public async Task SaveAsync(EvolutionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        await inner.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        WriteMilliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds + _segmentMilliseconds);
        _segmentMilliseconds = 0;
        FinalSnapshotBytes = new FileInfo(Path.Combine(inner.DirectoryPath,
            DirectoryEvolutionCheckpointStore.FileNameFor(checkpoint.Sequence))).Length;
        FinalSegmentBytes = checkpoint.SegmentIds.Sum(id => _segmentBytes[id]);
        FinalSegmentCount = checkpoint.SegmentIds.Count;
    }

    public Task<EvolutionCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default) =>
        inner.LoadLatestAsync(runId, cancellationToken);

    public async Task WriteSegmentAsync(string runId, long segmentId, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        long started = Stopwatch.GetTimestamp();
        long length = 0;
        await inner.WriteSegmentAsync(runId, segmentId, async (stream, token) =>
        {
            await write(stream, token).ConfigureAwait(false);
            length = stream.Length;
        }, cancellationToken).ConfigureAwait(false);
        _segmentMilliseconds += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _segmentBytes[segmentId] = length;
    }

    public Task<Stream?> OpenSegmentAsync(string runId, long segmentId, CancellationToken cancellationToken = default) =>
        inner.OpenSegmentAsync(runId, segmentId, cancellationToken);
}
