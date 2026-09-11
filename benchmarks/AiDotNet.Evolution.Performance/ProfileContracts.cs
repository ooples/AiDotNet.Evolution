using System.Globalization;
using System.Numerics;

namespace AiDotNet.Evolution.Performance;

/// <summary>Declared, auditable constants of the isolated profiling protocol. Every gate below is stated, not implicit.</summary>
public static class ProfileProtocol
{
    /// <summary>Each measured phase repeats its case until at least this much wall time is measured.</summary>
    public const double MinimumMeasuredMilliseconds = 100;

    /// <summary>An upper bound on repetitions inside one measured phase; exceeding it fails instead of reporting a sub-resolution time.</summary>
    public const int MaximumIterations = 1_000_000;

    /// <summary>A simulated asynchronous delay may exceed its request by this much on average before the attempt fails.</summary>
    public const double DelayToleranceMilliseconds = 2.0;

    /// <summary>Foreign (non-worker) busy time on the pinned CPUs above this share of their capacity fails the attempt.</summary>
    public const double MaximumForeignCpuFraction = 0.25;

    /// <summary>Foreign busy time above this share is flagged in the report even when the attempt passes.</summary>
    public const double ForeignCpuFlagFraction = 0.05;

    /// <summary>Slowest-to-fastest elapsed ratio across repetitions of one case that fails the campaign.</summary>
    public const double MaximumElapsedMaxMinRatio = 2.0;

    /// <summary>Canonical values for factors a case kind does not apply, so an unused field can never imply a contrast.</summary>
    public const int CanonicalCells = 256, CanonicalIslands = 1, CanonicalWorkers = 1, CanonicalMaxInFlight = 8;

    /// <summary>Canonical proposal operator; cases that do not vary the operator must use it.</summary>
    public const string CanonicalVariation = "restart";
}

/// <summary>The declared genome-dependent delay schedule for mixed-duration cases, shared by the runner and its contracts.</summary>
public static class ProfileDelaySchedule
{
    /// <summary>Requested asynchronous delays in milliseconds, one per genome-derived bucket.</summary>
    public static IReadOnlyList<int> Milliseconds { get; } = new[] { 0, 1, 8 };

    /// <summary>Selects the bucket for a genome coordinate; deterministic and independent of worker count.</summary>
    public static int Bucket(double coordinate) => (int)((coordinate + 5) * 1000) % Milliseconds.Count;

    /// <summary>Requested delay for a genome coordinate.</summary>
    public static int DelayMilliseconds(double coordinate) => Milliseconds[Bucket(coordinate)];
}

