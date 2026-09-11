using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Performance;

public sealed record ProfileAttempt(string CaseId, int Repetition, string InputFile, string OutputFile, string LogFile,
    string Status, string? Error, ProfileContention? Contention, ProfileMeasurement? Measurement);

public sealed record ProfileSummary(string CaseId, string Kind, int Workers, EvolutionDispatchMode Dispatch, bool MixedDuration,
    bool Checkpoint, int Repetitions, long OperationsPerIteration, double MedianIterations,
    double MedianOperationsPerSecond, double MinimumOperationsPerSecond, double MaximumOperationsPerSecond,
    double MedianElapsedMilliseconds, double MinimumElapsedMilliseconds, double MaximumElapsedMilliseconds, double ElapsedMaxMinRatio,
    double MedianMillisecondsPerIteration, double MedianAllocatedBytes, long MaximumLifetimePeakWorkingSetBytes,
    double? MedianEvaluatorSlotUtilization, double? MedianBestQuality, double MedianCheckpointStoreMilliseconds,
    double MaximumForeignCpuFraction);

public sealed record ProfileReport(string Protocol, string SourceRevision, string AssemblyInformationalVersion, string WorkingTreeStatus,
    bool Smoke, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string AffinityHex, string PinnedTopology,
    int Repetitions, IReadOnlyList<ProfileCase> Cases, IReadOnlyList<ProfileAttempt> Attempts,
    int DeterministicWorkerGroups, IReadOnlyList<ProfileSummary> Summaries, string Status, IReadOnlyList<string> Limitations);

