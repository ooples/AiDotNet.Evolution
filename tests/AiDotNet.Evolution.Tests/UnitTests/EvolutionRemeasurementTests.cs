using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionRemeasurementTests
{
    [Fact]
    public void RemeasurementPutsEveryEliteOnOneGrid()
    {
        MapElitesArchive<TestGenome> archive = Archive(
            Entry(1, 10, 10), Entry(2, 20, 30), Entry(3, 30, 50));

        int remaining = archive.Remeasure(genome => Values(genome.Value * 5.0));

        Assert.Equal(remaining, archive.Count);
        foreach (EvolutionArchiveEntry<TestGenome> entry in archive.Entries)
        {
            Assert.Equal(entry.Candidate.CanonicalGenome.Genome.Value * 5.0,
                entry.Evaluation.Descriptors["x"], 10);
            Assert.Equal(archive.TryCreateKey(entry.Evaluation.Descriptors)?.StableKey, entry.Cell.StableKey);
        }
    }

    [Fact]
    public void CollisionsKeepTheBetterEliteDeterministically()
    {
        MapElitesArchive<TestGenome> archive = Archive(Entry(1, 10, 10), Entry(2, 90, 50));

        archive.Remeasure(_ => Values(25));

        EvolutionArchiveEntry<TestGenome> survivor = Assert.Single(archive.Entries);
        Assert.Equal(90, survivor.Evaluation.Quality);
        Assert.Equal(90, archive.Best?.Evaluation.Quality);
    }

    [Fact]
    public void UnplaceableReplacementKeepsOldValuesButUsesFinalGrid()
    {
        MapElitesArchive<TestGenome> archive = Archive(Entry(1, 10, 10), Entry(2, 20, 30));
        string before = archive.Entries.Single(entry => entry.Evaluation.GenomeId == "genome-1").Cell.StableKey;

        archive.Remeasure(genome => genome.Value == 2 ? Values(-500) : null);

        EvolutionArchiveEntry<TestGenome> kept =
            archive.Entries.Single(entry => entry.Evaluation.GenomeId == "genome-1");
        Assert.Equal(10, kept.Evaluation.Descriptors["x"], 10);
        Assert.NotEqual(before, kept.Cell.StableKey);
        Assert.Equal(archive.TryCreateKey(kept.Evaluation.Descriptors)?.StableKey, kept.Cell.StableKey);
    }

    [Fact]
    public void ResultDoesNotDependOnInsertionOrder()
    {
        MapElitesArchive<TestGenome> forwards = Archive(
            Entry(1, 10, 10), Entry(2, 20, 30), Entry(3, 30, 50));
        MapElitesArchive<TestGenome> backwards = Archive(
            Entry(3, 30, 50), Entry(1, 10, 10), Entry(2, 20, 30));
        Func<TestGenome, IReadOnlyDictionary<string, double>?> reading = genome => Values(100 - genome.Value * 10);

        forwards.Remeasure(reading);
        backwards.Remeasure(reading);

        Assert.Equal(
            forwards.Entries.Select(entry => entry.Cell.StableKey + ":" + entry.Evaluation.GenomeId),
            backwards.Entries.Select(entry => entry.Cell.StableKey + ":" + entry.Evaluation.GenomeId));
    }

    [Fact]
    public void CallbackFailureLeavesTheArchiveCompletelyUnchanged()
    {
        MapElitesArchive<TestGenome> archive = Archive(Entry(1, 10, 10), Entry(2, 20, 30));
        long version = archive.Version;
        long gridCells = archive.TotalGridCells;
        string definition = string.Join("|", archive.Descriptors.Select(item => item.ToCanonicalString()));
        string[] entries = archive.Entries.Select(Identity).ToArray();
        string? best = archive.Best?.Evaluation.GenomeId;

        Assert.Throws<InvalidOperationException>(() => archive.Remeasure(genome =>
            genome.Value == 1 ? Values(-500) : throw new InvalidOperationException("measurement failed")));

        Assert.Equal(version, archive.Version);
        Assert.Equal(gridCells, archive.TotalGridCells);
        Assert.Equal(definition, string.Join("|", archive.Descriptors.Select(item => item.ToCanonicalString())));
        Assert.Equal(entries, archive.Entries.Select(Identity));
        Assert.Equal(best, archive.Best?.Evaluation.GenomeId);
    }

    [Fact]
    public void EmptyArchiveDoesNotInvokeTheCallbackOrChangeVersion()
    {
        MapElitesArchive<TestGenome> archive = Archive();

        Assert.Equal(0, archive.Remeasure(_ => throw new InvalidOperationException("should not be called")));
        Assert.Equal(0, archive.Version);
    }

    [Fact]
    public void EvaluationCopyChangesOnlyDescriptors()
    {
        (EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) = Entry(1, 10, 10);

        EvolutionEvaluation moved = evaluation.WithDescriptors(Values(40));

        Assert.Equal(evaluation.EvaluationId, moved.EvaluationId);
        Assert.Equal(evaluation.GenomeId, moved.GenomeId);
        Assert.Equal(evaluation.Quality, moved.Quality);
        Assert.Equal(evaluation.TaskVersionHash, moved.TaskVersionHash);
        Assert.Equal(evaluation.EvaluatorVersionHash, moved.EvaluatorVersionHash);
        Assert.Equal(evaluation.Cost.CostUnits, moved.Cost.CostUnits);
        Assert.Equal(40, moved.Descriptors["x"], 10);
        Assert.Equal(10, evaluation.Descriptors["x"], 10);
        Assert.Equal(candidate.EvaluationId, moved.EvaluationId);
    }

    private static string Identity(EvolutionArchiveEntry<TestGenome> entry) =>
        entry.Cell.StableKey + ":" + entry.Evaluation.GenomeId + ":" +
        entry.Evaluation.Descriptors["x"].ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static Dictionary<string, double> Values(double value) =>
        new(StringComparer.Ordinal) { ["x"] = value };

    private static MapElitesArchive<TestGenome> Archive(params (EvolutionCandidate<TestGenome> Candidate,
        EvolutionEvaluation Evaluation)[] entries)
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Grow)
        });
        foreach ((EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) in entries)
            Assert.NotEqual(EvolutionArchiveInsertionResult.Rejected, archive.TryAdd(candidate, evaluation));
        return archive;
    }

    private static (EvolutionCandidate<TestGenome>, EvolutionEvaluation) Entry(
        int value, double quality, double descriptor)
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
            Values(descriptor),
            Array.Empty<double>(),
            Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            lineage,
            EvolutionCacheStatus.Miss,
            Array.Empty<EvolutionDiagnostic>(),
            "task-v1",
            "evaluator-v1",
            "config-v1");

        return (candidate, evaluation);
    }
}