/// <summary>A bounded, predeclared profiling case; capacities and actually occupied cells are reported separately.</summary>
public sealed record ProfileCase(string Id, string Kind, int Cells, int Dimensions, int Islands, int Workers,
    int Budget, bool MixedDuration, bool Checkpoint, EvolutionDispatchMode Dispatch, int MaxInFlight, ulong Seed,
    string Variation = ProfileProtocol.CanonicalVariation)
{
    /// <summary>Factors this case kind actually applies; every other factor is pinned to its canonical value.</summary>
    public string AppliedFactors => Kind switch
    {
        "engine" => "cells,dimensions,islands,workers,budget,mixedDuration,checkpoint,dispatch,maxInFlight,seed,variation",
        "evaluation-only" => "dimensions,workers,budget,mixedDuration,seed",
        "archive-snapshot" => "cells,dimensions",
        _ => "dimensions,islands,budget"
    };

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 120 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Case identifiers must be bounded ASCII letters, digits and hyphens.", nameof(Id));
        if (Kind is not ("engine" or "evaluation-only" or "archive-snapshot" or "checkpoint-restore"))
            throw new ArgumentException("Unknown profiling operation.", nameof(Kind));
        if (Variation is not (ProfileProtocol.CanonicalVariation or "mutation"))
            throw new ArgumentException("Unknown proposal operator.", nameof(Variation));
        if (Cells < 1 || Cells > 10000) throw new ArgumentOutOfRangeException(nameof(Cells), "Profile factors exceed the bounded protocol.");
        if (Dimensions < 1 || Dimensions > 32) throw new ArgumentOutOfRangeException(nameof(Dimensions), "Profile factors exceed the bounded protocol.");
        if (Islands < 1 || Islands > 4) throw new ArgumentOutOfRangeException(nameof(Islands), "Profile factors exceed the bounded protocol.");
        if (Workers < 1 || Workers > 4) throw new ArgumentOutOfRangeException(nameof(Workers), "Profile factors exceed the bounded protocol.");
        if (Budget < 8 || Budget > 4096) throw new ArgumentOutOfRangeException(nameof(Budget), "Profile factors exceed the bounded protocol.");
        if (MaxInFlight < 4 || MaxInFlight > 32) throw new ArgumentOutOfRangeException(nameof(MaxInFlight), "Profile factors exceed the bounded protocol.");
        if (!Enum.IsDefined(Dispatch)) throw new ArgumentOutOfRangeException(nameof(Dispatch), "Profile factors exceed the bounded protocol.");
        ValidateUnappliedFactors();
    }

    /// <summary>Every factor a kind ignores must hold its canonical value, so a recorded field never implies an untested contrast.</summary>
    private void ValidateUnappliedFactors()
    {
        bool canonicalDispatch = Dispatch == EvolutionDispatchMode.Batch && MaxInFlight == ProfileProtocol.CanonicalMaxInFlight;
        bool canonicalOperator = Variation == ProfileProtocol.CanonicalVariation;
        bool valid = Kind switch
        {
            "engine" => true,
            "evaluation-only" => Cells == ProfileProtocol.CanonicalCells && Islands == ProfileProtocol.CanonicalIslands &&
                !Checkpoint && canonicalDispatch && canonicalOperator,
            "archive-snapshot" => Islands == ProfileProtocol.CanonicalIslands && Workers == ProfileProtocol.CanonicalWorkers &&
                !Checkpoint && !MixedDuration && canonicalDispatch && canonicalOperator,
            _ => Cells == ProfileProtocol.CanonicalCells && Workers == ProfileProtocol.CanonicalWorkers && Checkpoint &&
                !MixedDuration && canonicalDispatch && canonicalOperator
        };
        if (!valid) throw new ArgumentException($"Case '{Id}' sets a factor its kind does not apply: {AppliedFactors} are applied.", nameof(Kind));
    }

    /// <summary>Identifies fixed search semantics, excluding worker count. Continuous checkpoint drains change proposal context.</summary>
    public string DeterminismKey => string.Join("|", Kind, Cells, Dimensions, Islands, Budget, MixedDuration, Checkpoint, Dispatch,
        MaxInFlight, Seed.ToString(CultureInfo.InvariantCulture), Variation);

    public static IReadOnlyList<ProfileCase> Suite(bool smoke, ulong seed)
    {
        var cases = new List<ProfileCase>();
        int budget = smoke ? 32 : 256;
        foreach (int workers in smoke ? new[] { 1, 2 } : new[] { 1, 2, 4 })
            foreach (var dispatch in new[] { EvolutionDispatchMode.Batch, EvolutionDispatchMode.Continuous })
                foreach (bool mixed in smoke ? new[] { true } : new[] { false, true })
                    foreach (bool checkpoint in new[] { false, true })
                        cases.Add(new ProfileCase($"engine-w{workers}-{dispatch}-{(mixed ? "mixed" : "cheap")}-cp{(checkpoint ? 1 : 0)}",
                            "engine", 256, 8, 1, workers, budget, mixed, checkpoint, dispatch, 8, seed));
        // A parent-dependent operator, so worker-count determinism is not established only for restart proposals.
        foreach (int workers in smoke ? new[] { 1, 2 } : new[] { 1, 2, 4 })
            cases.Add(new ProfileCase($"engine-mutation-w{workers}", "engine", 256, 8, 1, workers, budget, false, false,
                EvolutionDispatchMode.Batch, 8, seed, "mutation"));
        foreach (int workers in smoke ? new[] { 2 } : new[] { 1, 2, 4 })
            foreach (bool mixed in smoke ? new[] { true } : new[] { false, true })
                cases.Add(new ProfileCase($"evaluation-w{workers}-{(mixed ? "mixed" : "cheap")}", "evaluation-only", 256, 8, 1,
                    workers, budget, mixed, false, EvolutionDispatchMode.Batch, 8, seed));
        var baseline = new ProfileCase("baseline", "engine", 256, 8, 1, 1, budget, false, false, EvolutionDispatchMode.Batch, 8, seed);
        if (!smoke)
        {
            foreach (int cells in new[] { 32, 4096 }) cases.Add(baseline with { Id = "engine-cells-" + cells, Cells = cells });
            foreach (int dimensions in new[] { 2, 32 }) cases.Add(baseline with { Id = "engine-dimensions-" + dimensions, Dimensions = dimensions });
            foreach (int islands in new[] { 2, 4 }) cases.Add(baseline with { Id = "engine-islands-" + islands, Islands = islands });
        }
        foreach (int cells in smoke ? new[] { 32 } : new[] { 100, 1000, 10000 })
            cases.Add(baseline with { Id = "archive-cells-" + cells, Kind = "archive-snapshot", Cells = cells, Dimensions = 1 });
        if (!smoke)
            foreach (int dimensions in new[] { 8, 32 })
                cases.Add(baseline with { Id = "archive-dimensions-" + dimensions, Kind = "archive-snapshot", Cells = 1000, Dimensions = dimensions });
        foreach (int evaluations in smoke ? new[] { 32 } : new[] { 32, 256, 2048 })
            cases.Add(baseline with { Id = "checkpoint-evaluations-" + evaluations, Kind = "checkpoint-restore", Budget = evaluations, Checkpoint = true });
        foreach (var item in cases) item.Validate();
        if (cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
            throw new InvalidOperationException("The protocol repeats a case identifier.");
        return cases.AsReadOnly();
    }
}

