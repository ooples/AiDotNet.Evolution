using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Performance;

/// <summary>One committed per-attempt record: every scalar and the state hash, without the raw quality curve.</summary>
public sealed record ProfileEvidenceAttempt(string CaseId, int Repetition, string Status, string Kind, int Workers,
    EvolutionDispatchMode Dispatch, bool MixedDuration, bool Checkpoint, int Cells, int Dimensions, int Islands, int Budget,
    string Variation, long Iterations, long Operations, double ElapsedMilliseconds, double MillisecondsPerIteration,
    double OperationsPerSecond, double WarmupMilliseconds, long WarmupOperations, long ManagedAllocatedBytes, long ProcessLifetimePeakWorkingSetBytes,
    long PeakBeforeMeasurementBytes, double ProcessCpuMilliseconds, ulong? ProcessCpuCycles, double? EvaluatorSlotUtilization,
    int PeakConcurrentEvaluations, long EvaluationCalls, int OccupiedCells, string? StateHash, double? BestQuality,
    long CheckpointSaves, long CheckpointPayloadBytes, double CheckpointStoreMilliseconds,
    IReadOnlyList<ProfileDelayBucket> DelayBuckets, double? ForeignCpuFraction, string? Error);

/// <summary>Columnar quality-over-time curve, retained only where the README reports quality against elapsed time.</summary>
public sealed record ProfileEvidenceCurve(string CaseId, int Repetition, IReadOnlyList<double> ElapsedMilliseconds,
    IReadOnlyList<double> BestQuality);

/// <summary>Where the complete raw report lives, with the digest that identifies it.</summary>
public sealed record ProfileEvidenceRaw(string FileName, long Bytes, string Sha256, string? DownloadUrl);

/// <summary>The committed, regenerable summary of one campaign; the raw report is published separately.</summary>
public sealed record ProfileEvidenceSummary(string Protocol, string Schema, string SourceRevision, string AssemblyInformationalVersion,
    string WorkingTreeStatus, bool Smoke, DateTimeOffset StartedUtc, DateTimeOffset FinishedUtc, string AffinityHex, string PinnedTopology,
    int Repetitions, string Status, int DeterministicWorkerGroups, ProfileEnvironment Environment, IReadOnlyList<string> Limitations,
    IReadOnlyList<ProfileSummary> Summaries, IReadOnlyList<ProfileEvidenceAttempt> Attempts,
    IReadOnlyList<ProfileEvidenceCurve> MixedDispatchCurves, ProfileEvidenceRaw? Raw);

/// <summary>State hashes that disagreed inside one fixed-semantics group of a failed campaign.</summary>
public sealed record ProfileEvidenceHashGroup(string StateHash, IReadOnlyList<string> Attempts);

public sealed record ProfileEvidenceDivergence(string DeterminismKey, IReadOnlyList<ProfileEvidenceHashGroup> Groups);

/// <summary>The compact record of a campaign that failed its comparison, retained instead of its raw report.</summary>
public sealed record ProfileEvidenceFailure(string Schema, string SourceRevision, string Status, string Reason, int Attempts,
    int PassedAttempts, IReadOnlyList<ProfileEvidenceDivergence> Divergences, ProfileEvidenceRaw? Raw);

/// <summary>Builds the committed evidence summary and regenerates every published table from it.</summary>
public static class ProfileEvidence
{
    public const string Schema = "engine-profile-evidence-v1";

