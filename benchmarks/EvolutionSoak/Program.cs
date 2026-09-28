using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;

// V1-73 (#178): long-run soak. One executable, three modes, each printing one JSON line.
//   memory <evaluations> <checkpointEvery> <storeDir>   sample working set, GC heap, handles, threads and child
//                                                        processes every 5% of the run
//   run <evaluations> <checkpointEvery> <storeDir> <resume 0|1>
//                                                        one run that prints "EVAL <n>" every 100 evaluations; used by
//                                                        the kill/resume driver
//   resume <evaluations> <checkpointEvery> <workDir> <killPoints>
//                                                        uninterrupted run, then one killed-and-resumed run per kill point
if (args.Length < 1) return Usage();
return args[0] switch
{
    "memory" when args.Length == 4 => await Memory(int.Parse(args[1]), int.Parse(args[2]), args[3]),
    "run" when args.Length == 5 => await Run(int.Parse(args[1]), int.Parse(args[2]), args[3], args[4] == "1", progress: true),
    "resume" when args.Length == 5 => await Resume(int.Parse(args[1]), int.Parse(args[2]), args[3], int.Parse(args[4])),
    _ => Usage()
};

static int Usage()
{
    Console.Error.WriteLine("Usage: EvolutionSoak memory|run|resume ... (see Program.cs)");
    return 2;
}

static EvolutionEngine<EvolutionSearchGenome> Engine(int evaluations, int checkpointEvery, string storeDir, bool resume,
    IEvolutionObserver<EvolutionSearchGenome>? observer)
{
    var builder = new EvolutionSearchSpaceBuilder();
    for (int i = 0; i < 4; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
    EvolutionSearchSpace space = builder.Build();
    var task = new EvolutionSearchTask(space, "soak", "v1", "null-v1", (genome, _, _) =>
        new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-genome.Values.Values.Sum(v => v.Number * v.Number),
            new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1)));
    return new EvolutionEngine<EvolutionSearchGenome>(task, new SearchSpaceRestart(space),
        _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 64) }),
        new EvolutionEngineOptions
        {
            RunId = "soak", Seed = 42, MaxEvaluationAttempts = evaluations, MaxProposals = evaluations * 2,
            MaxGenerations = evaluations, ProposalBatchSize = 8, MaxDegreeOfParallelism = 4, MigrationInterval = 0,
            CheckpointInterval = checkpointEvery, Resume = resume,
            DeduplicationCapacity = int.TryParse(Environment.GetEnvironmentVariable("SOAK_DEDUP_CAPACITY"), out int capacity) ? capacity : 0
        }, observer: observer, checkpointStore: new DirectoryEvolutionCheckpointStore(storeDir, maxCheckpointBytes: 256L * 1024 * 1024),
        genomeCodec: space);
}

static EvolutionSearchGenome[] Seeds()
{
    var builder = new EvolutionSearchSpaceBuilder();
    for (int i = 0; i < 4; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
    EvolutionSearchSpace space = builder.Build();
    return Enumerable.Range(0, 8).Select(i => space.Sample(StableRandom.CreateStream(42, (ulong)i))).ToArray();
}

static async Task<int> Run(int evaluations, int checkpointEvery, string storeDir, bool resume, bool progress)
{
    var counter = new Counter(progress ? 100 : 0, null);
    EvolutionRunResult<EvolutionSearchGenome> result = await Engine(evaluations, checkpointEvery, storeDir, resume, counter).RunAsync(Seeds());
    Console.WriteLine(JsonSerializer.Serialize(new { Mode = "run", result.StateHash, result.Counters.CompletedEvaluations, Resumed = resume }));
    return 0;
}

static async Task<int> Memory(int evaluations, int checkpointEvery, string storeDir)
{
    var samples = new List<object>();
    int step = Math.Max(1, evaluations / 20);
    var counter = new Counter(0, n =>
    {
        if (n % step != 0) return;
        using Process self = Process.GetCurrentProcess();
        self.Refresh();
        samples.Add(new
        {
            Evaluations = n, WorkingSetBytes = self.WorkingSet64, PrivateBytes = self.PrivateMemorySize64,
            GcHeapBytes = GC.GetTotalMemory(false), self.HandleCount, Threads = self.Threads.Count, ChildProcesses = ChildCount(self.Id)
        });
    });
    var clock = Stopwatch.StartNew();
    EvolutionRunResult<EvolutionSearchGenome> result = await Engine(evaluations, checkpointEvery, storeDir, false, counter).RunAsync(Seeds());
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Mode = "memory", result.Counters.CompletedEvaluations, CheckpointEvery = checkpointEvery, Seconds = clock.Elapsed.TotalSeconds,
        result.StateHash, Samples = samples
    }));
    return 0;
}