public sealed record ProfileQualityPoint(long EvaluationId, long Completed, double ElapsedMilliseconds, double BestQuality);

/// <summary>Observed cost of one requested asynchronous delay bucket, so a collapsed OS timer cannot pass as the schedule.</summary>
public sealed record ProfileDelayBucket(int RequestedMilliseconds, long Count, double MeanMilliseconds,
    double MinimumMilliseconds, double MaximumMilliseconds);

/// <summary>Busy time on the pinned CPUs during one attempt, separating this worker from every other process.</summary>
public sealed record ProfileContention(string AffinityHex, int PinnedProcessors, double ElapsedMilliseconds,
    double PinnedBusyMilliseconds, double? WorkerCpuMilliseconds, double ForeignBusyFraction, bool Flagged, string Source);

public sealed record ProfileEnvironment(string Runtime, string OperatingSystem, string Architecture, string Cpu,
    int LogicalProcessors, string AffinityHex, bool ServerGc, string? TieredCompilation, string? UseAllCpuGroups, string? AssignCpuGroups,
    string ProcessorGroup, string SmtTopology, string PowerPolicy, double TimerResolutionMilliseconds, double ProcessorTimeResolutionMilliseconds);

/// <summary>Measured work only, with lifetime high-water memory explicitly separated from managed allocations.</summary>
public sealed record ProfileMeasurement(ProfileCase Case, ProfileEnvironment Environment, long Iterations,
    long OperationsPerIteration, long Operations, double ElapsedMilliseconds, double MillisecondsPerIteration,
    double OperationsPerSecond, double WarmupMilliseconds, long WarmupOperations, long ManagedAllocatedBytes,
    long WorkingSetBeforeBytes, long WorkingSetAfterBytes, long ProcessLifetimePeakWorkingSetBytes,
    long PeakBeforeMeasurementBytes, double ProcessCpuMilliseconds, ulong? ProcessCpuCycles, double? ProcessCpuFineMilliseconds,
    double? EvaluatorSlotUtilization, int PeakConcurrentEvaluations, long EvaluationCalls, int OccupiedCells, string? StateHash,
    double? BestQuality, long CheckpointSaves, long CheckpointPayloadBytes, double CheckpointStoreMilliseconds,
    IReadOnlyList<ProfileDelayBucket> DelayBuckets, IReadOnlyList<ProfileQualityPoint> QualityByElapsed);