    /// <summary>Reduces a complete report to per-attempt scalars plus the curves the published tables actually use.</summary>
    public static ProfileEvidenceSummary Compact(ProfileReport report, ProfileEvidenceRaw? raw)
    {
        ArgumentNullException.ThrowIfNull(report);
        var attempts = report.Attempts.Select(attempt =>
        {
            var measurement = attempt.Measurement;
            var scenario = measurement?.Case ?? report.Cases.First(item => item.Id == attempt.CaseId);
            return new ProfileEvidenceAttempt(attempt.CaseId, attempt.Repetition, attempt.Status, scenario.Kind, scenario.Workers,
                scenario.Dispatch, scenario.MixedDuration, scenario.Checkpoint, scenario.Cells, scenario.Dimensions, scenario.Islands,
                scenario.Budget, scenario.Variation, measurement?.Iterations ?? 0, measurement?.Operations ?? 0,
                Round(measurement?.ElapsedMilliseconds ?? 0, 4), Round(measurement?.MillisecondsPerIteration ?? 0, 4),
                Round(measurement?.OperationsPerSecond ?? 0, 3), Round(measurement?.WarmupMilliseconds ?? 0, 3),
                measurement?.WarmupOperations ?? 0, measurement?.ManagedAllocatedBytes ?? 0, measurement?.ProcessLifetimePeakWorkingSetBytes ?? 0,
                measurement?.PeakBeforeMeasurementBytes ?? 0, Round(measurement?.ProcessCpuMilliseconds ?? 0, 4),
                measurement?.ProcessCpuCycles, measurement?.EvaluatorSlotUtilization is { } utilization ? Round(utilization, 6) : null,
                measurement?.PeakConcurrentEvaluations ?? 0, measurement?.EvaluationCalls ?? 0, measurement?.OccupiedCells ?? 0,
                measurement?.StateHash, measurement?.BestQuality, measurement?.CheckpointSaves ?? 0, measurement?.CheckpointPayloadBytes ?? 0,
                Round(measurement?.CheckpointStoreMilliseconds ?? 0, 4),
                (measurement?.DelayBuckets ?? Array.Empty<ProfileDelayBucket>())
                    .Select(bucket => bucket with { MeanMilliseconds = Round(bucket.MeanMilliseconds, 4), MinimumMilliseconds = Round(bucket.MinimumMilliseconds, 4), MaximumMilliseconds = Round(bucket.MaximumMilliseconds, 4) }).ToArray(),
                attempt.Contention is { } contention ? Round(contention.ForeignBusyFraction, 6) : null,
                attempt.Error is null ? null : Truncate(attempt.Error, 2000));
        }).ToArray();
        var curves = report.Attempts
            .Where(attempt => attempt.Measurement is { } measurement && IsMixedDispatchCase(measurement.Case) && measurement.QualityByElapsed.Count > 0)
            .Select(attempt => new ProfileEvidenceCurve(attempt.CaseId, attempt.Repetition,
                attempt.Measurement!.QualityByElapsed.Select(point => Round(point.ElapsedMilliseconds, 3)).ToArray(),
                attempt.Measurement!.QualityByElapsed.Select(point => Round(point.BestQuality, 6)).ToArray()))
            .ToArray();
        var environment = report.Attempts.Select(attempt => attempt.Measurement?.Environment).OfType<ProfileEnvironment>().First();
        return new ProfileEvidenceSummary(report.Protocol, Schema, report.SourceRevision, report.AssemblyInformationalVersion,
            report.WorkingTreeStatus, report.Smoke, report.StartedUtc, report.FinishedUtc, report.AffinityHex, report.PinnedTopology,
            report.Repetitions, report.Status, report.DeterministicWorkerGroups, environment, report.Limitations, report.Summaries,
            attempts, curves, raw);
    }

    /// <summary>Cases whose quality-over-time curve is published: mixed durations, checkpoints off, both dispatchers.</summary>
    public static bool IsMixedDispatchCase(ProfileCase scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        return scenario.Kind == "engine" && scenario.MixedDuration && !scenario.Checkpoint &&
            scenario.Variation == ProfileProtocol.CanonicalVariation;
    }

    /// <summary>Reduces a failed campaign to the groups whose state hashes disagreed, reading its raw report defensively.</summary>
    public static ProfileEvidenceFailure CompactFailure(JsonDocument rawReport, string reason, ProfileEvidenceRaw? raw)
    {
        ArgumentNullException.ThrowIfNull(rawReport);
        var root = rawReport.RootElement;
        string revision = root.TryGetProperty("sourceRevision", out var revisionElement) ? revisionElement.GetString() ?? "unknown" : "unknown";
        string status = root.TryGetProperty("status", out var statusElement) ? statusElement.GetString() ?? "unknown" : "unknown";
        var groups = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        int attempts = 0, passed = 0;
        if (root.TryGetProperty("attempts", out var attemptsElement) && attemptsElement.ValueKind == JsonValueKind.Array)
            foreach (var attempt in attemptsElement.EnumerateArray())
            {
                attempts++;
                if (attempt.TryGetProperty("status", out var attemptStatus) && attemptStatus.GetString() == "passed") passed++;
                if (!attempt.TryGetProperty("measurement", out var measurement) || measurement.ValueKind != JsonValueKind.Object) continue;
                if (!measurement.TryGetProperty("case", out var scenario) || scenario.ValueKind != JsonValueKind.Object) continue;
                if (!scenario.TryGetProperty("kind", out var kind) || kind.GetString() != "engine") continue;
                string key = SemanticKey(scenario);
                string hash = measurement.TryGetProperty("stateHash", out var hashElement) ? hashElement.GetString() ?? "none" : "none";
                string id = (attempt.TryGetProperty("caseId", out var caseId) ? caseId.GetString() : null) + "-r" +
                    (attempt.TryGetProperty("repetition", out var repetition) ? repetition.GetInt32().ToString(CultureInfo.InvariantCulture) : "?");
                if (!groups.TryGetValue(key, out var byHash)) groups[key] = byHash = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                if (!byHash.TryGetValue(hash, out var ids)) byHash[hash] = ids = new List<string>();
                ids.Add(id);
            }
        var divergences = groups
            .Where(group => group.Value.Count > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new ProfileEvidenceDivergence(group.Key,
                group.Value.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new ProfileEvidenceHashGroup(entry.Key, entry.Value.OrderBy(id => id, StringComparer.Ordinal).ToArray()))
                    .ToArray()))
            .ToArray();
        return new ProfileEvidenceFailure(Schema, revision, status, reason, attempts, passed, divergences, raw);

