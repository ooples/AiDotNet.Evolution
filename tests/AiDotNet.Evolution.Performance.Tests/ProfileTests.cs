using System.Text.Json;
using AiDotNet.Evolution.Performance;
using Xunit;

namespace AiDotNet.Evolution.Performance.Tests;

public sealed class ProfileTests
{
    private static ProfileCase Case(int workers = 1) => new("engine-w" + workers, "engine", 32, 2, 1, workers, 8,
        false, false, EvolutionDispatchMode.Batch, 8, 42);

    private static ProfileEnvironment Environment() =>
        new("test-runtime", "test-os", "X64", "fixture-cpu", 4, "F", false, "0", "0", "0", "0",
            "cores=2;threadsPerCore=2;selected=core1[2+3]:cpu2", "scheme=fixture", 1, 15.625);

    private static ProfileMeasurement Valid(ProfileCase? scenario = null)
    {
        var item = scenario ?? Case();
        return new ProfileMeasurement(item, Environment(), 2, item.Budget, 2 * item.Budget, 200, 100,
            2 * item.Budget * 1000d / 200, 5, 16, 1024, 2048, 4096, 4096, 2048, 10, 1000, null, .1, 1, 2 * item.Budget,
            8, "stable-hash", -1, 0, 0, 0, Array.Empty<ProfileDelayBucket>(),
            Enumerable.Range(0, item.Budget).Select(i => new ProfileQualityPoint(i, i + 1, i * 10, -8 + i)).ToArray());
    }

    private static ProfileContention Contention(double foreign = 0.01) =>
        new("F", 4, 200, 8, 4, foreign, foreign > ProfileProtocol.ForeignCpuFlagFraction, "fixture");

    private static ProfileAttempt Attempt(ProfileMeasurement measurement, int repetition = 0) => new(measurement.Case.Id,
        repetition, "case.json", "measurement.json", "worker.log", "passed", null, Contention(), measurement);

    [Theory]
    [InlineData(true, 13)]
    [InlineData(false, 47)]
    public void SuiteCoversDeclaredFactorsWithoutDuplicates(bool smoke, int expected)
    {
        var cases = ProfileCase.Suite(smoke, 42);
        Assert.Equal(expected, cases.Count);
        Assert.Equal(expected, cases.Select(item => item.Id).Distinct().Count());
        Assert.All(cases, item => item.Validate());
        Assert.All(cases.Where(item => item.Dispatch == EvolutionDispatchMode.Continuous), item => Assert.Equal(8, item.MaxInFlight));
        Assert.Contains(cases, item => item.Variation == "mutation");
        Assert.True(cases.Where(item => item.Variation == "mutation").Select(item => item.Workers).Distinct().Count() >= 2);
        if (!smoke)
        {
            Assert.Contains(cases, item => item.Islands == 4);
            Assert.Contains(cases, item => item.Workers == 4);
            Assert.Contains(cases, item => item.Dimensions == 32);
            Assert.Contains(cases, item => item.Kind == "archive-snapshot" && item.Cells == 10000);
        }
    }