public static class ProfileValidation
{
    /// <summary>Rejects missing work, nonsensical metrics and worker-dependent search state rather than dropping a case.</summary>
    public static void Validate(ProfileMeasurement measurement)
    {
        measurement.Case.Validate();
        if (measurement.Iterations <= 0 || measurement.OperationsPerIteration <= 0 ||
            measurement.Operations != measurement.Iterations * measurement.OperationsPerIteration ||
            !double.IsFinite(measurement.ElapsedMilliseconds) || measurement.ElapsedMilliseconds <= 0 ||
            !double.IsFinite(measurement.MillisecondsPerIteration) || measurement.MillisecondsPerIteration <= 0 ||
            !double.IsFinite(measurement.OperationsPerSecond) || measurement.OperationsPerSecond <= 0 || measurement.ManagedAllocatedBytes < 0 ||
            measurement.WorkingSetBeforeBytes < 0 || measurement.WorkingSetAfterBytes < 0 || measurement.ProcessLifetimePeakWorkingSetBytes <= 0 ||
            measurement.PeakBeforeMeasurementBytes < 0 ||
            !double.IsFinite(measurement.ProcessCpuMilliseconds) || measurement.ProcessCpuMilliseconds < 0 ||
            !double.IsFinite(measurement.CheckpointStoreMilliseconds) || measurement.CheckpointStoreMilliseconds < 0 ||
            measurement.CheckpointSaves < 0 || measurement.CheckpointPayloadBytes < 0 || measurement.EvaluationCalls < 0 || measurement.OccupiedCells < 0)
            throw new InvalidDataException("Profiling produced invalid measurement fields.");
        if (!double.IsFinite(measurement.WarmupMilliseconds) || measurement.WarmupMilliseconds <= 0 || measurement.WarmupOperations <= 0)
            throw new InvalidDataException("The measured phase must be preceded by a timed warm-up invocation that really executed work.");
        if (measurement.ElapsedMilliseconds < ProfileProtocol.MinimumMeasuredMilliseconds)
            throw new InvalidDataException("The measured phase is below the declared minimum measured time: single-shot sub-millisecond timing is not reported.");
        if (Math.Abs(measurement.MillisecondsPerIteration * measurement.Iterations - measurement.ElapsedMilliseconds) > 1e-6 * measurement.ElapsedMilliseconds + 1e-6)
            throw new InvalidDataException("Per-iteration time disagrees with the measured elapsed time.");
        ValidateProcessorTime(measurement);
        if (measurement.EvaluatorSlotUtilization is { } utilization && (!double.IsFinite(utilization) || utilization < 0 || utilization > 1.001))
            throw new InvalidDataException("Evaluator slot utilization is outside its physical bound.");
        if (measurement.PeakConcurrentEvaluations < 0 || measurement.PeakConcurrentEvaluations > measurement.Case.Workers)
            throw new InvalidDataException("Observed evaluator concurrency exceeded the declared worker bound.");
        ValidateDelays(measurement);
        if (measurement.Case.Kind is "engine" or "evaluation-only")
            if (measurement.EvaluationCalls != measurement.Case.Budget * measurement.Iterations ||
                measurement.OperationsPerIteration != measurement.Case.Budget ||
                measurement.EvaluatorSlotUtilization is null || measurement.PeakConcurrentEvaluations == 0)
                throw new InvalidDataException("The declared evaluation budget was not measured in full.");
        if (measurement.Case.Kind == "engine") ValidateEngineEvidence(measurement);
        if (measurement.Case.Kind == "archive-snapshot" && measurement.OccupiedCells != measurement.Case.Cells)
            throw new InvalidDataException("The archive fixture did not contain the declared occupied-cell count.");
    }

