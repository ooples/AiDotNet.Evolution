using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionMetricQueryTests
{
    [Fact]
    public void BestByRanksOnTheNamedMetricRatherThanQuality()
    {
        MapElitesArchive<TestGenome> archive = Archive(
            Entry(1, 10, Metrics(("accuracy", 0.5), ("latency", 90))),
            Entry(2, 90, Metrics(("accuracy", 0.1), ("latency", 10))));

        Assert.Equal(90, archive.Best?.Evaluation.Quality);
        Assert.Equal(0.5, archive.BestBy("accuracy")?.Evaluation.Metrics["accuracy"]);
        Assert.Equal(10,
            archive.BestBy("latency", EvolutionOptimizationDirection.Minimize)?.Evaluation.Metrics["latency"]);
    }

    [Fact]
    public void MissingMetricsAreAbsentRatherThanScoredAsZero()
    {
        MapElitesArchive<TestGenome> archive = Archive(
            Entry(1, 10, Metrics(("cost", 5))),
            Entry(2, 90, Metrics(("unrelated", 1))));

        EvolutionArchiveEntry<TestGenome>? cheapest =
            archive.BestBy("cost", EvolutionOptimizationDirection.Minimize);

        Assert.NotNull(cheapest);
        Assert.Equal(5, cheapest.Evaluation.Metrics["cost"]);
        Assert.Single(archive.WithMetric("cost"));
        Assert.Null(archive.BestBy("nonexistent"));
        Assert.Empty(archive.TopBy("nonexistent", 5));
    }

    [Fact]
    public void TopByIsBoundedStableAndBestFirst()
    {
        MapElitesArchive<TestGenome> archive = Archive(
            Entry(3, 3, Metrics(("accuracy", 0.5))),
            Entry(2, 2, Metrics(("accuracy", 0.9))),
            Entry(1, 1, Metrics(("accuracy", 0.5))));

        Assert.Equal(new[] { 0.9, 0.5 },
            archive.TopBy("accuracy", 2).Select(entry => entry.Evaluation.Metrics["accuracy"]));
        IReadOnlyList<string> tied = archive.TopBy("accuracy", 3)
            .Where(entry => entry.Evaluation.Metrics["accuracy"] == 0.5)
            .Select(entry => entry.Evaluation.GenomeId)
            .ToArray();
        Assert.Equal(tied.OrderBy(id => id, StringComparer.Ordinal), tied);
        Assert.Empty(archive.TopBy("accuracy", 0));
    }

    [Fact]
    public void MetricNamesIsTheSortedUnionOfReportedMetrics()
    {
        MapElitesArchive<TestGenome> archive = Archive(
            Entry(1, 1, Metrics(("recall", 1), ("accuracy", 1))),
            Entry(2, 2, Metrics(("latency", 1), ("accuracy", 1))));

        Assert.Equal(new[] { "accuracy", "latency", "recall" }, archive.MetricNames());
    }

    [Fact]
    public void RunQueriesCrossIslandsAndDeduplicateMigratedGenomes()
    {
        var first = Archive(Entry(1, 10, Metrics(("accuracy", 0.9))));
        var second = Archive(
            Entry(1, 10, Metrics(("accuracy", 0.9))),
            Entry(2, 20, Metrics(("accuracy", 0.4))));
        EvolutionRunResult<TestGenome> result = Run(first, second);

        Assert.Equal(0.9, result.BestBy("accuracy")?.Evaluation.Metrics["accuracy"]);
        IReadOnlyList<EvolutionArchiveEntry<TestGenome>> top = result.TopBy("accuracy", 2);
        Assert.Equal(2, top.Count);
        Assert.Equal(2, top.Select(entry => entry.Evaluation.GenomeId).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[] { "accuracy" }, result.MetricNames());
    }

    [Fact]
    public void InvalidArgumentsAndDirectionsAreRefused()
    {
        MapElitesArchive<TestGenome> archive = Archive(Entry(1, 1, Metrics(("accuracy", 0.5))));

        Assert.ThrowsAny<ArgumentException>(() => archive.BestBy("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => archive.TopBy("accuracy", -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            archive.BestBy("accuracy", (EvolutionOptimizationDirection)int.MaxValue));
        Assert.ThrowsAny<ArgumentException>(() => Entry(2, 1, Metrics(("score", double.NaN))));
    }

    private static Dictionary<string, double> Metrics(params (string Name, double Value)[] values)
    {
        var metrics = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string name, double value) in values) metrics[name] = value;
        return metrics;
    }

    private static MapElitesArchive<TestGenome> Archive(params (EvolutionCandidate<TestGenome> Candidate,
        EvolutionEvaluation Evaluation)[] entries)
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp)
        });

        foreach ((EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) in entries)
        {
            Assert.NotEqual(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(candidate, evaluation));
        }

        return archive;
    }

    private static (EvolutionCandidate<TestGenome>, EvolutionEvaluation) Entry(
        int value, double quality, IReadOnlyDictionary<string, double> metrics)
    {
        var genome = new TestGenome(value);
        string genomeId = "genome-" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<TestGenome>(
            value, new EvolutionCanonicalGenome<TestGenome>(genome, genomeId), lineage);
        var evaluation = new EvolutionEvaluation(
            value,
            genomeId,
            EvolutionEvaluationStatus.Completed,
            quality,
            EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = value * 10 },
            Array.Empty<double>(),
            Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            lineage,
            EvolutionCacheStatus.Miss,
            Array.Empty<EvolutionDiagnostic>(),
            "task-v1",
            "evaluator-v1",
            "config-v1",
            metrics);

        return (candidate, evaluation);
    }

    private static EvolutionRunResult<TestGenome> Run(params MapElitesArchive<TestGenome>[] islands) => new(
        EvolutionStopReason.EvaluationBudgetReached,
        islands,
        new EvolutionRunCounters(0, 0, 0, new Dictionary<EvolutionEvaluationStatus, long>()),
        "state-hash");
}
