using System.Text.Json;
using AiDotNet.Evolution.Performance;
using Xunit;

namespace AiDotNet.Evolution.Performance.Tests;

public sealed class ProfileTests
{
    private static ProfileCase Case(int workers = 1) => new("engine-w" + workers, "engine", 32, 2, 1, workers, 8,
        false, false, EvolutionDispatchMode.Batch, 8, 42);

    private static ProfileMeasurement Valid(ProfileCase? scenario = null) => new(scenario ?? Case(),
        new ProfileEnvironment("test-runtime", "test-os", "X64", "fixture-cpu", 4, "F", false, "0", "0", "0", "0"),
        8, 100, 80, 1024, 2048, 4096, 4096, 2048, 10, .1, 1, 8, 8, "stable-hash", -1,
        0, 0, 0, Enumerable.Range(0, 8).Select(i => new ProfileQualityPoint(i, i + 1, i * 10, -8 + i)).ToArray());

    private static ProfileAttempt Attempt(ProfileMeasurement measurement, int repetition = 0) => new(measurement.Case.Id,
        repetition, "case.json", "measurement.json", "worker.log", "passed", null, measurement);

    [Theory]
    [InlineData(true, 11)]
    [InlineData(false, 44)]
    public void SuiteCoversDeclaredFactorsWithoutDuplicates(bool smoke, int expected)
    {
        var cases = ProfileCase.Suite(smoke, 42);
        Assert.Equal(expected, cases.Count);
        Assert.Equal(expected, cases.Select(item => item.Id).Distinct().Count());
        Assert.All(cases, item => item.Validate());
        Assert.All(cases.Where(item => item.Dispatch == EvolutionDispatchMode.Continuous), item => Assert.Equal(8, item.MaxInFlight));
        if (!smoke)
        {
            Assert.Contains(cases, item => item.Islands == 4);
            Assert.Contains(cases, item => item.Workers == 4);
            Assert.Contains(cases, item => item.Dimensions == 32);
            Assert.Contains(cases, item => item.Kind == "archive-snapshot" && item.Cells == 10000);
        }
    }

    [Theory]
    [InlineData(0UL, 4, 0UL)]
    [InlineData(0b10101010UL, 2, 0b1010UL)]
    [InlineData(ulong.MaxValue, 4, 15UL)]
    [InlineData(1UL << 63, 4, 1UL << 63)]
    [InlineData(1UL, 1, 1UL)]
    public void AffinitySelectionStaysInsideAvailableCpuMask(ulong available, int maximum, ulong expected)
    {
        if (available == 0) Assert.Throws<ArgumentOutOfRangeException>(() => ProfileRunner.SelectAffinity(available, maximum));
        else Assert.Equal(expected, ProfileRunner.SelectAffinity(available, maximum));
    }

    [Fact]
    public void SemanticKeyExcludesOnlySchedulingConcurrencyAndCheckpointSideEffects()
    {
        var scenario = Case();
        Assert.Equal(scenario.DeterminismKey, (scenario with { Id = "different", Workers = 4, Checkpoint = true }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { MaxInFlight = 16 }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Dispatch = EvolutionDispatchMode.Continuous }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Seed = 43 }).DeterminismKey);
        Assert.NotEqual(scenario.DeterminismKey, (scenario with { Islands = 2 }).DeterminismKey);
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
        yield return new object[] { value with { ElapsedMilliseconds = double.NaN } };
        yield return new object[] { value with { ElapsedMilliseconds = 0 } };
        yield return new object[] { value with { OperationsPerSecond = double.PositiveInfinity } };
        yield return new object[] { value with { ManagedAllocatedBytes = -1 } };
        yield return new object[] { value with { ProcessLifetimePeakWorkingSetBytes = 0 } };
        yield return new object[] { value with { ProcessCpuMilliseconds = double.NaN } };
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
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { ElapsedMilliseconds = 200 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { BestQuality = -100 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { Completed = 8 } : p).ToArray() } };
        yield return new object[] { value with { QualityByElapsed = value.QualityByElapsed.Select((p, i) => i == 1 ? p with { EvaluationId = 0 } : p).ToArray() } };
        yield return new object[] { value with { Case = value.Case with { Kind = "archive-snapshot" } } };
    }

    [Theory]
    [MemberData(nameof(CorruptMeasurements))]
    public void InvalidEvidenceCannotPass(ProfileMeasurement measurement) => Assert.Throws<InvalidDataException>(() => ProfileValidation.Validate(measurement));

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
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [attempts[0], Attempt(second with { StateHash = "different" })]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1,
            [attempts[0], Attempt(second with { Environment = second.Environment with { Runtime = "changed-runtime" } })]));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.ValidateDeterminism([first]));
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
    public async Task InvalidRevisionIsRejectedBeforeCreatingOutput()
    {
        string directory = Path.Combine(Path.GetTempPath(), "evolution-profile-invalid-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<ArgumentException>(() => ProfileCampaign.RunAsync(directory, "uncommitted", true));
        Assert.False(Directory.Exists(directory));
    }
}