    /// <summary>Process CPU time cannot exceed its physical bound; the platform's coarse resolution is recorded, not assumed.</summary>
    private static void ValidateProcessorTime(ProfileMeasurement measurement)
    {
        double resolution = measurement.Environment.ProcessorTimeResolutionMilliseconds;
        if (!double.IsFinite(resolution) || resolution < 0) throw new InvalidDataException("Process CPU-time resolution was not recorded.");
        double bound = measurement.Environment.LogicalProcessors * measurement.ElapsedMilliseconds + resolution;
        if (measurement.ProcessCpuMilliseconds > bound)
            throw new InvalidDataException("Process CPU time exceeds processors times elapsed time plus the recorded clock resolution.");
        if (measurement.ProcessCpuFineMilliseconds is { } fine && (!double.IsFinite(fine) || fine < 0 || fine > bound))
            throw new InvalidDataException("Fine-grained process CPU time exceeds its physical bound.");
    }

    /// <summary>Requires the declared delay schedule to have been observed, not merely requested.</summary>
    private static void ValidateDelays(ProfileMeasurement measurement)
    {
        if (!measurement.Case.MixedDuration)
        {
            if (measurement.DelayBuckets.Count != 0) throw new InvalidDataException("A cheap case must not record simulated delays.");
            return;
        }
        int[] requested = measurement.DelayBuckets.Select(bucket => bucket.RequestedMilliseconds).ToArray();
        if (!requested.SequenceEqual(ProfileDelaySchedule.Milliseconds) || requested.Distinct().Count() != requested.Length)
            throw new InvalidDataException("Observed delay buckets do not match the declared mixed-duration schedule.");
        if (measurement.DelayBuckets.Sum(bucket => bucket.Count) != measurement.EvaluationCalls)
            throw new InvalidDataException("Delay accounting does not cover every measured evaluation.");
        foreach (var bucket in measurement.DelayBuckets)
        {
            if (bucket.Count <= 0 || !double.IsFinite(bucket.MeanMilliseconds) || bucket.MeanMilliseconds < 0 ||
                bucket.MinimumMilliseconds < 0 || bucket.MaximumMilliseconds < bucket.MinimumMilliseconds)
                throw new InvalidDataException("A declared delay bucket recorded no or impossible observations.");
            if (bucket.RequestedMilliseconds == 0) continue;
            if (bucket.MeanMilliseconds < bucket.RequestedMilliseconds - 0.25 ||
                bucket.MeanMilliseconds > bucket.RequestedMilliseconds + ProfileProtocol.DelayToleranceMilliseconds)
                throw new InvalidDataException(
                    $"Simulated {bucket.RequestedMilliseconds} ms delays actually took {bucket.MeanMilliseconds:F3} ms on average, " +
                    $"outside the declared +{ProfileProtocol.DelayToleranceMilliseconds} ms tolerance (raise the platform timer resolution).");
        }
    }

    private static void ValidateEngineEvidence(ProfileMeasurement measurement)
    {
        if (string.IsNullOrEmpty(measurement.StateHash) || measurement.BestQuality is not { } best || !double.IsFinite(best) ||
            measurement.QualityByElapsed.Count != measurement.Case.Budget ||
            measurement.OccupiedCells <= 0 || (measurement.Case.Checkpoint && measurement.CheckpointSaves == 0))
            throw new InvalidDataException("Engine profiling omitted quality, state, archive or checkpoint evidence.");
        double priorTime = 0, priorBest = double.NegativeInfinity;
        long completed = 0;
        var ids = new HashSet<long>();
        foreach (var point in measurement.QualityByElapsed)
        {
            if (!double.IsFinite(point.BestQuality) || !double.IsFinite(point.ElapsedMilliseconds) || point.ElapsedMilliseconds < priorTime ||
                point.Completed != ++completed || point.EvaluationId < 0 || !ids.Add(point.EvaluationId) || point.BestQuality < priorBest ||
                point.ElapsedMilliseconds > measurement.ElapsedMilliseconds + 1)
                throw new InvalidDataException("Quality-over-time evidence is inconsistent.");
            priorTime = point.ElapsedMilliseconds; priorBest = point.BestQuality;
        }
        if (priorBest != measurement.BestQuality) throw new InvalidDataException("Final quality disagrees with the measured quality curve.");
    }

