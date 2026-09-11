using System.Globalization;

namespace AiDotNet.Evolution.Performance;

/// <summary>A bounded, predeclared profiling case; capacities and actually occupied cells are reported separately.</summary>
public sealed record ProfileCase(string Id, string Kind, int Cells, int Dimensions, int Islands, int Workers,
    int Budget, bool MixedDuration, bool Checkpoint, EvolutionDispatchMode Dispatch, int MaxInFlight, ulong Seed)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 120 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new ArgumentException("Case identifiers must be bounded ASCII letters, digits and hyphens.");
        if (Kind is not ("engine" or "evaluation-only" or "archive-snapshot" or "checkpoint-restore"))
            throw new ArgumentException("Unknown profiling operation.");
        if (Cells < 1 || Cells > 10000 || Dimensions < 1 || Dimensions > 32 || Islands < 1 || Islands > 4 ||
            Workers < 1 || Workers > 4 || Budget < 8 || Budget > 4096 || MaxInFlight < 4 || MaxInFlight > 32 ||
            !Enum.IsDefined(Dispatch)) throw new ArgumentOutOfRangeException(nameof(Cells), "Profile factors exceed the bounded protocol.");
    }

    /// <summary>Identifies fixed search semantics, excluding worker count. Continuous checkpoint drains change proposal context.</summary>
    public string DeterminismKey => string.Join("|", Kind, Cells, Dimensions, Islands, Budget, MixedDuration, Checkpoint, Dispatch,
        MaxInFlight, Seed.ToString(CultureInfo.InvariantCulture));

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

public sealed record ProfileEnvironment(string Runtime, string OperatingSystem, string Architecture, string Cpu,
    int LogicalProcessors, string AffinityHex, bool ServerGc, string? TieredCompilation, string? UseAllCpuGroups, string? AssignCpuGroups,
    string ProcessorGroup);

/// <summary>Measured work only, with lifetime high-water memory explicitly separated from managed allocations.</summary>
public sealed record ProfileMeasurement(ProfileCase Case, ProfileEnvironment Environment, long Operations,
    double ElapsedMilliseconds, double OperationsPerSecond, long ManagedAllocatedBytes,
    long WorkingSetBeforeBytes, long WorkingSetAfterBytes, long ProcessLifetimePeakWorkingSetBytes,
    long PeakBeforeMeasurementBytes, double ProcessCpuMilliseconds, double? EvaluatorSlotUtilization,
    int PeakConcurrentEvaluations, long EvaluationCalls, int OccupiedCells, string? StateHash, double? BestQuality,
    long CheckpointSaves, long CheckpointPayloadBytes, double CheckpointStoreMilliseconds,
    IReadOnlyList<ProfileQualityPoint> QualityByElapsed);

public static class ProfileValidation
{
    /// <summary>Rejects missing work, nonsensical metrics and worker-dependent search state rather than dropping a case.</summary>
    public static void Validate(ProfileMeasurement measurement)
    {
        measurement.Case.Validate();
        if (measurement.Operations <= 0 || !double.IsFinite(measurement.ElapsedMilliseconds) || measurement.ElapsedMilliseconds <= 0 ||
            !double.IsFinite(measurement.OperationsPerSecond) || measurement.OperationsPerSecond <= 0 || measurement.ManagedAllocatedBytes < 0 ||
            measurement.WorkingSetBeforeBytes < 0 || measurement.WorkingSetAfterBytes < 0 || measurement.ProcessLifetimePeakWorkingSetBytes <= 0 ||
            measurement.PeakBeforeMeasurementBytes < 0 ||
            !double.IsFinite(measurement.ProcessCpuMilliseconds) || measurement.ProcessCpuMilliseconds < 0 ||
            !double.IsFinite(measurement.CheckpointStoreMilliseconds) || measurement.CheckpointStoreMilliseconds < 0 ||
            measurement.CheckpointSaves < 0 || measurement.CheckpointPayloadBytes < 0 || measurement.EvaluationCalls < 0 || measurement.OccupiedCells < 0)
            throw new InvalidDataException("Profiling produced invalid measurement fields.");
        if (measurement.EvaluatorSlotUtilization is { } utilization && (!double.IsFinite(utilization) || utilization < 0 || utilization > 1.001))
            throw new InvalidDataException("Evaluator slot utilization is outside its physical bound.");
        if (measurement.PeakConcurrentEvaluations < 0 || measurement.PeakConcurrentEvaluations > measurement.Case.Workers)
            throw new InvalidDataException("Observed evaluator concurrency exceeded the declared worker bound.");
        if (measurement.Case.Kind is "engine" or "evaluation-only")
            if (measurement.EvaluationCalls != measurement.Case.Budget || measurement.Operations != measurement.Case.Budget ||
                measurement.EvaluatorSlotUtilization is null || measurement.PeakConcurrentEvaluations == 0)
                throw new InvalidDataException("The declared evaluation budget was not measured in full.");
        if (measurement.Case.Kind == "engine")
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
        if (measurement.Case.Kind == "archive-snapshot" && measurement.OccupiedCells != measurement.Case.Cells)
            throw new InvalidDataException("The archive fixture did not contain the declared occupied-cell count.");
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
