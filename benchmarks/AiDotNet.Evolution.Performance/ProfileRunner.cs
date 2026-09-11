using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace AiDotNet.Evolution.Performance;

public static class ProfileRunner
{
    /// <summary>Runs in a fresh child. Setup and one warmup invocation precede the measured work.</summary>
    public static async Task<ProfileMeasurement> MeasureAsync(ProfileCase scenario, ulong affinity, ushort processorGroup = 0)
    {
        scenario.Validate();
        ApplyAffinity(affinity);
        using var windowsAffinity = ProfileWindowsAffinity.BindOwnedWorker(processorGroup, affinity);
        var builder = new EvolutionSearchSpaceBuilder();
        for (int dimension = 0; dimension < scenario.Dimensions; dimension++) builder.Add(EvolutionParameter.Real("x" + dimension, -5, 5));
        var space = builder.Build();
        var seeds = Enumerable.Range(0, scenario.Budget).Select(index => space.Sample(StableRandom.CreateStream(scenario.Seed, (ulong)index))).ToArray();
        ArchiveBenchmarks? archive = null;
        CheckpointBenchmarks? checkpoint = null;
        if (scenario.Kind == "archive-snapshot")
        {
            archive = new ArchiveBenchmarks { Cells = scenario.Cells, Dimensions = scenario.Dimensions }; archive.Setup(); archive.Snapshot();
        }
        else if (scenario.Kind == "checkpoint-restore")
        {
            checkpoint = new CheckpointBenchmarks { Evaluations = scenario.Budget, Islands = scenario.Islands, Dimensions = scenario.Dimensions };
            await checkpoint.Setup(); await checkpoint.RestoreWithoutNewEvaluations();
        }
        else await RunSearchAsync(scenario with { Budget = 16 }, space, seeds, new Probe(scenario.Workers));

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        long workingBefore = process.WorkingSet64, peakBefore = process.PeakWorkingSet64;
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        var probe = new Probe(scenario.Workers);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp(); probe.Started = started;
        EvolutionRunResult<EvolutionSearchGenome>? result = null;
        string? restoredHash = null;
        long operations;
        int occupied = 0;
        if (archive is not null)
        {
            operations = 16;
            for (int i = 0; i < operations; i++) occupied = archive.Snapshot().Count;
        }
        else if (checkpoint is not null)
        {
            operations = 2;
            for (int i = 0; i < operations; i++) restoredHash = await checkpoint.RestoreWithoutNewEvaluations();
        }
        else
        {
            operations = scenario.Budget;
            result = await RunSearchAsync(scenario, space, seeds, probe);
            occupied = result?.Islands.Sum(island => island.Count) ?? 0;
        }
        double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        var environment = new ProfileEnvironment(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "not-reported",
            Environment.ProcessorCount, CurrentAffinity().ToString("X", CultureInfo.InvariantCulture), GCSettings.IsServerGC,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Environment.GetEnvironmentVariable("DOTNET_Thread_UseAllCpuGroups"),
            Environment.GetEnvironmentVariable("DOTNET_Thread_AssignCpuGroups"), CurrentProcessorGroup());
        var measurement = new ProfileMeasurement(scenario, environment, operations, elapsed, operations * 1000d / elapsed,
            allocated, workingBefore, process.WorkingSet64, process.PeakWorkingSet64, peakBefore,
            process.TotalProcessorTime.TotalMilliseconds - cpuBefore,
            scenario.Kind is "engine" or "evaluation-only" ? probe.OccupiedTicks * 1000d / Stopwatch.Frequency / (scenario.Workers * elapsed) : null,
            probe.PeakConcurrency, probe.Calls, occupied, result?.StateHash ?? restoredHash, result?.Best?.Evaluation.Quality,
            probe.CheckpointSaves, probe.CheckpointBytes, probe.CheckpointTicks * 1000d / Stopwatch.Frequency, probe.Quality.AsReadOnly());
        ProfileValidation.Validate(measurement);
        return measurement;
    }