    [Fact]
    public void CasesCannotSetFactorsTheirKindIgnores()
    {
        var evaluation = new ProfileCase("evaluation-w1", "evaluation-only", 256, 8, 1, 1, 8, false, false, EvolutionDispatchMode.Batch, 8, 42);
        evaluation.Validate();
        Assert.Throws<ArgumentException>(() => (evaluation with { Islands = 4 }).Validate());
        Assert.Throws<ArgumentException>(() => (evaluation with { Checkpoint = true }).Validate());
        Assert.Throws<ArgumentException>(() => (evaluation with { Dispatch = EvolutionDispatchMode.Continuous }).Validate());
        var archive = new ProfileCase("archive-cells-100", "archive-snapshot", 100, 1, 1, 1, 8, false, false, EvolutionDispatchMode.Batch, 8, 42);
        archive.Validate();
        Assert.Throws<ArgumentException>(() => (archive with { Workers = 4 }).Validate());
        Assert.Throws<ArgumentException>(() => (archive with { MixedDuration = true }).Validate());
        var checkpoint = new ProfileCase("checkpoint-32", "checkpoint-restore", 256, 8, 1, 1, 32, false, true, EvolutionDispatchMode.Batch, 8, 42);
        checkpoint.Validate();
        Assert.Throws<ArgumentException>(() => (checkpoint with { Cells = 4096 }).Validate());
        Assert.Throws<ArgumentException>(() => (checkpoint with { Variation = "mutation" }).Validate());
        Assert.DoesNotContain("cells", checkpoint.AppliedFactors, StringComparison.Ordinal);
        Assert.Contains("cells", archive.AppliedFactors, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedDurationScheduleIsThreeDistinctDeclaredDelays()
    {
        Assert.Equal(new[] { 0, 1, 8 }, ProfileDelaySchedule.Milliseconds);
        Assert.Equal(3, ProfileDelaySchedule.Milliseconds.Distinct().Count());
        var observed = new HashSet<int>();
        for (double coordinate = -5; coordinate < 5; coordinate += 0.001) observed.Add(ProfileDelaySchedule.DelayMilliseconds(coordinate));
        Assert.Equal(new[] { 0, 1, 8 }, observed.Order().ToArray());
        Assert.Equal(ProfileDelaySchedule.Milliseconds[ProfileDelaySchedule.Bucket(-4.998)], ProfileDelaySchedule.DelayMilliseconds(-4.998));
    }

    [Fact]
    public void AffinitySelectionTakesOneLogicalProcessorPerPhysicalCoreAwayFromCpuZero()
    {
        ulong[] cores = [0x3, 0xC, 0x30, 0xC0, 0x300];
        Assert.Equal(0b1_0101_0100UL, ProfileHost.SelectAffinity(ulong.MaxValue, cores));
        Assert.Equal(0b0001_0100UL, ProfileHost.SelectAffinity(ulong.MaxValue, cores, 2));
        // Only two cores exist: CPU 0's core is used last, and never twice.
        Assert.Equal(0b0101UL, ProfileHost.SelectAffinity(0xF, [0x3, 0xC], 2));
        Assert.Equal(0b1111UL, ProfileHost.SelectAffinity(0xF, [0x3, 0xC], 4));
        // Unknown topology falls back to the permitted mask itself.
        Assert.Equal(0b1111UL, ProfileHost.SelectAffinity(0xF, Array.Empty<ulong>(), 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileHost.SelectAffinity(0, cores));
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileHost.SelectAffinity(0xF, cores, 5));
        Assert.Contains("threadsPerCore=2", ProfileHost.DescribeTopology(cores, 0b1_0101_0100UL), StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeControlsCheckRejectsEveryUnappliedControl()
    {
        var environment = Environment() with { AffinityHex = "154", LogicalProcessors = 4, ProcessorGroup = "0" };
        ProfileValidation.ValidateRuntimeControls(environment, 0x154, 0);
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment, 0x155, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { TieredCompilation = "1" }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { UseAllCpuGroups = null }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { AssignCpuGroups = "1" }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { LogicalProcessors = 8 }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { TimerResolutionMilliseconds = 15.625 }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { Cpu = "not-reported" }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { SmtTopology = " " }, 0x154, 0));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment with { PowerPolicy = "" }, 0x154, 0));
        if (OperatingSystem.IsWindows())
            Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateRuntimeControls(environment, 0x154, 1));
    }

    [Fact]
    public void MedianIsTheMiddleSampleNotTheFastestOne()
    {
        Assert.Equal(100, ProfileCampaign.Median([1000, 10, 100]));
        Assert.Equal(55, ProfileCampaign.Median([10, 100, 1000, 10]));
        Assert.Equal(7, ProfileCampaign.Median([7]));
        Assert.Throws<ArgumentException>(() => ProfileCampaign.Median(Array.Empty<double>()));
    }

    [Fact]
    public void SummaryKeepsRepetitionSpreadAndTheWorstPeakMemory()
    {
        var scenario = Case();
        var fast = Valid(scenario) with { ElapsedMilliseconds = 100, MillisecondsPerIteration = 50, OperationsPerSecond = 320, ProcessLifetimePeakWorkingSetBytes = 1000 };
        var middle = Valid(scenario) with { ElapsedMilliseconds = 150, MillisecondsPerIteration = 75, OperationsPerSecond = 213, ProcessLifetimePeakWorkingSetBytes = 3000 };
        var slow = Valid(scenario) with { ElapsedMilliseconds = 180, MillisecondsPerIteration = 90, OperationsPerSecond = 178, ProcessLifetimePeakWorkingSetBytes = 2000 };
        ProfileAttempt[] attempts = [Attempt(fast), Attempt(middle, 1), Attempt(slow, 2)];
        var summary = ProfileCampaign.Summarize(scenario, attempts)!;
        Assert.Equal(3, summary.Repetitions);
        Assert.Equal(213, summary.MedianOperationsPerSecond);
        Assert.Equal(178, summary.MinimumOperationsPerSecond);
        Assert.Equal(320, summary.MaximumOperationsPerSecond);
        Assert.Equal(3000, summary.MaximumLifetimePeakWorkingSetBytes);
        Assert.Equal(1.8, summary.ElapsedMaxMinRatio, 6);
        ProfileValidation.ValidateDispersion([summary]);
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateDispersion([summary with { ElapsedMaxMinRatio = 2.5 }]));
    }

    [Fact]
    public void PinnedCpuContentionIsMeasuredAndGated()
    {
        var before = new ProfileCpuLoadSample(1000, 4000, "fixture");
        var idle = ProfileCampaign.Contention(before, new ProfileCpuLoadSample(1100, 4400, "fixture"), 0xF, 100, 95)!;
        Assert.Equal(4, idle.PinnedProcessors);
        Assert.Equal(100, idle.PinnedBusyMilliseconds);
        Assert.Equal(0.0125, idle.ForeignBusyFraction, 6);
        Assert.False(idle.Flagged);
        ProfileValidation.ValidateContention(idle);
        var busy = ProfileCampaign.Contention(before, new ProfileCpuLoadSample(1300, 4400, "fixture"), 0xF, 100, 0)!;
        Assert.Equal(0.75, busy.ForeignBusyFraction, 6);
        Assert.True(busy.Flagged);
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateContention(busy));
        Assert.Null(ProfileCampaign.Contention(null, null, 0xF, 100, 0));
    }

    [Fact]
    public void CaseOrderIsASeededPermutationThatChangesEveryRepetition()
    {
        var first = ProfileCampaign.CaseOrder(47, 0, 4711);
        var second = ProfileCampaign.CaseOrder(47, 1, 4711);
        var third = ProfileCampaign.CaseOrder(47, 2, 4711);
        foreach (var order in new[] { first, second, third })
            Assert.Equal(Enumerable.Range(0, 47), order.Order());
        Assert.NotEqual(Enumerable.Range(0, 47).ToArray(), first.ToArray());
        Assert.NotEqual(first.ToArray(), second.ToArray());
        Assert.NotEqual(second.ToArray(), third.ToArray());
        Assert.Equal(first.ToArray(), ProfileCampaign.CaseOrder(47, 0, 4711).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => ProfileCampaign.CaseOrder(0, 0, 1));
    }

    [Theory]
    [InlineData("0.1.0-preview.1+255feb24369702a32ea9db7a3f8a0b7a847d2762", "255feb24369702a32ea9db7a3f8a0b7a847d2762")]
    [InlineData("0.1.0", null)]
    [InlineData("0.1.0+short", null)]
    [InlineData(null, null)]
    public void EmbeddedRevisionIsReadFromTheInformationalVersion(string? informational, string? expected) =>
        Assert.Equal(expected, ProfileRevision.RevisionFrom(informational));

    [Fact]
    public void FabricatedRevisionsCannotLabelAReport()
    {
        const string built = "0.1.0-preview.1+255feb24369702a32ea9db7a3f8a0b7a847d2762";
        ProfileRevision.EnsureMatches("255feb24369702a32ea9db7a3f8a0b7a847d2762", built);
        ProfileRevision.EnsureMatches("255FEB24369702A32EA9DB7A3F8A0B7A847D2762", built);
        Assert.Throws<ArgumentException>(() => ProfileRevision.EnsureMatches("0000000000000000000000000000000000000000", built));
        Assert.Throws<ArgumentException>(() => ProfileRevision.EnsureMatches("uncommitted", built));
        Assert.Throws<ArgumentException>(() => ProfileRevision.EnsureMatches("255feb24369702a32ea9db7a3f8a0b7a847d2762", "0.1.0"));
        Assert.NotEqual("unavailable", ProfileRevision.WorkingTree());
    }

    [Fact]
    public async Task InvalidOrUnbuiltRevisionIsRejectedBeforeCreatingOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "evolution-profile-invalid-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileCampaign.RunAsync(directory, "uncommitted", true));
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileCampaign.RunAsync(directory, new string('a', 40), true));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void SemanticKeyExcludesWorkersButIncludesCheckpointDrainScheduleAndOperator()
    {
        var scenario = Case();
        Assert.Equal(scenario.DeterminismKey, (scenario with { Id = "different", Workers = 4 }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Checkpoint = true }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { MaxInFlight = 16 }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Dispatch = EvolutionDispatchMode.Continuous }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Seed = 43 }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Islands = 2 }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Variation = "mutation" }).DeterminismKey);
    }

    [Fact]
    public void MeasurementSurvivesJsonRoundTripWithAllQualityPoints()
    {
        var measurement = Valid();
        var restored = JsonSerializer.Deserialize<ProfileMeasurement>(JsonSerializer.Serialize(measurement, ProfileCampaign.JsonOptions), ProfileCampaign.JsonOptions)!;
        ProfileValidation.Validate(restored);
        Assert.Equal(measurement.Case, restored.Case);
        Assert.Equal(measurement.Environment, restored.Environment);
        Assert.Equal(measurement.QualityByElapsed, restored.QualityByElapsed);
    }

    public static IEnumerable<object[]> CorruptMeasurements()
    {
        var value = Valid();
        yield return new object[] { value with { Operations = 0 } };
        yield return new object[] { value with { Operations = 7 } };
        yield return new object[] { value with { Iterations = 0 } };
        yield return new object[] { value with { Iterations = 3 } };
        yield return new object[] { value with { MillisecondsPerIteration = 1 } };
        yield return new object[] { value with { ElapsedMilliseconds = double.NaN } };
        yield return new object[] { value with { ElapsedMilliseconds = 0 } };
        // A single-shot sub-millisecond measurement is rejected instead of published.
        yield return new object[] { value with { ElapsedMilliseconds = 0.4, MillisecondsPerIteration = 0.2, OperationsPerSecond = 40000 } };
        yield return new object[] { value with { WarmupMilliseconds = 0 } };
        yield return new object[] { value with { WarmupOperations = 0 } };
        yield return new object[] { value with { OperationsPerSecond = double.PositiveInfinity } };
        yield return new object[] { value with { ManagedAllocatedBytes = -1 } };
        yield return new object[] { value with { ProcessLifetimePeakWorkingSetBytes = 0 } };
        yield return new object[] { value with { ProcessCpuMilliseconds = double.NaN } };
        // Four logical processors cannot accumulate more than four times the elapsed time plus one clock tick.
        yield return new object[] { value with { ProcessCpuMilliseconds = 4 * 200 + 20 } };
        yield return new object[] { value with { ProcessCpuFineMilliseconds = -1 } };
        yield return new object[] { value with { CheckpointStoreMilliseconds = double.NaN } };
        yield return new object[] { value with { CheckpointSaves = -1 } };
        yield return new object[] { value with { EvaluatorSlotUtilization = double.NaN } };
        yield return new object[] { value with { EvaluatorSlotUtilization = 1.1 } };
        yield return new object[] { value with { EvaluatorSlotUtilization = null } };
        yield return new object[] { value with { PeakConcurrentEvaluations = 2 } };
        yield return new object[] { value with { PeakConcurrentEvaluations = 0 } };
        yield return new object[] { value with { EvaluationCalls = 7 } };
        yield return new object[] { value with { StateHash = null } };
        yield return new object[] { value with { BestQuality = double.NaN } };
        yield return new object[] { value with { BestQuality = 0 } };
        yield return new object[] { value with { QualityByElapsed = Array.Empty<ProfileQualityPoint>() } };
        yield return new object[] { value with { OccupiedCells = 0 } };
        yield return new object[] { value with { Case = value.Case with { Checkpoint = true } } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { ElapsedMilliseconds = double.NaN } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { ElapsedMilliseconds = -1 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { ElapsedMilliseconds = 400 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { BestQuality = -100 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { Completed = 8 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { EvaluationId = 0 } : p).ToArray() } };
        yield return new object[] { value with { Case = value.Case with { Kind = "archive-snapshot", Cells = 256, Dimensions = 1 } } };
        // A cheap case must not claim simulated delays.
        yield return new object[] { value with { DelayBuckets = [new ProfileDelayBucket(1, 16, 1.4, 1.1, 2.0)] } };
    }

    [Theory]
    [MemberData(nameof(CorruptMeasurements))]
    public void InvalidEvidenceCannotPass(ProfileMeasurement measurement) => Assert.Throws<InvalidDataException>(() => ProfileValidation.Validate(measurement));

    public static IEnumerable<object[]> CorruptDelayEvidence()
    {
        var mixed = Valid(Case() with { MixedDuration = true });
        ProfileDelayBucket[] honest = [new(0, 6, 0, 0, 0), new(1, 5, 1.4, 1.05, 2.4), new(8, 5, 8.4, 8.1, 9.1)];
        yield return new object[] { mixed with { DelayBuckets = Array.Empty<ProfileDelayBucket>() } };
        // The Windows default 15.6 ms timer tick: the schedule says 1 and 8 ms.
        yield return new object[] { mixed with { DelayBuckets = [honest[0], honest[1] with { MeanMilliseconds = 15.6 }, honest[2] with { MeanMilliseconds = 15.6 }] } };
        yield return new object[] { mixed with { DelayBuckets = [honest[0], honest[1] with { MeanMilliseconds = 3.5 }, honest[2]] } };
        yield return new object[] { mixed with { DelayBuckets = [honest[0], honest[1] with { MeanMilliseconds = 0.2 }, honest[2]] } };
        // Delays collapsed onto a single bucket, or a bucket that was never observed.
        yield return new object[] { mixed with { DelayBuckets = [new ProfileDelayBucket(1, 16, 1.4, 1.1, 2.0)] } };
        yield return new object[] { mixed with { DelayBuckets = [honest[0], honest[1]] } };
        yield return new object[] { mixed with { DelayBuckets = [honest[0], honest[1], honest[2] with { Count = 1 }] } };
    }

    [Theory]
    [MemberData(nameof(CorruptDelayEvidence))]
    public void CollapsedOrUnobservedDelaysCannotPass(ProfileMeasurement measurement) =>
        Assert.Throws<InvalidDataException>(() => ProfileValidation.Validate(measurement));

    [Fact]
    public void HonestDelayEvidencePasses()
    {
        var mixed = Valid(Case() with { MixedDuration = true }) with
        {
            DelayBuckets = [new ProfileDelayBucket(0, 6, 0, 0, 0), new ProfileDelayBucket(1, 5, 1.42, 1.05, 2.6),
                new ProfileDelayBucket(8, 5, 8.41, 8.10, 9.30)]
        };
        ProfileValidation.Validate(mixed);
    }

    [Fact]
    public void CampaignMustAccountForEveryScheduledRepetitionIncludingFailures()
    {
        var first = Valid(Case(1)); var second = Valid(Case(2));
        ProfileCase[] cases = [first.Case, second.Case];
        ProfileAttempt[] attempts = [Attempt(first), Attempt(second)];
        Assert.Equal(1, ProfileCampaign.ValidateComplete(cases, 1, attempts));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 2, attempts));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], attempts[0]]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], attempts[1] with { Status = "failed" }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], attempts[1] with { Measurement = null }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], attempts[1] with { Contention = null }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], attempts[1] with { Contention = Contention(0.9) }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], Attempt(second with { StateHash = "different" })]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1,
            [attempts[0], Attempt(second with { Environment = second.Environment with { Runtime = "changed-runtime" } })]));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateDeterminism([first]));
    }

    [Fact]
    public void EvidenceSummaryKeepsEveryAttemptAndOnlyPublishedCurves()
    {
        var mixed = Case() with { Id = "engine-w1-Batch-mixed-cp0", MixedDuration = true };
        var cheap = Case() with { Id = "engine-w1-Batch-cheap-cp0" };
        var mixedMeasurement = Valid(mixed) with
        {
            DelayBuckets = [new ProfileDelayBucket(0, 6, 0, 0, 0), new ProfileDelayBucket(1, 5, 1.4, 1.05, 2.6), new ProfileDelayBucket(8, 5, 8.4, 8.1, 9.3)]
        };
        var report = Report([cheap, mixed], [Attempt(Valid(cheap)), Attempt(mixedMeasurement)]);
        var summary = ProfileEvidence.Compact(report, new ProfileEvidenceRaw("raw.json", 123, "abc", "https://example.invalid/raw.json"));
        Assert.Equal(2, summary.Attempts.Count);
        Assert.All(summary.Attempts, attempt => Assert.Equal("passed", attempt.Status));
        Assert.Contains(summary.Attempts, attempt => attempt.StateHash == "stable-hash" && attempt.ForeignCpuFraction is not null);
        Assert.Single(summary.MixedDispatchCurves);
        Assert.Equal(mixed.Id, summary.MixedDispatchCurves[0].CaseId);
        Assert.Equal(mixed.Budget, summary.MixedDispatchCurves[0].BestQuality.Count);
        Assert.True(ProfileEvidence.IsMixedDispatchCase(mixed));
        Assert.False(ProfileEvidence.IsMixedDispatchCase(cheap));
        string tables = ProfileEvidence.RenderTables(summary);
        Assert.Contains("Median ms to quality", tables, StringComparison.Ordinal);
        Assert.Contains(cheap.Id, tables, StringComparison.Ordinal);
        Assert.Contains("Batch / 1", tables, StringComparison.Ordinal);
        var restored = JsonSerializer.Deserialize<ProfileEvidenceSummary>(JsonSerializer.Serialize(summary, ProfileCampaign.JsonOptions), ProfileCampaign.JsonOptions)!;
        Assert.Equal(tables, ProfileEvidence.RenderTables(restored));
    }

    [Fact]
    public void PublishedCurveSlicesComeFromTheRetainedCurve()
    {
        var curve = new ProfileEvidenceCurve("case", 0, [10, 20, 600], [-100, -20, -5]);
        Assert.Equal(20, ProfileEvidence.MillisecondsToQuality(curve, -25));
        Assert.True(double.IsNaN(ProfileEvidence.MillisecondsToQuality(curve, 100)));
        Assert.Equal(-20, ProfileEvidence.QualityAt(curve, 500));
        Assert.Equal(-5, ProfileEvidence.QualityAt(curve, 600));
        Assert.True(double.IsNaN(ProfileEvidence.QualityAt(curve, 1)));
    }

    [Fact]
    public void FailedCampaignKeepsOnlyItsDivergingHashGroups()
    {
        string raw = """
        {
          "sourceRevision": "0bed72d64516f244ed59bbcea2c6816caf7bc9b7",
          "status": "failed",
          "attempts": [
            { "caseId": "engine-w1-cp0", "repetition": 0, "status": "passed",
              "measurement": { "case": { "kind": "engine", "cells": 256, "dimensions": 8, "islands": 1, "budget": 256, "mixedDuration": false, "dispatch": "Batch", "maxInFlight": 8, "seed": 4711 }, "stateHash": "aaa" } },
            { "caseId": "engine-w2-cp1", "repetition": 0, "status": "passed",
              "measurement": { "case": { "kind": "engine", "cells": 256, "dimensions": 8, "islands": 1, "budget": 256, "mixedDuration": false, "dispatch": "Batch", "maxInFlight": 8, "seed": 4711 }, "stateHash": "bbb" } },
            { "caseId": "archive-cells-100", "repetition": 0, "status": "failed", "measurement": null }
          ]
        }
        """;
        using var document = JsonDocument.Parse(raw);
        var failure = ProfileEvidence.CompactFailure(document, "checkpoint settings were not part of the fixed-semantics key", null);
        Assert.Equal("failed", failure.Status);
        Assert.Equal(3, failure.Attempts);
        Assert.Equal(2, failure.PassedAttempts);
        var divergence = Assert.Single(failure.Divergences);
        Assert.Equal(2, divergence.Groups.Count);
        Assert.Equal(["aaa", "bbb"], divergence.Groups.Select(group => group.StateHash));
        Assert.Equal("engine-w1-cp0-r0", divergence.Groups[0].Attempts[0]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    public void ArchiveDimensionalityDoesNotFakeOccupiedCellCount(int dimensions)
    {
        var fixture = new ArchiveBenchmarks { Cells = 100, Dimensions = dimensions };
        fixture.Setup();
        Assert.NotNull(fixture.Lookup()); Assert.NotNull(fixture.Sample());
        Assert.Equal(100, fixture.Snapshot().Count);
        Assert.Equal(dimensions, fixture.Lookup()!.Candidate.CanonicalGenome.Genome.Values.Count);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 8)]
    [InlineData(4, 1)]
    [InlineData(4, 8)]
    public async Task CheckpointFixtureRestoresRealStateAcrossIslandAndGenomeSizes(int islands, int dimensions)
    {
        var fixture = new CheckpointBenchmarks { Evaluations = 32, Islands = islands, Dimensions = dimensions };
        await fixture.Setup();
        Assert.Equal(await fixture.RestoreWithoutNewEvaluations(), await fixture.RestoreWithoutNewEvaluations());
        fixture.ChecksumEnvelope().Validate();
        Assert.NotNull(await fixture.LoadClone());
    }

    [Fact]
    public async Task MeasuredPhaseWarmsUp_Repeats_AndSubtractsItsOwnAllocation()
    {
        var scenario = Case() with { Id = "contract-cheap", Budget = 8 };
        long before = GC.GetTotalAllocatedBytes(precise: true);
        var measurement = await ProfileRunner.MeasureUnpinnedAsync(scenario);
        long after = GC.GetTotalAllocatedBytes(precise: true);
        Assert.True(measurement.WarmupOperations > 0, "the measured phase must be preceded by warm-up work");
        Assert.True(measurement.WarmupMilliseconds > 0);
        Assert.True(measurement.ElapsedMilliseconds >= ProfileProtocol.MinimumMeasuredMilliseconds);
        Assert.True(measurement.Iterations > 1, "a sub-100 ms case must repeat instead of reporting one shot");
        Assert.Equal(measurement.Iterations * scenario.Budget, measurement.EvaluationCalls);
        Assert.Equal(scenario.Budget, measurement.QualityByElapsed.Count);
        Assert.True(measurement.ManagedAllocatedBytes < after - before,
            "measured allocation must exclude everything allocated before the measured phase");
        Assert.Empty(measurement.DelayBuckets);
        Assert.False(string.IsNullOrEmpty(measurement.StateHash));
    }

    [Fact]
    public async Task MixedDurationDelaysAreObservedNotAssumed()
    {
        var scenario = Case() with { Id = "contract-mixed", Budget = 24, MixedDuration = true };
        var measurement = await ProfileRunner.MeasureUnpinnedAsync(scenario);
        Assert.Equal(ProfileDelaySchedule.Milliseconds, measurement.DelayBuckets.Select(bucket => bucket.RequestedMilliseconds).ToArray());
        Assert.Equal(measurement.EvaluationCalls, measurement.DelayBuckets.Sum(bucket => bucket.Count));
        foreach (var bucket in measurement.DelayBuckets.Where(bucket => bucket.RequestedMilliseconds > 0))
            Assert.InRange(bucket.MeanMilliseconds, bucket.RequestedMilliseconds - 0.25,
                bucket.RequestedMilliseconds + ProfileProtocol.DelayToleranceMilliseconds);
        Assert.InRange(measurement.Environment.TimerResolutionMilliseconds, 0, 1);
    }

    [Fact]
    public async Task RepeatedIterationsKeepTheSameDeterministicState()
    {
        var scenario = Case() with { Id = "contract-mutation", Budget = 8, Variation = "mutation" };
        var measurement = await ProfileRunner.MeasureUnpinnedAsync(scenario);
        Assert.True(measurement.Iterations > 1);
        Assert.False(string.IsNullOrEmpty(measurement.StateHash));
        var repeat = await ProfileRunner.MeasureUnpinnedAsync(scenario);
        Assert.Equal(measurement.StateHash, repeat.StateHash);
    }

    private static ProfileReport Report(IReadOnlyList<ProfileCase> cases, IReadOnlyList<ProfileAttempt> attempts) =>
        new("engine-profile-v2", new string('a', 40), "0.1.0+" + new string('a', 40), "clean", false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(3), "154", "cores=64", 1, cases, attempts, 1,
            cases.Select(item => ProfileCampaign.Summarize(item, attempts)).Where(item => item is not null).Cast<ProfileSummary>().ToArray(),
            "passed", ["fixture"]);
}
