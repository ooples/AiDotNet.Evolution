using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

/// <summary>
/// The early-stopping criterion is three-valued: only a measured non-improvement charges patience, an unmeasurable
/// reading is counted with its reason, and a criterion that was never measurable ends the run with an exception.
/// </summary>
public sealed class EvolutionEarlyStoppingCriterionTests
{
    private static MapElitesArchive<TestGenome> Archive() =>
        new(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 100) });

    private static EvolutionEngineOptions Options(EvolutionEarlyStoppingOptions stopping, int budget = 8) => new()
    {
        RunId = "early-stopping-criterion",
        Seed = 11,
        MaxEvaluationAttempts = budget,
        MaxProposals = 60,
        MaxGenerations = 60,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        MigrationInterval = 0,
        EarlyStopping = stopping
    };

    /// <summary>Completes every candidate but reports the watched metric only from a threshold value upwards.</summary>
    private sealed class MetricFromTask : IEvolutionTask<TestGenome>
    {
        private readonly int _reportFrom;
        public MetricFromTask(int reportFrom) => _reportFrom = reportFrom;
        public string Id => "metric-from";
        public string VersionHash => "metric-from-v1";
        public string EvaluatorVersionHash => "metric-from-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            var metrics = new Dictionary<string, double>();
            if (value >= _reportFrom) metrics["accuracy"] = value / 100d;
            return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, value,
                descriptors: new Dictionary<string, double> { ["x"] = Math.Max(0, Math.Min(100, value)) },
                metrics: metrics));
        }
    }

    /// <summary>Fails every candidate below a threshold, so nothing reaches the archive until one clears it.</summary>
    private sealed class FailBelowTask : IEvolutionTask<TestGenome>
    {
        private readonly int _completeFrom;
        public FailBelowTask(int completeFrom) => _completeFrom = completeFrom;
        public string Id => "fail-below";
        public string VersionHash => "fail-below-v1";
        public string EvaluatorVersionHash => "fail-below-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(genome, genome.Value.ToString(CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            return value < _completeFrom
                ? new(new EvolutionTaskResult(EvolutionEvaluationStatus.Failed,
                    diagnostics: new[] { new EvolutionDiagnostic("synthetic_failure", "below threshold") }))
                : new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, value,
                    descriptors: new Dictionary<string, double> { ["x"] = Math.Max(0, Math.Min(100, value)) }));
        }
    }

    /// <summary>An archive that claims no cells at all, which the archive geometry contract must refuse.</summary>
    private sealed class ZeroCellArchive : IEvolutionArchive<TestGenome>, IEvolutionArchiveCellCount
    {
        private readonly MapElitesArchive<TestGenome> _inner = Archive();
        public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors => _inner.Descriptors;
        public string DefinitionHash => "zero-cell-v1";
        public EvolutionOptimizationDirection Direction => _inner.Direction;
        public int Count => _inner.Count;
        public long Version => _inner.Version;
        public long TotalCells => 0;
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>> Entries => _inner.Entries;
        public EvolutionArchiveEntry<TestGenome>? Best => _inner.Best;
        public EvolutionArchiveEntry<TestGenome>? Get(EvolutionCellKey cell) => _inner.Get(cell);
        public EvolutionArchiveEntry<TestGenome>? Sample(StableRandom random) => _inner.Sample(random);
        public EvolutionArchiveInsertionResult TryAdd(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) =>
            _inner.TryAdd(candidate, evaluation);
    }

    [Fact]
    public async Task NamedMetricNobodyHasReportedYetIsUnmeasurableAndChargesNoPatience()
    {
        var options = Options(new EvolutionEarlyStoppingOptions
        {
            MetricName = "accuracy",
            PatienceEvaluations = 2
        });
        var run = await new EvolutionEngine<TestGenome>(new MetricFromTask(3), new SequentialVariation(),
            _ => Archive(), options).RunAsync(new[] { new TestGenome(1) });

        // Values 1 and 2 carry no "accuracy", so those readings measure nothing. Charging them would exhaust a
        // patience of 2 before the metric ever appeared.
        Assert.Equal(EvolutionStopReason.EvaluationBudgetReached, run.StopReason);
        EvolutionEarlyStoppingReport report = run.EarlyStopping;
        Assert.True(report.Enabled);
        Assert.Equal("metric:accuracy", report.Criterion);
        Assert.True(report.UnmeasurableReadings > 0);
        Assert.True(report.MeasuredReadings > 0);
        Assert.Equal(report.UnmeasurableReadings,
            report.UnmeasurableReasons[EvolutionEarlyStoppingUnmeasurableReason.MetricNotReported]);
        Assert.Single(report.UnmeasurableReasons);
        Assert.Equal(report.UnmeasurableReadings, report.UnmeasurableEvaluations);
        Assert.Equal(report.MeasuredReadings + report.UnmeasurableReadings, report.Readings);
        Assert.True(report.WasEverMeasurable);
    }

    [Fact]
    public async Task NamedMetricNoEvaluationEverReportsFailsTheRun()
    {
        var options = Options(new EvolutionEarlyStoppingOptions
        {
            MetricName = "accuracy",
            PatienceEvaluations = 2
        }, budget: 4);
        var engine = new EvolutionEngine<TestGenome>(new MetricFromTask(int.MaxValue), new SequentialVariation(),
            _ => Archive(), options);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.RunAsync(new[] { new TestGenome(1) }));
        Assert.Contains("metric:accuracy", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(EvolutionEarlyStoppingUnmeasurableReason.MetricNotReported), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QdScoreOnAnEmptyArchiveIsUnmeasurableUntilSomethingIsArchived()
    {
        var options = Options(new EvolutionEarlyStoppingOptions
        {
            Metric = EvolutionEarlyStoppingMetric.QdScore,
            PatienceEvaluations = 2,
            MinimumImprovement = 1e-9
        });
        var run = await new EvolutionEngine<TestGenome>(new FailBelowTask(5), new IncrementVariation(),
            _ => Archive(), options).RunAsync(new[] { new TestGenome(1), new TestGenome(2), new TestGenome(5) });

        Assert.NotEqual(EvolutionStopReason.EarlyStopped, run.StopReason);
        EvolutionEarlyStoppingReport report = run.EarlyStopping;
        Assert.Equal(EvolutionEarlyStoppingMetric.QdScore, report.Metric);
        Assert.Equal("QdScore", report.Criterion);
        Assert.True(report.UnmeasurableReadings > 0);
        Assert.Equal(report.UnmeasurableReadings,
            report.UnmeasurableReasons[EvolutionEarlyStoppingUnmeasurableReason.EmptyArchive]);
        Assert.True(report.WasEverMeasurable);
    }

    [Theory]
    [InlineData(EvolutionEarlyStoppingMetric.QdScore, "QdScore")]
    [InlineData(EvolutionEarlyStoppingMetric.BestQuality, "BestQuality")]
    public async Task ArchiveCriteriaThatNeverFillFailTheRun(EvolutionEarlyStoppingMetric metric, string criterion)
    {
        var options = Options(new EvolutionEarlyStoppingOptions { Metric = metric, PatienceEvaluations = 2 }, budget: 4);
        var engine = new EvolutionEngine<TestGenome>(new FailBelowTask(int.MaxValue), new IncrementVariation(),
            _ => Archive(), options);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.RunAsync(new[] { new TestGenome(1), new TestGenome(2) }));
        Assert.Contains(criterion, failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(EvolutionEarlyStoppingUnmeasurableReason.EmptyArchive), failure.Message, StringComparison.Ordinal);
        Assert.Contains("never measurable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageIsMeasurableFromItsFirstReadingAndZeroCellArchivesAreRefused()
    {
        var options = Options(new EvolutionEarlyStoppingOptions
        {
            Metric = EvolutionEarlyStoppingMetric.Coverage,
            PatienceEvaluations = 3,
            MinimumImprovement = 1e-9
        }, budget: 5);
        var run = await new EvolutionEngine<TestGenome>(new MetricFromTask(1), new SequentialVariation(),
            _ => Archive(), options).RunAsync(new[] { new TestGenome(1) });

        // Occupancy of a valid grid is always a real number - zero occupied cells is a measurement, not a gap - so
        // this criterion has no unmeasurable state to record.
        EvolutionEarlyStoppingReport report = run.EarlyStopping;
        Assert.Equal("Coverage", report.Criterion);
        Assert.Equal(0, report.UnmeasurableReadings);
        Assert.Empty(report.UnmeasurableReasons);
        Assert.True(report.WasEverMeasurable);

        // The only way occupancy could have no denominator is an archive that claims no cells, and the geometry
        // contract refuses that outright rather than letting a criterion read 0/0.
        var geometry = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new EvolutionEngine<TestGenome>(new MetricFromTask(1), new SequentialVariation(),
                _ => new ZeroCellArchive(), Options(new EvolutionEarlyStoppingOptions
                {
                    Metric = EvolutionEarlyStoppingMetric.Coverage,
                    PatienceEvaluations = 3
                }, budget: 3)).RunAsync(new[] { new TestGenome(1) }));
        Assert.Contains("cell count", geometry.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisabledEarlyStoppingReportsNothingAndNeverFailsTheRun()
    {
        var options = Options(new EvolutionEarlyStoppingOptions(), budget: 3);
        var run = await new EvolutionEngine<TestGenome>(new FailBelowTask(int.MaxValue), new IncrementVariation(),
            _ => Archive(), options).RunAsync(new[] { new TestGenome(1) });

        EvolutionEarlyStoppingReport report = run.EarlyStopping;
        Assert.False(report.Enabled);
        Assert.Equal(0, report.Readings);
        Assert.False(report.WasEverMeasurable);
        Assert.Empty(report.UnmeasurableReasons);
    }

    [Fact]
    public void ReportCountsMustAccountForEveryUnmeasurableReading()
    {
        Assert.Throws<ArgumentException>(() => new EvolutionEarlyStoppingReport(true,
            EvolutionEarlyStoppingMetric.BestQuality, null, 0, 0, 2, 2,
            new Dictionary<EvolutionEarlyStoppingUnmeasurableReason, long>
            {
                [EvolutionEarlyStoppingUnmeasurableReason.EmptyArchive] = 1
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionEarlyStoppingReport(true,
            EvolutionEarlyStoppingMetric.BestQuality, null, -1, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => new EvolutionEarlyStoppingReport(true,
            EvolutionEarlyStoppingMetric.BestQuality, "  ", 0, 0, 0, 0));

        var report = new EvolutionEarlyStoppingReport(true, EvolutionEarlyStoppingMetric.QdScore, null, 3, 2, 4, 9,
            new Dictionary<EvolutionEarlyStoppingUnmeasurableReason, long>
            {
                [EvolutionEarlyStoppingUnmeasurableReason.EmptyArchive] = 4,
                [EvolutionEarlyStoppingUnmeasurableReason.NoArchiveCells] = 0
            });
        Assert.Equal(5, report.MeasuredReadings);
        Assert.Equal(9, report.Readings);
        Assert.Equal(9, report.UnmeasurableEvaluations);
        Assert.Single(report.UnmeasurableReasons);
    }
}
