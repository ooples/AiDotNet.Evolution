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
//   sandbox <executions> <python> <concurrency>          out-of-process sandbox executions whose programs start a detached
//                                                        grandchild and time out 1% of the time; counts survivors after
if (args.Length < 1) return Usage();
return args[0] switch
{
    "memory" when args.Length == 4 => await Memory(int.Parse(args[1]), int.Parse(args[2]), args[3]),
    "run" when args.Length == 5 => await Run(int.Parse(args[1]), int.Parse(args[2]), args[3], args[4] == "1", progress: true),
    "resume" when args.Length == 5 => await Resume(int.Parse(args[1]), int.Parse(args[2]), args[3], int.Parse(args[4])),
    "sandbox" when args.Length == 4 => await Sandbox(int.Parse(args[1]), args[2], int.Parse(args[3])),
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
            EnableEvaluationCache = Environment.GetEnvironmentVariable("SOAK_EVALUATION_CACHE") != "0",
            CheckpointFormat = Enum.TryParse(Environment.GetEnvironmentVariable("SOAK_CHECKPOINT_FORMAT"), out EvolutionCheckpointFormat format) &&
                Enum.IsDefined(typeof(EvolutionCheckpointFormat), format) ? format : EvolutionCheckpointFormat.Auto,
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
        // Measured after a full compacting collection, so a sample shows what the process retains rather than where the
        // collector happened to be; the instantaneous working set swings by a third with collection timing alone.
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
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

static async Task<int> Sandbox(int executions, string python, int concurrency)
{
    // Every grandchild carries this marker in its command line, so survivors can be found whatever their parent now is.
    string marker = "soak-orphan-" + Guid.NewGuid().ToString("N");
    var options = new AiDotNet.Evolution.Programs.ProgramSandboxOptions();
    options.SetInterpreter(AiDotNet.Evolution.Programs.ProgramLanguage.Python,
        new AiDotNet.Evolution.Programs.ProgramInterpreterSpecification(python, "{source}"));
    options.Limits.TimeLimitSeconds = 2;
    options.Limits.MaxConcurrentExecutions = concurrency;
    // DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP on Windows: the grandchild outlives its parent unless the job kills it.
    string spawn = "import subprocess, sys, time\n" +
        "flags = 0x00000008 | 0x00000200 if sys.platform == 'win32' else 0\n" +
        "subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)', '" + marker + "'], creationflags=flags)\n";
    string normal = spawn + "print('ok')\n";
    string slow = spawn + "time.sleep(30)\nprint('late')\n";
    // Positive control: a marked sleeper started outside the sandbox must be counted, or a zero below proves nothing.
    var control = new ProcessStartInfo(python) { UseShellExecute = false };
    foreach (string a in new[] { "-c", "import time; time.sleep(120)", marker }) control.ArgumentList.Add(a);
    using (Process sleeper = Process.Start(control) ?? throw new InvalidOperationException("Control sleeper did not start."))
    {
        int seen = 0;
        for (int attempt = 0; attempt < 20 && seen == 0; attempt++) { await Task.Delay(250); seen = Marked(marker); }
        sleeper.Kill(entireProcessTree: true);
        sleeper.WaitForExit();
        if (seen != 1) throw new InvalidOperationException($"The orphan counter saw {seen} marked processes where exactly one was running.");
    }
    // Second control: the same program run outside the sandbox must leave its grandchild behind, so zero orphans below
    // is the sandbox's doing rather than a spawn that never happened.
    string script = Path.Combine(Path.GetTempPath(), marker + ".py");
    File.WriteAllText(script, normal);
    var bare = new ProcessStartInfo(python) { UseShellExecute = false, RedirectStandardOutput = true };
    bare.ArgumentList.Add(script);
    using (Process parent = Process.Start(bare) ?? throw new InvalidOperationException("Bare run did not start."))
    {
        parent.StandardOutput.ReadToEnd();
        parent.WaitForExit();
    }
    int leftBehind = 0;
    for (int attempt = 0; attempt < 20 && leftBehind == 0; attempt++) { await Task.Delay(250); leftBehind = Marked(marker); }
    KillMarked(marker);
    File.Delete(script);
    if (leftBehind != 1) throw new InvalidOperationException($"Outside the sandbox the program left {leftBehind} grandchildren, not one.");
    int completed = 0, timedOut = 0, other = 0, peakMarked = 0;
    var clock = Stopwatch.StartNew();
    using (var engine = new AiDotNet.Evolution.Programs.ProcessProgramExecutionEngine(options))
    {
        var running = new List<Task<AiDotNet.Evolution.Programs.ProgramExecuteResponse>>();
        for (int i = 0; i < executions; i++)
        {
            running.Add(engine.ExecuteAsync(new AiDotNet.Evolution.Programs.ProgramExecuteRequest
            {
                Language = AiDotNet.Evolution.Programs.ProgramLanguage.Python,
                SourceCode = i % 100 == 99 ? slow : normal
            }));
            if (running.Count < concurrency * 2 && i < executions - 1) continue;
            foreach (var response in await Task.WhenAll(running))
            {
                if (response.Success) completed++;
                else if (response.ErrorCode == AiDotNet.Evolution.Programs.ProgramExecuteErrorCode.TimeoutOrCanceled) timedOut++;
                else other++;
            }
            running.Clear();
            if (i % 1000 == 999) peakMarked = Math.Max(peakMarked, Marked(marker));
        }
    }
    // A killed tree takes a moment to disappear. A true orphan sleeps for 120 s, so anything still marked after 30 s of
    // polling is one; a tree that was merely slow to tear down reaches zero well before.
    int survivors = Marked(marker);
    var settle = Stopwatch.StartNew();
    while (survivors > 0 && settle.Elapsed < TimeSpan.FromSeconds(30))
    {
        await Task.Delay(TimeSpan.FromSeconds(1));
        survivors = Marked(marker);
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Mode = "sandbox", Executions = executions, Completed = completed, TimedOut = timedOut, Other = other,
        PeakMarkedDuringRun = peakMarked, OrphansAfter = survivors, SettleSeconds = settle.Elapsed.TotalSeconds,
        Seconds = clock.Elapsed.TotalSeconds
    }));
    return survivors == 0 ? 0 : 1;
}

// Stops every process a control run left behind.
static void KillMarked(string marker)
{
    var start = new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*time.sleep(120)*" + marker + "*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }\"")
    { UseShellExecute = false };
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Process cleanup did not start.");
    process.WaitForExit();
}

// Counts live processes whose command line carries the marker (WMI, so it sees processes of any parent).
static int Marked(string marker)
{
    var start = new ProcessStartInfo("powershell", "-NoProfile -Command \"@(Get-CimInstance Win32_Process | Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*time.sleep(120)*" + marker + "*' }).Count\"")
    { RedirectStandardOutput = true, UseShellExecute = false };
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("Process query did not start.");
    string output = process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    return int.Parse(output.Trim());
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