    public static ulong CurrentAffinity()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            using var process = Process.GetCurrentProcess();
            return unchecked((ulong)process.ProcessorAffinity.ToInt64());
        }
        throw new PlatformNotSupportedException("The controlled profiler currently supports Windows and Linux CPU affinity.");
    }

    public static ulong SelectAffinity(ulong available, int maximumProcessors = 4)
    {
        if (available == 0 || maximumProcessors < 1 || maximumProcessors > 4) throw new ArgumentOutOfRangeException(nameof(available));
        ulong selected = 0; int count = 0;
        for (int bit = 0; bit < 64 && count < maximumProcessors; bit++)
            if ((available & (1UL << bit)) != 0) { selected |= 1UL << bit; count++; }
        return selected;
    }

    private static string CurrentProcessorGroup()
    {
        if (!OperatingSystem.IsWindows()) return "not-applicable";
        using var process = Process.GetCurrentProcess();
        ushort count = 64; var groups = new ushort[count];
        if (!GetProcessGroupAffinity(process.Handle, ref count, groups))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (count != 1) throw new InvalidOperationException("The controlled worker spans multiple Windows CPU groups.");
        return groups[0].ToString(CultureInfo.InvariantCulture);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessGroupAffinity(IntPtr process, ref ushort groupCount, [Out] ushort[] groups);

    private static void ApplyAffinity(ulong requested)
    {
        ulong available = CurrentAffinity();
        if (requested == 0 || (requested & ~available) != 0) throw new ArgumentOutOfRangeException(nameof(requested));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            using var process = Process.GetCurrentProcess(); process.ProcessorAffinity = new IntPtr(unchecked((long)requested));
        }
        if (CurrentAffinity() != requested) throw new InvalidOperationException("Worker CPU affinity was not applied exactly.");
    }

    private static async Task<EvolutionRunResult<EvolutionSearchGenome>?> RunSearchAsync(ProfileCase scenario, EvolutionSearchSpace space,
        IReadOnlyList<EvolutionSearchGenome> inputs, Probe probe)
    {
        if (scenario.Kind == "evaluation-only")
        {
            await Parallel.ForEachAsync(inputs.Take(scenario.Budget), new ParallelOptions { MaxDegreeOfParallelism = scenario.Workers },
                async (genome, token) => { await EvaluateAsync(genome, scenario, probe, token); });
            return null;
        }
        var task = new EvolutionSearchTask(space, "profile-sphere", "profile-sphere-v1", "profile-sphere-" + scenario.MixedDuration,
            (genome, _, token) => EvaluateAsync(genome, scenario, probe, token));
        var engine = new EvolutionEngine<EvolutionSearchGenome>(task, new SearchSpaceRestart(space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, scenario.Cells) }),
            new EvolutionEngineOptions
            {
                RunId = "profile",
                Seed = scenario.Seed,
                IslandCount = scenario.Islands,
                Dispatch = scenario.Dispatch,
                MaxDegreeOfParallelism = scenario.Workers,
                MaxInFlight = scenario.MaxInFlight,
                ProposalBatchSize = 8,
                MaxEvaluationAttempts = scenario.Budget,
                MaxProposals = scenario.Budget * 8,
                MaxGenerations = scenario.Budget * 8,
                MigrationInterval = 0,
                EnableEvaluationCache = false,
                CheckpointInterval = scenario.Checkpoint ? 32 : 0
            }, observer: probe, genomeCodec: space, checkpointStore: scenario.Checkpoint ? new TimedStore(probe) : null);
        var result = await engine.RunAsync(inputs.Take(8));
        if (result.Counters.EvaluationAttempts != scenario.Budget || result.Counters.CompletedEvaluations != scenario.Budget ||
            result.RetainedFailures.Count != 0) throw new InvalidOperationException("The engine profiling fixture did not finish valid declared work.");
        return result;
    }

    private static async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionSearchGenome genome, ProfileCase scenario, Probe probe, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp(); int active = Interlocked.Increment(ref probe.Active);
        Interlocked.Increment(ref probe.Calls);
        int prior;
        do { prior = Volatile.Read(ref probe.PeakConcurrency); } while (active > prior && Interlocked.CompareExchange(ref probe.PeakConcurrency, active, prior) != prior);
        try
        {
            if (scenario.MixedDuration)
            {
                int bucket = (int)((genome.Number("x0") + 5) * 1000) % 3;
                int delay = bucket == 0 ? 0 : bucket == 1 ? 1 : 8;
                if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
            }
            double loss = genome.Values.Values.Sum(value => value.Number * value.Number);
            return EvolutionTaskResult.Completed(-loss, new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1);
        }
        finally { Interlocked.Add(ref probe.OccupiedTicks, Stopwatch.GetTimestamp() - started); Interlocked.Decrement(ref probe.Active); }
    }

    private sealed class Probe(int workers) : IEvolutionObserver<EvolutionSearchGenome>
    {
        public long Started = Stopwatch.GetTimestamp();
        public long Calls, OccupiedTicks, CheckpointSaves, CheckpointBytes, CheckpointTicks;
        public int Active, PeakConcurrency;
        public readonly List<ProfileQualityPoint> Quality = new();
        private double _best = double.NegativeInfinity;
        public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> evolutionEvent, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref Active) > workers) throw new InvalidOperationException("Worker accounting exceeded the fixture bound.");
            if (evolutionEvent.Kind == EvolutionEventKind.Evaluated && evolutionEvent.Evaluation is { Status: EvolutionEvaluationStatus.Completed } evaluation)
            {
                _best = Math.Max(_best, evaluation.Quality!.Value);
                Quality.Add(new ProfileQualityPoint(evaluation.EvaluationId, Quality.Count + 1, Stopwatch.GetElapsedTime(Started).TotalMilliseconds, _best));
            }
            return default;
        }
    }

    private sealed class TimedStore(Probe probe) : IEvolutionCheckpointStore
    {
        private readonly InMemoryEvolutionCheckpointStore _inner = new(1);
        public async Task SaveAsync(EvolutionCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            long started = Stopwatch.GetTimestamp();
            await _inner.SaveAsync(checkpoint, cancellationToken);
            probe.CheckpointTicks += Stopwatch.GetTimestamp() - started;
            probe.CheckpointSaves++; probe.CheckpointBytes += Encoding.UTF8.GetByteCount(checkpoint.Payload);
        }
        public Task<EvolutionCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default) => _inner.LoadLatestAsync(runId, cancellationToken);
    }
}
