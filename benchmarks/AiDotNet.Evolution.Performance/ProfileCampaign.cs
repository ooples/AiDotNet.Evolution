using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Performance;

public sealed record ProfileAttempt(string CaseId, int Repetition, string InputFile, string OutputFile, string LogFile,
    string Status, string? Error, ProfileMeasurement? Measurement);

public sealed record ProfileSummary(string CaseId, int Repetitions, double MedianOperationsPerSecond, double MedianElapsedMilliseconds,
    double MedianAllocatedBytes, long MaximumLifetimePeakWorkingSetBytes, double? MedianEvaluatorSlotUtilization,
    double? MedianBestQuality, double MedianCheckpointStoreMilliseconds);

public sealed record ProfileReport(string Protocol, string SourceRevision, bool Smoke, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc,
    string AffinityHex, int Repetitions, IReadOnlyList<ProfileCase> Cases, IReadOnlyList<ProfileAttempt> Attempts,
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
        if (sourceRevision.Length != 40 || sourceRevision.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("Supply the exact 40-character Git revision of the built source.", nameof(sourceRevision));
        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new IOException("Choose a new or empty output directory; previous evidence is never overwritten.");
        Directory.CreateDirectory(root);
        ulong affinity = ProfileRunner.SelectAffinity(ProfileRunner.CurrentAffinity());
        ushort processorGroup = ProfileWindowsAffinity.CurrentThreadGroup();
        int repetitions = smoke ? 1 : 3;
        var cases = ProfileCase.Suite(smoke, 4711);
        var started = DateTimeOffset.UtcNow;
        var attempts = new List<ProfileAttempt>();
        var plan = new
        {
            protocol = "engine-profile-v1",
            sourceRevision,
            smoke,
            started,
            repetitions,
            affinityHex = affinity.ToString("X", CultureInfo.InvariantCulture),
            processorGroup,
            cases
        };
        await WriteNewAsync(Path.Combine(root, "plan.json"), plan);
        // Rotate case order across repetitions to avoid always giving one dispatcher the coldest host.
        for (int repetition = 0; repetition < repetitions; repetition++)
            for (int offset = 0; offset < cases.Count; offset++)
            {
                var scenario = cases[(offset + repetition * 13) % cases.Count];
                string stem = scenario.Id + "-r" + repetition.ToString(CultureInfo.InvariantCulture);
                string input = stem + "-case.json", output = stem + "-measurement.json", log = stem + ".log";
                await WriteNewAsync(Path.Combine(root, input), scenario);
                string status = "failed"; string? error = null; ProfileMeasurement? measurement = null;
                try
                {
                    await RunChildAsync(Path.Combine(root, input), Path.Combine(root, output), Path.Combine(root, log), affinity, processorGroup);
                    var info = new FileInfo(Path.Combine(root, output));
                    if (!info.Exists || info.Length > 8 * 1024 * 1024) throw new InvalidDataException("The worker result is missing or oversized.");
                    measurement = JsonSerializer.Deserialize<ProfileMeasurement>(await File.ReadAllTextAsync(info.FullName), JsonOptions)
                        ?? throw new InvalidDataException("The worker emitted a null result.");
                    if (measurement.Case != scenario) throw new InvalidDataException("The worker measured a different case from the predeclared plan.");
                    ProfileValidation.Validate(measurement);
                    if (measurement.Environment.AffinityHex != affinity.ToString("X", CultureInfo.InvariantCulture) ||
                        measurement.Environment.TieredCompilation != "0" || measurement.Environment.UseAllCpuGroups != "0" ||
                        measurement.Environment.AssignCpuGroups != "0" || measurement.Environment.LogicalProcessors != BitOperations.PopCount(affinity) ||
                        (OperatingSystem.IsWindows() && measurement.Environment.ProcessorGroup != processorGroup.ToString(CultureInfo.InvariantCulture)))
                        throw new InvalidDataException("The worker did not apply the declared runtime controls.");
                    status = "passed";
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    error = exception.ToString();
                }
                var attempt = new ProfileAttempt(scenario.Id, repetition, input, output, log, status, error, measurement);
                attempts.Add(attempt);
                // An interrupted campaign still has its full plan plus durable per-attempt outcomes.
                await WriteNewAsync(Path.Combine(root, stem + "-attempt.json"), attempt);
                Console.WriteLine($"{attempts.Count}/{cases.Count * repetitions} {stem}: {status}");
            }
        int deterministicGroups = 0;
        string campaignStatus = "passed";
        try { deterministicGroups = ValidateComplete(cases, repetitions, attempts); }
        catch (InvalidDataException exception) { campaignStatus = "failed"; Console.Error.WriteLine(exception.Message); }
        var summaries = cases.Select(scenario => Summarize(scenario.Id, attempts)).Where(item => item is not null).Cast<ProfileSummary>().ToArray();
        var report = new ProfileReport("engine-profile-v1", sourceRevision, smoke, started, DateTimeOffset.UtcNow,
            affinity.ToString("X", CultureInfo.InvariantCulture), repetitions, cases, attempts, deterministicGroups, summaries, campaignStatus,
            new[] {
                "Source revision is supplied by the caller; reproduce from that committed source with a clean tracked worktree.",
                "CPU affinity, processor count and tiered compilation are pinned per owned child. Host background load, frequency and power policy are not controlled.",
                "Peak working set is each fresh process's lifetime high-water mark, including startup, setup and warmup; it is not measured-phase managed allocation.",
                "Managed allocation uses process-wide GC.GetTotalAllocatedBytes around measured work; observer instrumentation is included.",
                "Evaluator slot utilization includes simulated asynchronous waits (0/1/8 ms). It is not CPU utilization or a model-provider throughput forecast.",
                "Checkpoint store timing excludes serialization; compare paired checkpoint-on/off whole-run timing for inclusive overhead.",
                "Archive snapshot fixtures fill the declared cells; engine cell counts are capacity and observed occupancy is reported separately.",
                "One authored sphere task, one fixed seed and three host-local repetitions are diagnostic, not a release regression gate, optimizer-quality benchmark or competitor claim.",
                "Batch/continuous quality curves use the same budget and MaxInFlight=8; worker invariance is checked only within fixed dispatch semantics."
            });
        await WriteNewAsync(Path.Combine(root, "report.json"), report);
        return report;
    }

    public static int ValidateComplete(IReadOnlyList<ProfileCase> cases, int repetitions, IReadOnlyList<ProfileAttempt> attempts)
    {
        if (repetitions < 1 || cases.Count == 0 || cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count ||
            attempts.Count != cases.Count * repetitions) throw new InvalidDataException("Campaign evidence does not cover the entire declared plan.");
        foreach (var scenario in cases)
            for (int repetition = 0; repetition < repetitions; repetition++)
            {
                var matching = attempts.Where(item => item.CaseId == scenario.Id && item.Repetition == repetition).ToArray();
                if (matching.Length != 1 || matching[0].Status != "passed" || matching[0].Measurement is not { } result || result.Case != scenario)
                    throw new InvalidDataException("A planned attempt failed, was omitted, duplicated or substituted: " + scenario.Id);
                ProfileValidation.Validate(result);
            }
        var measurements = attempts.Select(item => item.Measurement!).ToArray();
        if (measurements.Select(item => item.Environment).Distinct().Count() != 1)
            throw new InvalidDataException("Runtime or machine controls changed across the campaign.");
        return ProfileValidation.ValidateDeterminism(measurements);
    }

    private static ProfileSummary? Summarize(string id, IEnumerable<ProfileAttempt> attempts)
    {
        var measurements = attempts.Where(item => item.CaseId == id && item.Status == "passed").Select(item => item.Measurement!).ToArray();
        if (measurements.Length == 0) return null;
        return new ProfileSummary(id, measurements.Length, Median(measurements.Select(item => item.OperationsPerSecond)),
            Median(measurements.Select(item => item.ElapsedMilliseconds)), Median(measurements.Select(item => (double)item.ManagedAllocatedBytes)),
            measurements.Max(item => item.ProcessLifetimePeakWorkingSetBytes),
            measurements.All(item => item.EvaluatorSlotUtilization.HasValue) ? Median(measurements.Select(item => item.EvaluatorSlotUtilization!.Value)) : null,
            measurements.All(item => item.BestQuality.HasValue) ? Median(measurements.Select(item => item.BestQuality!.Value)) : null,
            Median(measurements.Select(item => item.CheckpointStoreMilliseconds)));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length % 2 == 0 ? (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2 : sorted[sorted.Length / 2];
    }

    private static async Task RunChildAsync(string input, string output, string log, ulong affinity, ushort processorGroup)
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
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the owned profile worker.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(), stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        bool timedOut = false;
        try { await child.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            timedOut = true;
            if (!child.HasExited) child.Kill(entireProcessTree: true); // Only this owned child and its descendants.
            await child.WaitForExitAsync();
        }
        await using (var stream = new FileStream(log, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        await using (var writer = new StreamWriter(stream))
            await writer.WriteAsync($"exit={child.ExitCode}; timeout={timedOut}\nSTDOUT\n{await stdout}\nSTDERR\n{await stderr}");
        if (timedOut || child.ExitCode != 0) throw new InvalidOperationException($"Profile worker failed (exit {child.ExitCode}, timeout {timedOut}); see {log}.");
    }

    public static async Task WriteNewAsync<T>(string path, T value)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions);
    }
}
