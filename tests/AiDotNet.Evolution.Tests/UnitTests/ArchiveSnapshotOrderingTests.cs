using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>Snapshots skip the sort when a view is already in cell-key order, and must still order a view that is not.</summary>
public sealed class ArchiveSnapshotOrderingTests
{
    private sealed class ReversedView : IEvolutionArchiveView<TestGenome>
    {
        private readonly MapElitesArchive<TestGenome> _inner;
        public ReversedView(MapElitesArchive<TestGenome> inner) => _inner = inner;
        public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors => _inner.Descriptors;
        public IReadOnlyList<EvolutionArchiveEntry<TestGenome>> Entries => Enumerable.Reverse(_inner.Entries).ToArray();
        public EvolutionArchiveEntry<TestGenome>? Best => _inner.Best;
        public int Count => _inner.Count;
        public long Version => _inner.Version;
        public string DefinitionHash => _inner.DefinitionHash;
        public EvolutionOptimizationDirection Direction => _inner.Direction;
        public EvolutionArchiveEntry<TestGenome>? Get(EvolutionCellKey cell) => _inner.Get(cell);
    }

    [Fact]
    public void Unsorted_views_are_ordered_and_sorted_views_are_kept_as_is()
    {
        var archive = new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 1000, 1000) });
        foreach (int i in new[] { 5, 900, 12, 400, 77 })
        {
            var (candidate, evaluation) = MapElitesArchiveTests.Create(i, "g" + i, i, i + 0.5);
            archive.TryAdd(candidate, evaluation);
        }
        string[] expected = archive.Entries.Select(e => e.Cell.StableKey).OrderBy(k => k, StringComparer.Ordinal).ToArray();

        var fromReversed = new EvolutionArchiveSnapshot<TestGenome>(new ReversedView(archive));
        var fromSorted = new EvolutionArchiveSnapshot<TestGenome>(archive);

        Assert.Equal(expected, fromReversed.Entries.Select(e => e.Cell.StableKey));
        Assert.Equal(expected, fromSorted.Entries.Select(e => e.Cell.StableKey));
        Assert.Equal(fromSorted.Best?.Evaluation.GenomeId, fromReversed.Best?.Evaluation.GenomeId);
    }
}
