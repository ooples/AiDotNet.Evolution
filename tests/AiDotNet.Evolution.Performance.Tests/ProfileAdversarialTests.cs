using AiDotNet.Evolution.Performance;
using Xunit;

namespace AiDotNet.Evolution.Performance.Tests;

public sealed partial class ProfileTests
{
    [Fact]
    public void DerivedThroughputMustMatchMeasuredWork()
    {
        Assert.Throws<InvalidDataException>(() => ProfileValidation.Validate(Valid() with { OperationsPerSecond = 1 }));
        Assert.Throws<InvalidDataException>(() => ProfileValidation.Validate(Valid() with { Iterations = long.MaxValue }));
    }

    [Theory]
    [InlineData(double.NaN, 2)]
    [InlineData(1, double.PositiveInfinity)]
    [InlineData(2, 3)]
    [InlineData(0, 1)]
    public void DelayMeanMustLieInsideFiniteExtrema(double minimum, double maximum)
    {
        var measurement = Valid(Case() with { MixedDuration = true }) with
        {
            DelayBuckets = [new(0, 6, 0, 0, 0), new(1, 5, 1.4, minimum, maximum), new(8, 5, 8.4, 8.1, 9.1)]
        };
        Assert.Throws<InvalidDataException>(() => ProfileValidation.Validate(measurement));
    }

    [Fact]
    public void TablesNormalizeEachAttemptBeforeAggregating()
    {
        var scenario = Case();
        var first = Valid(scenario) with { Iterations = 1, Operations = 8, ElapsedMilliseconds = 300, MillisecondsPerIteration = 300, ManagedAllocatedBytes = 8192 };
        var second = Valid(scenario) with { Iterations = 2, Operations = 16, ElapsedMilliseconds = 400, MillisecondsPerIteration = 200, ManagedAllocatedBytes = 32768 };
        var third = Valid(scenario) with { Iterations = 4, Operations = 32, ElapsedMilliseconds = 600, MillisecondsPerIteration = 150, ManagedAllocatedBytes = 16384 };
        var report = Report([scenario], [Attempt(first), Attempt(second, 1), Attempt(third, 2)]);
        string tables = ProfileEvidence.RenderTables(ProfileEvidence.Compact(report, null));
        // Actual per-iteration range is150..300, median bytes/op1024 (not median bytes / median operations).
        Assert.Contains("150.000-300.000 | 1.000 |", tables, StringComparison.Ordinal);
    }

    [Fact]
    public void UnplannedUnjustifiedAndUnboundedContentionRetriesCannotPass()
    {
        var first = Attempt(Valid(Case(1))); var second = Attempt(Valid(Case(2)));
        ProfileCase[] cases = [first.Measurement!.Case, second.Measurement!.Case];
        var contended = second with { Status = "contended", Contention = Contention(0.9) };
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [first, second, contended with { CaseId = "unknown" }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [first, second, contended with { Repetition = 9 }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [first, second, contended with { Contention = Contention(0.01) }]));
        Assert.Throws<InvalidDataException>(() => ProfileCampaign.ValidateComplete(cases, 1, [first, second, contended, contended, contended]));
    }

    [Fact]
    public void ThreadSchedulerTimeIsNotReportedAsProcessCpu() => Assert.Null(ProfileHost.ProcessFineCpuMilliseconds());
}
