using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;

namespace AiDotNet.Evolution.Performance;

public static class ProfileRunner
{
    /// <summary>Runs in a fresh child pinned to the supplied CPUs. Setup and one warmup invocation precede the measured work.</summary>
    public static async Task<ProfileMeasurement> MeasureAsync(ProfileCase scenario, ulong affinity, ushort processorGroup = 0)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        scenario.Validate();
        ApplyAffinity(affinity);
        using var windowsAffinity = ProfileWindowsAffinity.BindOwnedWorker(processorGroup, affinity);
        return await MeasureCoreAsync(scenario, processorGroup).ConfigureAwait(false);
    }

    /// <summary>The identical measured phase without changing this process's affinity; contract tests use it in-process.</summary>
    public static Task<ProfileMeasurement> MeasureUnpinnedAsync(ProfileCase scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        scenario.Validate();
        return MeasureCoreAsync(scenario, ProfileWindowsAffinity.CurrentThreadGroup());
    }

    private static async Task<ProfileMeasurement> MeasureCoreAsync(ProfileCase scenario, ushort processorGroup)
    {
        // Windows rounds Task.Delay to the current timer tick (15.6 ms by default), which would silently turn the
        // declared 0/1/8 ms schedule into 0/15.6/15.6 ms. Raise it for this owned process and record what took effect.
        using var timerResolution = ProfileHost.RaiseTimerResolution();
        var builder = new EvolutionSearchSpaceBuilder();
        for (int dimension = 0; dimension < scenario.Dimensions; dimension++) builder.Add(EvolutionParameter.Real("x" + dimension, -5, 5));
        var space = builder.Build();
        var seeds = Enumerable.Range(0, scenario.Budget).Select(index => space.Sample(StableRandom.CreateStream(scenario.Seed, (ulong)index))).ToArray();
        ArchiveBenchmarks? archive = null;
        CheckpointBenchmarks? checkpoint = null;
        long warmupStarted = Stopwatch.GetTimestamp();
        long warmupOperations = 0;
        if (scenario.Kind == "archive-snapshot")
        {
            archive = new ArchiveBenchmarks { Cells = scenario.Cells, Dimensions = scenario.Dimensions }; archive.Setup();
            warmupOperations += archive.Snapshot().Count > 0 ? 1 : 0;
        }
        else if (scenario.Kind == "checkpoint-restore")
        {
            checkpoint = new CheckpointBenchmarks { Evaluations = scenario.Budget, Islands = scenario.Islands, Dimensions = scenario.Dimensions };
            await checkpoint.Setup();
            warmupOperations += (await checkpoint.RestoreWithoutNewEvaluations()).Length > 0 ? 1 : 0;
        }
        else
        {
            var warmupProbe = new Probe(scenario.Workers);
            await RunSearchAsync(scenario with { Budget = 16 }, space, seeds, warmupProbe);
            warmupOperations = warmupProbe.Calls;
        }
        double warmup = Stopwatch.GetElapsedTime(warmupStarted).TotalMilliseconds;

        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        long workingBefore = process.WorkingSet64, peakBefore = process.PeakWorkingSet64;
        double cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        ulong? cyclesBefore = ProfileHost.ProcessCycles();
        double? fineCpuBefore = ProfileHost.ProcessFineCpuMilliseconds();
        var probe = new Probe(scenario.Workers);
        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        long started = Stopwatch.GetTimestamp(); probe.Started = started;
        string? stateHash = null;
        double? bestQuality = null;
        int occupied = 0;
        long operationsPerIteration = scenario.Kind switch { "archive-snapshot" => 16, "checkpoint-restore" => 2, _ => scenario.Budget };
        IReadOnlyList<ProfileQualityPoint> curve = Array.Empty<ProfileQualityPoint>();
        long iterations = 0;
        double elapsed;
        // Single-shot sub-millisecond cases cannot be timed honestly, so every case repeats to a declared minimum.
        while (true)
        {
            probe.BeginIteration();
            if (archive is not null)
            {
                for (int operation = 0; operation < operationsPerIteration; operation++) occupied = archive.Snapshot().Count;
            }
            else if (checkpoint is not null)
            {
                for (int operation = 0; operation < operationsPerIteration; operation++) stateHash = await checkpoint.RestoreWithoutNewEvaluations();
            }
            else
            {
                var result = await RunSearchAsync(scenario, space, seeds, probe);
                if (result is not null)
                {
                    occupied = result.Islands.Sum(island => island.Count);
                    if (stateHash is not null && !string.Equals(stateHash, result.StateHash, StringComparison.Ordinal))
                        throw new InvalidOperationException("Repeating an identical case changed its deterministic state hash.");
                    stateHash = result.StateHash; bestQuality = result.Best?.Evaluation.Quality;
                }
            }
            if (++iterations == 1) curve = probe.Quality.ToArray();
            elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed >= ProfileProtocol.MinimumMeasuredMilliseconds) break;
            if (iterations >= ProfileProtocol.MaximumIterations)
                throw new InvalidOperationException("The measured phase never reached the declared minimum measured time.");
        }
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();
        ulong? cycles = ProfileHost.ProcessCycles() is { } after && cyclesBefore is { } before ? after - before : null;
        double? fineCpu = ProfileHost.ProcessFineCpuMilliseconds() is { } afterFine && fineCpuBefore is { } beforeFine ? afterFine - beforeFine : null;
        var measurement = new ProfileMeasurement(scenario, CaptureEnvironment(processorGroup), iterations, operationsPerIteration,
            iterations * operationsPerIteration, elapsed, elapsed / iterations, iterations * operationsPerIteration * 1000d / elapsed,
            warmup, warmupOperations, allocated, workingBefore, process.WorkingSet64, process.PeakWorkingSet64, peakBefore,
            process.TotalProcessorTime.TotalMilliseconds - cpuBefore, cycles, fineCpu,
            scenario.Kind is "engine" or "evaluation-only" ? probe.OccupiedTicks * 1000d / Stopwatch.Frequency / (scenario.Workers * elapsed) : null,
            probe.PeakConcurrency, probe.Calls, occupied, stateHash, bestQuality,
            probe.CheckpointSaves, probe.CheckpointBytes, probe.CheckpointTicks * 1000d / Stopwatch.Frequency,
            probe.DelayBuckets(), curve);
        ProfileValidation.Validate(measurement);
        return measurement;
    }

    private static ProfileEnvironment CaptureEnvironment(ushort processorGroup)
    {
        ulong affinity = CurrentAffinity();
        var cores = ProfileHost.CoreMasks(processorGroup);
        return new ProfileEnvironment(RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), ProfileHost.CpuName(),
            Environment.ProcessorCount, affinity.ToString("X", CultureInfo.InvariantCulture), GCSettings.IsServerGC,
            Environment.GetEnvironmentVariable("DOTNET_TieredCompilation"), Environment.GetEnvironmentVariable("DOTNET_Thread_UseAllCpuGroups"),
            Environment.GetEnvironmentVariable("DOTNET_Thread_AssignCpuGroups"), CurrentProcessorGroup(),
            ProfileHost.DescribeTopology(cores, affinity), ProfileHost.PowerPolicy(),
            ProfileHost.TimerResolutionMilliseconds(), ProfileHost.ProcessorTimeResolutionMilliseconds());
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

    /// <summary>Picks one logical processor per physical core, away from CPU 0, using this host's reported topology.</summary>
    public static ulong SelectAffinity(ulong available, int maximumProcessors = 4) =>
        ProfileHost.SelectAffinity(available, ProfileHost.CoreMasks(ProfileWindowsAffinity.CurrentThreadGroup()), maximumProcessors);

    private static string CurrentProcessorGroup()
    {
        if (!OperatingSystem.IsWindows()) return "not-applicable";
        using var process = Process.GetCurrentProcess();
        ushort count = 64; var groups = new ushort[count];
        if (!GetProcessGroupAffinity(process.Handle, ref count, groups))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        // A worker that spans several CPU groups reports all of them; the campaign's runtime-control
        // check then rejects it, because it can only match the single declared group.
        return string.Join("+", groups.Take(count).Select(group => group.ToString(CultureInfo.InvariantCulture)));
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
        IVariationOperator<EvolutionSearchGenome> variation = scenario.Variation == "mutation"
            ? new SearchSpaceMutation(space)
            : new SearchSpaceRestart(space);
        var task = new EvolutionSearchTask(space, "profile-sphere", "profile-sphere-v1", "profile-sphere-" + scenario.MixedDuration,
            (genome, _, token) => EvaluateAsync(genome, scenario, probe, token));
        var engine = new EvolutionEngine<EvolutionSearchGenome>(task, variation,
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
        if (active > probe.Workers) throw new InvalidOperationException("Worker accounting exceeded the fixture bound.");
        int prior;
        do { prior = Volatile.Read(ref probe.PeakConcurrency); } while (active > prior && Interlocked.CompareExchange(ref probe.PeakConcurrency, active, prior) != prior);
        try
        {
            if (scenario.MixedDuration)
            {
                int bucket = ProfileDelaySchedule.Bucket(genome.Number("x0"));
                int delay = ProfileDelaySchedule.Milliseconds[bucket];
                double observed = 0;
                if (delay > 0)
                {
                    long delayStarted = Stopwatch.GetTimestamp();
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    observed = Stopwatch.GetElapsedTime(delayStarted).TotalMilliseconds;
                }
                probe.RecordDelay(bucket, observed);
            }
            double loss = genome.Values.Values.Sum(value => value.Number * value.Number);
            return EvolutionTaskResult.Completed(-loss, new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1);
        }
        finally { Interlocked.Add(ref probe.OccupiedTicks, Stopwatch.GetTimestamp() - started); Interlocked.Decrement(ref probe.Active); }
    }

    private sealed class Probe(int workers) : IEvolutionObserver<EvolutionSearchGenome>
    {
        private readonly long[] _delayCount = new long[ProfileDelaySchedule.Milliseconds.Count];
        private readonly double[] _delaySum = new double[ProfileDelaySchedule.Milliseconds.Count];
        private readonly double[] _delayMinimum = Enumerable.Repeat(double.PositiveInfinity, ProfileDelaySchedule.Milliseconds.Count).ToArray();
        private readonly double[] _delayMaximum = new double[ProfileDelaySchedule.Milliseconds.Count];
        private readonly object _delayLock = new();
        public readonly int Workers = workers;
        public long Started = Stopwatch.GetTimestamp();
        public long Calls, OccupiedTicks, CheckpointSaves, CheckpointBytes, CheckpointTicks;
        public int Active, PeakConcurrency;
        public readonly List<ProfileQualityPoint> Quality = new();
        private double _best = double.NegativeInfinity;

        /// <summary>Starts a repeated measured iteration; cumulative cost counters continue, the quality curve restarts.</summary>
        public void BeginIteration() { Quality.Clear(); _best = double.NegativeInfinity; }

        public void RecordDelay(int bucket, double observedMilliseconds)
        {
            lock (_delayLock)
            {
                _delayCount[bucket]++;
                _delaySum[bucket] += observedMilliseconds;
                _delayMinimum[bucket] = Math.Min(_delayMinimum[bucket], observedMilliseconds);
                _delayMaximum[bucket] = Math.Max(_delayMaximum[bucket], observedMilliseconds);
            }
        }

        public IReadOnlyList<ProfileDelayBucket> DelayBuckets()
        {
            lock (_delayLock)
            {
                var buckets = new List<ProfileDelayBucket>();
                for (int bucket = 0; bucket < ProfileDelaySchedule.Milliseconds.Count; bucket++)
                {
                    if (_delayCount[bucket] == 0) continue;
                    buckets.Add(new ProfileDelayBucket(ProfileDelaySchedule.Milliseconds[bucket], _delayCount[bucket],
                        _delaySum[bucket] / _delayCount[bucket], double.IsPositiveInfinity(_delayMinimum[bucket]) ? 0 : _delayMinimum[bucket],
                        _delayMaximum[bucket]));
                }
                return buckets;
            }
        }

        public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> evolutionEvent, CancellationToken cancellationToken = default)
        {
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