        static string SemanticKey(JsonElement scenario)
        {
            string Text(string name) => scenario.TryGetProperty(name, out var value)
                ? value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.Number => value.GetRawText(),
                    _ => string.Empty
                }
                : string.Empty;
            return string.Join("|", Text("kind"), Text("cells"), Text("dimensions"), Text("islands"), Text("budget"),
                Text("mixedDuration"), Text("dispatch"), Text("maxInFlight"), Text("seed"));
        }
    }

    /// <summary>Computes the SHA-256 and size of a published raw report.</summary>
    public static ProfileEvidenceRaw Describe(string path, string? downloadUrl)
    {
        using var stream = File.OpenRead(path);
        byte[] digest = SHA256.HashData(stream);
        return new ProfileEvidenceRaw(Path.GetFileName(path), new FileInfo(path).Length, Convert.ToHexString(digest).ToLowerInvariant(), downloadUrl);
    }

    /// <summary>Regenerates both published tables from the committed summary alone.</summary>
    public static string RenderTables(ProfileEvidenceSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var builder = new StringBuilder();
        // Deliberately ASCII: these tables are rendered through a Windows console into a file comparison, and
        // non-ASCII glyphs there depend on the active code page rather than on the data.
        builder.Append("| Dispatch / workers | Median run ms | Median evaluator-slot utilization | Median ms to quality >= -25 | Median quality at 500 ms |\n");
        builder.Append("| --- | ---: | ---: | ---: | ---: |\n");
        foreach (var item in summary.Summaries.Where(item => item.Kind == "engine" && item.MixedDuration && !item.Checkpoint)
            .OrderBy(item => item.Workers).ThenBy(item => item.Dispatch))
        {
            var curves = summary.MixedDispatchCurves.Where(curve => curve.CaseId == item.CaseId).ToArray();
            string threshold = curves.Length == 0 ? "n/a"
                : Format(ProfileCampaign.Median(curves.Select(curve => MillisecondsToQuality(curve, -25))), 3);
            string atFiveHundred = curves.Length == 0 ? "n/a"
                : Format(ProfileCampaign.Median(curves.Select(curve => QualityAt(curve, 500))), 6);
            builder.Append(CultureInfo.InvariantCulture,
                $"| {item.Dispatch} / {item.Workers} | {Format(item.MedianMillisecondsPerIteration, 3)} | {item.MedianEvaluatorSlotUtilization * 100:F2}% | {threshold} | {atFiveHundred} |\n");
        }
        builder.Append("\n| Case | Operations/s | Iterations | Ms/iteration | Min-max ms/iteration | Managed KiB/op | Max lifetime peak MiB | Max foreign CPU |\n");
        builder.Append("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |\n");
        foreach (var item in summary.Summaries)
        {
            double minimumPerIteration = item.MinimumElapsedMilliseconds / Math.Max(1, item.MedianIterations);
            double maximumPerIteration = item.MaximumElapsedMilliseconds / Math.Max(1, item.MedianIterations);
            double kibPerOperation = item.MedianAllocatedBytes / Math.Max(1, item.MedianIterations * item.OperationsPerIteration) / 1024d;
            builder.Append(CultureInfo.InvariantCulture,
                $"| {item.CaseId} | {Format(item.MedianOperationsPerSecond, 2)} | {Format(item.MedianIterations, 0)} | {Format(item.MedianMillisecondsPerIteration, 3)} | {Format(minimumPerIteration, 3)}-{Format(maximumPerIteration, 3)} | {Format(kibPerOperation, 3)} | {Format(item.MaximumLifetimePeakWorkingSetBytes / 1048576d, 2)} | {item.MaximumForeignCpuFraction * 100:F2}% |\n");
        }
        return builder.ToString();
    }

    /// <summary>Elapsed milliseconds at which best-so-far quality first reached the threshold, or NaN when it never did.</summary>
    public static double MillisecondsToQuality(ProfileEvidenceCurve curve, double threshold)
    {
        ArgumentNullException.ThrowIfNull(curve);
        for (int index = 0; index < curve.BestQuality.Count; index++)
            if (curve.BestQuality[index] >= threshold) return curve.ElapsedMilliseconds[index];
        return double.NaN;
    }

    /// <summary>Best-so-far quality at an elapsed time, or NaN before the first completed evaluation.</summary>
    public static double QualityAt(ProfileEvidenceCurve curve, double elapsedMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(curve);
        double quality = double.NaN;
        for (int index = 0; index < curve.ElapsedMilliseconds.Count && curve.ElapsedMilliseconds[index] <= elapsedMilliseconds; index++)
            quality = curve.BestQuality[index];
        return quality;
    }

    private static string Format(double value, int decimals) =>
        double.IsFinite(value) ? value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : "n/a";

    private static double Round(double value, int digits) => double.IsFinite(value) ? Math.Round(value, digits, MidpointRounding.AwayFromZero) : value;

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