static async Task<int> Resume(int evaluations, int checkpointEvery, string workDir, int killPoints)
{
    string self = Environment.ProcessPath ?? "dotnet";
    string dll = typeof(Counter).Assembly.Location;
    Directory.CreateDirectory(workDir);
    string reference = Path.Combine(workDir, "uninterrupted");
    string expected = Child(self, dll, evaluations, checkpointEvery, reference, resume: false, killAt: null).Hash;
    var random = new StableRandom(73);
    var rows = new List<object>();
    for (int k = 0; k < killPoints; k++)
    {
        int killAt = 1000 + random.NextInt(evaluations - 2000);
        string store = Path.Combine(workDir, "kill-" + k);
        (_, int killedAfter) = Child(self, dll, evaluations, checkpointEvery, store, resume: false, killAt: killAt);
        string resumed = Child(self, dll, evaluations, checkpointEvery, store, resume: true, killAt: null).Hash;
        rows.Add(new { KillTarget = killAt, KilledAfter = killedAfter, Resumed = resumed, Equal = resumed == expected });
    }
    Console.WriteLine(JsonSerializer.Serialize(new { Mode = "resume", Evaluations = evaluations, CheckpointEvery = checkpointEvery, Expected = expected, Kills = rows }));
    return 0;
}

// Runs one child; with killAt it kills the whole process tree once the child reports that many evaluations.
static (string Hash, int Seen) Child(string host, string dll, int evaluations, int checkpointEvery, string store, bool resume, int? killAt)
{
    bool viaDotnet = !string.Equals(Path.GetFileNameWithoutExtension(host), Path.GetFileNameWithoutExtension(dll), StringComparison.OrdinalIgnoreCase);
    var start = new ProcessStartInfo(viaDotnet ? host : dll) { RedirectStandardOutput = true, UseShellExecute = false };
    if (viaDotnet) start.ArgumentList.Add(dll);
    foreach (string a in new[] { "run", evaluations.ToString(), checkpointEvery.ToString(), store, resume ? "1" : "0" }) start.ArgumentList.Add(a);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
    int seen = 0;
    string hash = string.Empty;
    string? line;
    while ((line = process.StandardOutput.ReadLine()) is not null)
    {
        if (line.StartsWith("EVAL ", StringComparison.Ordinal))
        {
            seen = int.Parse(line.AsSpan(5));
            if (killAt is { } target && seen >= target)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return (string.Empty, seen);
            }
        }
        else if (line.StartsWith('{')) hash = JsonDocument.Parse(line).RootElement.GetProperty("StateHash").GetString() ?? string.Empty;
    }
    process.WaitForExit();
    if (process.ExitCode != 0 || hash.Length == 0) throw new InvalidOperationException($"Child failed with exit code {process.ExitCode}.");
    return (hash, seen);
}

static int ChildCount(int pid)
{
    // Direct children by parent id; the soak evaluator starts none, so any here would be a leak.
    int count = 0;
    foreach (Process process in Process.GetProcesses())
    {
        try { if (ParentOf(process) == pid) count++; }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        finally { process.Dispose(); }
    }
    return count;
}

static int ParentOf(Process process)
{
    if (!OperatingSystem.IsWindows()) return -1;
    var info = new ProcessBasicInformation();
    return NtQueryInformationProcess(process.Handle, 0, ref info, System.Runtime.InteropServices.Marshal.SizeOf(info), out _) == 0
        ? (int)info.InheritedFromUniqueProcessId : -1;
}

[System.Runtime.InteropServices.DllImport("ntdll.dll")]
static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct ProcessBasicInformation
{
    public IntPtr Reserved1;
    public IntPtr PebBaseAddress;
    public IntPtr Reserved2a;
    public IntPtr Reserved2b;
    public IntPtr UniqueProcessId;
    public IntPtr InheritedFromUniqueProcessId;
}

/// <summary>Counts completed evaluations; optionally prints progress lines and calls a sampler on every one.</summary>
internal sealed class Counter(int printEvery, Action<int>? sample) : IEvolutionObserver<EvolutionSearchGenome>
{
    private int _count;

    public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> evolutionEvent, CancellationToken cancellationToken = default)
    {
        if (evolutionEvent.Kind != EvolutionEventKind.Evaluated) return default;
        int n = ++_count;
        if (printEvery > 0 && n % printEvery == 0) { Console.WriteLine("EVAL " + n); Console.Out.Flush(); }
        sample?.Invoke(n);
        return default;
    }
}
