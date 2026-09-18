using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionArchiveSnapshotOptimizationTests
{
    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize)]
    [InlineData(EvolutionOptimizationDirection.Minimize)]
    public void OwnedSortPreservesSourceOrderDetachedEntriesAndBestTieBreak(EvolutionOptimizationDirection direction)
    {
        var entries = new[] { Entry(2, "z", 1, direction), Entry(10, "b", 1, direction), Entry(3, "a", 1, direction) };
        var source = new View(entries, direction);
        var snapshot = new EvolutionArchiveSnapshot<TestGenome>(source);
        Assert.Equal(new[] { "z", "b", "a" }, entries.Select(entry => entry.Evaluation.GenomeId));
        Assert.Equal(entries.OrderBy(entry => entry.Cell.StableKey, StringComparer.Ordinal), snapshot.Entries);
        Assert.Equal("a", snapshot.Best!.Evaluation.GenomeId);
        foreach (var entry in entries) Assert.Same(entry, snapshot.Get(entry.Cell));
        entries[0] = Entry(4, "replacement", 99, direction);
        Assert.Equal(3, snapshot.Count); Assert.Equal("a", snapshot.Best.Evaluation.GenomeId);
        Assert.NotNull(snapshot.Get(new EvolutionCellKey(new[] { 2 })));
        Assert.Null(snapshot.Get(new EvolutionCellKey(new[] { 4 })));
        var copy = new EvolutionArchiveSnapshot<TestGenome>(snapshot);
        Assert.Equal(snapshot.Entries, copy.Entries); Assert.Same(snapshot.Best, copy.Best);
    }

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize, "high")]
    [InlineData(EvolutionOptimizationDirection.Minimize, "low")]
    public void BestScanUsesDeclaredDirection(EvolutionOptimizationDirection direction, string expected)
    {
        var source = new View(new[] { Entry(1, "low", -2, direction), Entry(2, "high", 3, direction) }, direction);
        Assert.Equal(expected, new EvolutionArchiveSnapshot<TestGenome>(source).Best!.Evaluation.GenomeId);
    }

    [Fact]
    public void EmptyAndDuplicateCellContractsArePreserved()
    {
        var empty = new EvolutionArchiveSnapshot<TestGenome>(new View(Array.Empty<EvolutionArchiveEntry<TestGenome>>(), EvolutionOptimizationDirection.Maximize));
        Assert.Empty(empty.Entries); Assert.Null(empty.Best);
        var entry = Entry(1, "g", 1, EvolutionOptimizationDirection.Maximize);
        Assert.Throws<ArgumentException>(() => new EvolutionArchiveSnapshot<TestGenome>(new View(new[] { entry, entry }, entry.Evaluation.Direction)));
    }

    private static EvolutionArchiveEntry<TestGenome> Entry(int cell, string id, double quality, EvolutionOptimizationDirection direction)
    {
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, (ulong)cell);
        var candidate = new EvolutionCandidate<TestGenome>(cell, new EvolutionCanonicalGenome<TestGenome>(new TestGenome(cell), id), lineage);
        var evaluation = new EvolutionEvaluation(cell, id, EvolutionEvaluationStatus.Completed, quality, direction,
            new Dictionary<string, double> { ["x"] = cell + 0.5 }, Array.Empty<double>(), Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
        return new(new EvolutionCellKey(new[] { cell }), candidate, evaluation);
    }

    private sealed class View(EvolutionArchiveEntry<TestGenome>[] entries, EvolutionOptimizationDirection direction) : IEvolutionArchiveView<TestGenome>
    {
        public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors { get; } = new[] { new EvolutionDescriptorDefinition("x", 0, 20, 20) };
        public string DefinitionHash => "test-view";
        public EvolutionOptimizationDirection Direction => direction;
        public int Count => entries.Length;
        public long Version => 1;
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>> Entries => entries;
        public EvolutionArchiveEntry<TestGenome>? Best => null;
        public EvolutionArchiveEntry<TestGenome>? Get(EvolutionCellKey cell) => entries.FirstOrDefault(entry => entry.Cell.Equals(cell));
    }
}