/// <summary>Runs the declared suite in fresh, affinity-pinned child processes and retains every failed attempt.</summary>
public static class ProfileCampaign
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static async Task<ProfileReport> RunAsync(string outputDirectory, string sourceRevision, bool smoke)
    {
        // The caller no longer decides the revision on trust: it must be the commit compiled into the measured assembly.
        ProfileRevisionInfo revision = ProfileRevision.Verify(sourceRevision);
        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Choose a new or empty output directory; previous evidence is never overwritten.");
        Directory.CreateDirectory(root);
        ushort processorGroup = ProfileWindowsAffinity.CurrentThreadGroup();
        ulong affinity = ProfileRunner.SelectAffinity(ProfileRunner.CurrentAffinity());
        string affinityHex = affinity.ToString("X", CultureInfo.InvariantCulture);
        string topology = ProfileHost.DescribeTopology(ProfileHost.CoreMasks(processorGroup), affinity);
        int repetitions = smoke ? 1 : 3;
        var cases = ProfileCase.Suite(smoke, 4711);
        var started = DateTimeOffset.UtcNow;
        var attempts = new List<ProfileAttempt>();
        var plan = new
        {
            protocol = "engine-profile-v2",
            sourceRevision = revision.Supplied,
            assemblyInformationalVersion = revision.InformationalVersion,
            workingTree = revision.WorkingTree,
            smoke,
            started,
            repetitions,
            affinityHex,
            processorGroup,
            topology,
            minimumMeasuredMilliseconds = ProfileProtocol.MinimumMeasuredMilliseconds,
            delaySchedule = ProfileDelaySchedule.Milliseconds,
            delayToleranceMilliseconds = ProfileProtocol.DelayToleranceMilliseconds,
            maximumForeignCpuFraction = ProfileProtocol.MaximumForeignCpuFraction,
            maximumElapsedMaxMinRatio = ProfileProtocol.MaximumElapsedMaxMinRatio,
            cases
        };
        await WriteNewAsync(Path.Combine(root, "plan.json"), plan);
        for (int repetition = 0; repetition < repetitions; repetition++)
            foreach (int index in CaseOrder(cases.Count, repetition, 4711))
            {
                var scenario = cases[index];
                string stem = scenario.Id + "-r" + repetition.ToString(CultureInfo.InvariantCulture);
                string input = stem + "-case.json", output = stem + "-measurement.json", log = stem + ".log";
                await WriteNewAsync(Path.Combine(root, input), scenario);
                string status = "failed"; string? error = null; ProfileMeasurement? measurement = null; ProfileContention? contention = null;
                try
                {
                    contention = await RunChildAsync(Path.Combine(root, input), Path.Combine(root, output), Path.Combine(root, log), affinity, processorGroup);
                    var info = new FileInfo(Path.Combine(root, output));
                    if (!info.Exists || info.Length > 8 * 1024 * 1024) throw new InvalidDataException("The worker result is missing or oversized.");
                    measurement = JsonSerializer.Deserialize<ProfileMeasurement>(await File.ReadAllTextAsync(info.FullName), JsonOptions)
                        ?? throw new InvalidDataException("The worker emitted a null result.");
                    if (measurement.Case != scenario) throw new InvalidDataException("The worker measured a different case from the predeclared plan.");
                    ProfileValidation.Validate(measurement);
                    ProfileValidation.ValidateRuntimeControls(measurement.Environment, affinity, processorGroup);
                    ProfileValidation.ValidateContention(contention ?? throw new InvalidDataException("Pinned-CPU load could not be sampled for this attempt."),
                        smoke ? SmokeForeignCpuFraction : ProfileProtocol.MaximumForeignCpuFraction);
                    status = "passed";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    error = exception.ToString();
                }
                var attempt = new ProfileAttempt(scenario.Id, repetition, input, output, log, status, error, contention, measurement);
                attempts.Add(attempt);
                // An interrupted campaign still has its full plan plus durable per-attempt outcomes.
                await WriteNewAsync(Path.Combine(root, stem + "-attempt.json"), attempt);
                Console.WriteLine($"{attempts.Count}/{cases.Count * repetitions} {stem}: {status}" +
                    (contention is null ? string.Empty : $" (foreign CPU {contention.ForeignBusyFraction:P2})"));
            }
        int deterministicGroups = 0;
        string campaignStatus = "passed";
        var summaries = cases.Select(scenario => Summarize(scenario, attempts)).Where(item => item is not null).Cast<ProfileSummary>().ToArray();
        try { deterministicGroups = ValidateComplete(cases, repetitions, attempts, smoke ? SmokeForeignCpuFraction : ProfileProtocol.MaximumForeignCpuFraction); }
        catch (InvalidDataException exception) { campaignStatus = "failed"; Console.Error.WriteLine(exception.Message); }
        var report = new ProfileReport("engine-profile-v2", revision.Supplied, revision.InformationalVersion, revision.WorkingTree,
            smoke, started, DateTimeOffset.UtcNow, affinityHex, topology, repetitions, cases, attempts, deterministicGroups, summaries, campaignStatus,
            new[] {
                "The source revision is verified against the commit SourceLink compiled into the measured assembly; the working-tree state is recorded, not enforced.",
                "CPU affinity (one logical processor per physical core, away from CPU 0), processor count, timer resolution and tiered compilation are pinned per owned child. Host frequency and power policy are recorded, not controlled.",
                "Foreign CPU use on the pinned processors is measured per attempt from system-wide per-processor counters and fails the attempt above " +
                    ProfileProtocol.MaximumForeignCpuFraction.ToString("P0", CultureInfo.InvariantCulture) + "; it is an aggregate, not a per-process attribution.",
                "Every case repeats until at least " + ProfileProtocol.MinimumMeasuredMilliseconds.ToString(CultureInfo.InvariantCulture) +
                    " ms is measured; throughput and allocation are per operation and elapsed time is the total for all iterations.",
                "Peak working set is each fresh process's lifetime high-water mark, including startup, setup and warmup; it is not measured-phase managed allocation.",
                "Managed allocation uses process-wide GC.GetTotalAllocatedBytes around measured work; observer instrumentation is included.",
                "Process CPU time keeps the platform's coarse clock resolution, which is recorded; Windows attempts also record unhalted process cycles.",
                "Evaluator slot utilization includes simulated asynchronous waits whose observed durations are recorded per bucket. It is not CPU utilization or a model-provider throughput forecast.",
                "Checkpoint store timing excludes serialization; compare paired checkpoint-on/off whole-run timing for inclusive overhead.",
                "Archive snapshot fixtures fill the declared cells; engine cell counts are capacity and observed occupancy is reported separately.",
                "One authored sphere task, one fixed seed and three host-local repetitions are diagnostic, not a release regression gate, optimizer-quality benchmark or competitor claim.",
                "Batch/continuous quality curves use the same budget and MaxInFlight=8; worker invariance is checked only within fixed dispatch semantics."
            });
        await WriteNewAsync(Path.Combine(root, "report.json"), report);
        return report;
    }

    /// <summary>Deterministic, seeded case order per repetition, so no dispatcher always runs on the coldest host.</summary>
    public static IReadOnlyList<int> CaseOrder(int count, int repetition, ulong seed)
    {
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        if (repetition < 0) throw new ArgumentOutOfRangeException(nameof(repetition));
        int[] order = Enumerable.Range(0, count).ToArray();
        var random = StableRandom.CreateStream(seed, (ulong)repetition);
        for (int index = count - 1; index > 0; index--)
        {
            int swap = random.NextInt(index + 1);
            (order[index], order[swap]) = (order[swap], order[index]);
        }
        return order;
    }

    /// <summary>Contention is recorded but not enforced for the hosted smoke run, which shares its runner with other work.</summary>
    public const double SmokeForeignCpuFraction = 1;

    public static int ValidateComplete(IReadOnlyList<ProfileCase> cases, int repetitions, IReadOnlyList<ProfileAttempt> attempts,
        double maximumForeignFraction = ProfileProtocol.MaximumForeignCpuFraction)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(attempts);
        if (repetitions < 1 || cases.Count == 0 || cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count ||
            attempts.Count != cases.Count * repetitions) throw new InvalidDataException("Campaign evidence does not cover the entire declared plan.");
        foreach (var scenario in cases)
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                var matching = attempts.Where(item => item.CaseId == scenario.Id && item.Repetition == repetition).ToArray();
                if (matching.Length != 1 || matching[0].Status != "passed" || matching[0].Measurement is not { } result || result.Case != scenario)
                    throw new InvalidDataException("A planned attempt failed, was omitted, duplicated or substituted: " + scenario.Id);
                ProfileValidation.Validate(result);
                if (matching[0].Contention is not { } contention)
                    throw new InvalidDataException("An attempt has no pinned-CPU contention evidence: " + scenario.Id);
                ProfileValidation.ValidateContention(contention, maximumForeignFraction);
            }
        var measurements = attempts.Select(item => item.Measurement!).ToArray();
        if (measurements.Select(item => item.Environment).Distinct().Count() != 1)
            throw new InvalidDataException("Runtime or machine controls changed across the campaign.");
        ProfileValidation.ValidateDispersion(cases.Select(scenario => Summarize(scenario, attempts)).Where(item => item is not null).Cast<ProfileSummary>());
        return ProfileValidation.ValidateDeterminism(measurements);
    }

    /// <summary>Medians with their slowest/fastest repetitions, so a single number never hides repetition spread.</summary>
    public static ProfileSummary? Summarize(ProfileCase scenario, IEnumerable<ProfileAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(attempts);
        var passed = attempts.Where(item => item.CaseId == scenario.Id && item.Status == "passed").ToArray();
        var measurements = passed.Select(item => item.Measurement!).ToArray();
        if (measurements.Length == 0) return null;
        double minimumElapsed = measurements.Min(item => item.MillisecondsPerIteration);
        double maximumElapsed = measurements.Max(item => item.MillisecondsPerIteration);
        return new ProfileSummary(scenario.Id, scenario.Kind, scenario.Workers, scenario.Dispatch, scenario.MixedDuration, scenario.Checkpoint,
            measurements.Length, measurements[0].OperationsPerIteration, Median(measurements.Select(item => (double)item.Iterations)),
            Median(measurements.Select(item => item.OperationsPerSecond)), measurements.Min(item => item.OperationsPerSecond),
            measurements.Max(item => item.OperationsPerSecond), Median(measurements.Select(item => item.ElapsedMilliseconds)),
            measurements.Min(item => item.ElapsedMilliseconds), measurements.Max(item => item.ElapsedMilliseconds),
            minimumElapsed <= 0 ? double.PositiveInfinity : maximumElapsed / minimumElapsed,
            Median(measurements.Select(item => item.MillisecondsPerIteration)),
            Median(measurements.Select(item => (double)item.ManagedAllocatedBytes)),
            measurements.Max(item => item.ProcessLifetimePeakWorkingSetBytes),
            measurements.All(item => item.EvaluatorSlotUtilization.HasValue) ? Median(measurements.Select(item => item.EvaluatorSlotUtilization!.Value)) : null,
            measurements.All(item => item.BestQuality.HasValue) ? Median(measurements.Select(item => item.BestQuality!.Value)) : null,
            Median(measurements.Select(item => item.CheckpointStoreMilliseconds)),
            passed.Select(item => item.Contention?.ForeignBusyFraction ?? 0).DefaultIfEmpty(0).Max());
    }

    public static double Median(IEnumerable<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) throw new ArgumentException("Cannot summarize an empty sample.", nameof(values));
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }

    /// <summary>Starts one owned child and measures how much of the pinned CPUs other processes used while it ran.</summary>
    private static async Task<ProfileContention?> RunChildAsync(string input, string output, string log, ulong affinity, ushort processorGroup)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new InvalidOperationException("Cannot resolve the profile executable."))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(start.FileName), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        if (OperatingSystem.IsLinux())
        {
            // sched_setaffinity(pid) only affects the main thread on Linux. Apply the mask before
            // CLR startup so every subsequently created runtime/pool thread inherits it.
            const string taskset = "/usr/bin/taskset";
            if (!File.Exists(taskset)) throw new PlatformNotSupportedException("Linux profiling requires /usr/bin/taskset (util-linux).");
            start.ArgumentList.Insert(0, start.FileName);
            start.ArgumentList.Insert(0, affinity.ToString("X", CultureInfo.InvariantCulture));
            start.FileName = taskset;
        }
        foreach (string argument in new[] { "--profile-worker", input, output, affinity.ToString("X", CultureInfo.InvariantCulture), processorGroup.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_TieredCompilation"] = "0";
        start.Environment["DOTNET_PROCESSOR_COUNT"] = BitOperations.PopCount(affinity).ToString(CultureInfo.InvariantCulture);
        // Windows 11 can otherwise redistribute newly created pool threads across CPU groups,
        // expanding the process mask after ApplyAffinity succeeded. Pin these before runtime startup.
        start.Environment["DOTNET_Thread_UseAllCpuGroups"] = "0";
        start.Environment["DOTNET_Thread_AssignCpuGroups"] = "0";
        ProfileCpuLoadSample? loadBefore = ProfileHost.SampleCpuLoad(affinity, processorGroup);
        long attemptStarted = Stopwatch.GetTimestamp();
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned profile worker.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        bool timedOut = false;
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!child.HasExited) child.Kill(entireProcessTree: true); // Only this owned child and its descendants.
            await child.WaitForExitAsync();
        }
        double attemptElapsed = Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds;
        ProfileCpuLoadSample? loadAfter = ProfileHost.SampleCpuLoad(affinity, processorGroup);
        double? workerCpu = null;
        try { workerCpu = child.TotalProcessorTime.TotalMilliseconds; } catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or SystemException) { }
        await using (var stream = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync($"exit={child.ExitCode}; timeout={timedOut}\nSTDOUT\n{await stdout}\nSTDERR\n{await stderr}");
        if (timedOut || child.ExitCode != 0) throw new InvalidOperationException($"Profile worker failed (exit {child.ExitCode}, timeout {timedOut}); see {log}.");
        return Contention(loadBefore, loadAfter, affinity, attemptElapsed, workerCpu);
    }

    /// <summary>Foreign busy time is pinned-CPU busy time minus this worker's own CPU time, over the pinned capacity.</summary>
    public static ProfileContention? Contention(ProfileCpuLoadSample? before, ProfileCpuLoadSample? after, ulong affinity,
        double elapsedMilliseconds, double? workerCpuMilliseconds)
    {
        if (before is null || after is null || elapsedMilliseconds <= 0) return null;
        int pinned = BitOperations.PopCount(affinity);
        double busy = Math.Max(0, after.BusyMilliseconds - before.BusyMilliseconds);
        double capacity = pinned * elapsedMilliseconds;
        double foreign = capacity <= 0 ? 0 : Math.Max(0, busy - (workerCpuMilliseconds ?? 0)) / capacity;
        return new ProfileContention(affinity.ToString("X", CultureInfo.InvariantCulture), pinned, elapsedMilliseconds, busy,
            workerCpuMilliseconds, foreign, foreign > ProfileProtocol.ForeignCpuFlagFraction, after.Source);
    }

    public static async Task WriteNewAsync<T>(string path, T value)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }
}
