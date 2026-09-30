using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionSelectionPolicyTests
{
    [Fact]
    public void DoubleSelectionReturnsNullForAnEmptyArchive()
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 4, 4)
        });
        var policy = new DoubleEvolutionSelectionPolicy<TestGenome>();

        Assert.Null(policy.Select(archive, new StableRandom(3), inspirationCount: 3));
    }

    [Fact]
    public void DoubleSelectionUsesDistinctQualityRankedInspirations()
    {
        var archive = new MapElitesArchive<TestGenome>(new[]
        {
            new EvolutionDescriptorDefinition("x", 0, 4, 4)
        });
        for (int i = 0; i < 4; i++) MapElitesArchiveTests.Add(archive, i, $"g{i}", i, i + 0.1);
        var policy = new DoubleEvolutionSelectionPolicy<TestGenome>();

        EvolutionSelection<TestGenome> selection = Assert.IsType<EvolutionSelection<TestGenome>>(
            policy.Select(archive, new StableRandom(3), inspirationCount: 3));

        Assert.DoesNotContain(selection.Inspirations,
            entry => entry.Evaluation.GenomeId == selection.Parent.Evaluation.GenomeId);
        Assert.Equal(selection.Inspirations.Count,
            selection.Inspirations.Select(entry => entry.Evaluation.GenomeId).Distinct().Count());
        Assert.Equal(selection.Inspirations.OrderByDescending(entry => entry.Evaluation.Quality)
            .Select(entry => entry.Evaluation.GenomeId), selection.Inspirations.Select(entry => entry.Evaluation.GenomeId));
    }

    [Fact]
    public void CuriosityStateRoundTripsAndRewardsSuccessfulParents()
    {
        var first = new CuriosityEvolutionSelectionPolicy<TestGenome>();
        (_, EvolutionEvaluation evaluation) = MapElitesArchiveTests.Create(2, "child", 2, 0.2);
        var lineage = new EvolutionLineage(new[] { "parent" }, null, "mut", null, 1, 0, 2);
        var childEvaluation = new EvolutionEvaluation(2, "child", EvolutionEvaluationStatus.Completed, 2,
            EvolutionOptimizationDirection.Maximize, evaluation.Descriptors, Array.Empty<double>(), Array.Empty<double>(),
            evaluation.Cost, lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");

        first.Observe(childEvaluation, EvolutionArchiveInsertionResult.Inserted);
        string state = first.CaptureState();
        var restored = new CuriosityEvolutionSelectionPolicy<TestGenome>();
        // The engine checkpoints a learning policy through this interface.
        IOutcomeAwareEvolutionSelectionPolicy<TestGenome> outcomeAware = restored;
        outcomeAware.RestoreState(state);

        Assert.Equal(2.0, first.Scores["parent"]);
        Assert.Equal(first.Scores, restored.Scores);
        Assert.Equal(state, restored.CaptureState());
    }
    [Theory]
    [InlineData(0, false)]
    [InlineData(120, false)]
    [InlineData(0, true)]
    [InlineData(120, true)]
    public void UniformSelectionOnTheIndexedArchiveMatchesTheMaterialisedAlgorithmExactly(int capacity, bool sharedGenomes)
    {
        // The archive keeps an ordered view in step with inserts, replacements and evictions, and uniform selection draws
        // from it in O(k). Both must reproduce the original algorithm draw for draw, so every run's state is unchanged.
        var archive = new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 200, 200) },
            capacity: capacity);
        var policy = new UniformEvolutionSelectionPolicy<TestGenome>();
        var feed = new StableRandom(17);
        var added = new List<EvolutionArchiveEntry<TestGenome>>();
        for (int i = 0; i < 3000; i++)
        {
            // With shared genomes, some genome ids land in several cells, which selection must exclude together with
            // the parent; the indexed path has to fall back to the materialised one for those parents.
            string genome = sharedGenomes && i % 3 != 0 ? "shared" + (i % 5) : "g" + i;
            MapElitesArchiveTests.Add(archive, i, genome, feed.NextDouble(), feed.NextDouble() * 200);
            added.AddRange(archive.Entries.Where(entry => entry.Evaluation.EvaluationId == i));
            if (i % 7 != 0) continue;
            // Independent of the archive's ordered view: every entry it still holds, found through Get, in key order.
            EvolutionArchiveEntry<TestGenome>[] expectedEntries = added.Where(entry => ReferenceEquals(archive.Get(entry.Cell), entry))
                .OrderBy(entry => entry.Cell.StableKey, StringComparer.Ordinal).ToArray();
            // The count comes from the archive's cell dictionary, not the view, so a cell the view dropped is caught.
            Assert.Equal(archive.Count, expectedEntries.Length);
            Assert.Equal(expectedEntries, archive.Entries);
            foreach (int k in new[] { 0, 1, 3, 12 })
            {
                var seed = new StableRandom((ulong)i, (ulong)k);
                StableRandom actualRandom = StableRandom.Restore(seed.CaptureState());
                StableRandom expectedRandom = StableRandom.Restore(seed.CaptureState());
                EvolutionSelection<TestGenome>? actual = policy.Select(archive, actualRandom, k);
                (EvolutionArchiveEntry<TestGenome> parent, List<EvolutionArchiveEntry<TestGenome>> inspirations) =
                    ReferenceUniform(expectedEntries, expectedRandom, k);
                Assert.NotNull(actual);
                Assert.Same(parent, actual.Parent);
                Assert.Equal(inspirations, actual.Inspirations);
                Assert.Equal(expectedRandom.NextUInt64(), actualRandom.NextUInt64());
            }
        }
    }

    // The pre-V1-72 algorithm, kept as the oracle: sample the parent from Entries, then a Fisher-Yates prefix over
    // every other entry.
    private static (EvolutionArchiveEntry<TestGenome>, List<EvolutionArchiveEntry<TestGenome>>) ReferenceUniform(
        IReadOnlyList<EvolutionArchiveEntry<TestGenome>> entries, StableRandom random, int inspirationCount)
    {
        EvolutionArchiveEntry<TestGenome> parent = entries[random.NextInt(entries.Count)];
        EvolutionArchiveEntry<TestGenome>[] candidates = entries
            .Where(entry => entry.Evaluation.GenomeId != parent.Evaluation.GenomeId).ToArray();
        int take = Math.Min(inspirationCount, candidates.Length);
        var inspirations = new List<EvolutionArchiveEntry<TestGenome>>(take);
        for (int i = 0; i < take; i++)
        {
            int selected = random.NextInt(i, candidates.Length);
            (candidates[i], candidates[selected]) = (candidates[selected], candidates[i]);
            inspirations.Add(candidates[i]);
        }
        return (parent, inspirations);
    }
}