    /// <summary>The declared per-child runtime controls, checked as one pure function so the check itself can be tested.</summary>
    public static void ValidateRuntimeControls(ProfileEnvironment environment, ulong affinity, ushort processorGroup)
    {
        string expected = affinity.ToString("X", CultureInfo.InvariantCulture);
        if (environment.AffinityHex != expected || environment.TieredCompilation != "0" || environment.UseAllCpuGroups != "0" ||
            environment.AssignCpuGroups != "0" || environment.LogicalProcessors != BitOperations.PopCount(affinity) ||
            (OperatingSystem.IsWindows() && environment.ProcessorGroup != processorGroup.ToString(CultureInfo.InvariantCulture)))
            throw new InvalidDataException("The worker did not apply the declared runtime controls.");
        if (!double.IsFinite(environment.TimerResolutionMilliseconds) || environment.TimerResolutionMilliseconds <= 0 ||
            environment.TimerResolutionMilliseconds > 1 + 1e-9)
            throw new InvalidDataException("The worker did not raise the platform timer resolution to one millisecond or finer.");
        if (string.IsNullOrWhiteSpace(environment.Cpu) || environment.Cpu == "not-reported" ||
            string.IsNullOrWhiteSpace(environment.SmtTopology) || string.IsNullOrWhiteSpace(environment.PowerPolicy))
            throw new InvalidDataException("The worker did not record CPU identity, SMT topology and power policy.");
    }

    /// <summary>Fails an attempt whose pinned CPUs were shared with other processes beyond the declared threshold.</summary>
    /// <remarks>The threshold is a parameter so the smoke run on a shared hosted runner can record and flag
    /// contention without failing, while the evidence campaign enforces <see cref="ProfileProtocol.MaximumForeignCpuFraction"/>.</remarks>
    public static void ValidateContention(ProfileContention contention, double maximumForeignFraction = ProfileProtocol.MaximumForeignCpuFraction)
    {
        ArgumentNullException.ThrowIfNull(contention);
        if (contention.PinnedProcessors <= 0 || !double.IsFinite(contention.ForeignBusyFraction) || contention.ForeignBusyFraction < 0)
            throw new InvalidDataException("Pinned-CPU contention was not measured for this attempt.");
        if (contention.ForeignBusyFraction > maximumForeignFraction)
            throw new InvalidDataException(
                $"Other processes used {contention.ForeignBusyFraction:P2} of the pinned CPUs, above the declared " +
                $"{maximumForeignFraction:P0} limit; the host was not idle enough to measure.");
    }

    /// <summary>Gates repeated attempts on their slowest-to-fastest spread instead of reporting a median alone.</summary>
    public static void ValidateDispersion(IEnumerable<ProfileSummary> summaries)
    {
        foreach (var summary in summaries)
        {
            if (summary.Repetitions < 2) continue;
            if (!double.IsFinite(summary.ElapsedMaxMinRatio) || summary.ElapsedMaxMinRatio < 1)
                throw new InvalidDataException("Repetition dispersion was not computed for " + summary.CaseId + ".");
            if (summary.ElapsedMaxMinRatio > ProfileProtocol.MaximumElapsedMaxMinRatio)
                throw new InvalidDataException($"Case {summary.CaseId} varied by {summary.ElapsedMaxMinRatio:F2}x across repetitions, " +
                    $"above the declared {ProfileProtocol.MaximumElapsedMaxMinRatio:F2}x limit.");
        }
    }

    public static int ValidateDeterminism(IEnumerable<ProfileMeasurement> measurements)
    {
        int groups = 0;
        foreach (var group in measurements.Where(item => item.Case.Kind == "engine").GroupBy(item => item.Case.DeterminismKey))
        {
            if (group.Select(item => item.Case.Workers).Distinct().Count() < 2) continue;
            groups++;
            if (group.Select(item => item.StateHash).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException("Worker count changed deterministic search state: " + group.Key);
        }
        if (groups == 0) throw new InvalidDataException("The campaign did not exercise any worker-count comparison.");
        return groups;
    }
}
