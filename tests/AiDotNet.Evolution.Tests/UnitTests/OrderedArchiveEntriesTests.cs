using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class OrderedArchiveEntriesTests
{
    [Fact]
    public void Random_inserts_removals_and_replacements_match_a_sorted_list_at_every_position()
    {
        // Enough operations to split chunks many times and to empty some; the reference is a plain sorted list.
        var random = new StableRandom(7UL, 3UL);
        var reference = new List<EvolutionArchiveEntry<TestGenome>>();
        var ordered = new OrderedArchiveEntries<TestGenome>(Array.Empty<EvolutionArchiveEntry<TestGenome>>());
        for (int step = 0; step < 6000; step++)
        {
            int cell = random.NextInt(3000);
            EvolutionArchiveEntry<TestGenome> entry = Entry(cell, step);
            int at = reference.FindIndex(e => e.Cell.StableKey == entry.Cell.StableKey);
            int action = random.NextInt(10);
            if (at < 0)
            {
                ordered.Insert(entry);
                int insertAt = reference.FindIndex(e => string.CompareOrdinal(e.Cell.StableKey, entry.Cell.StableKey) > 0);
                reference.Insert(insertAt < 0 ? reference.Count : insertAt, entry);
            }
            else if (action < 4)
            {
                ordered.Remove(entry.Cell.StableKey);
                reference.RemoveAt(at);
            }
            else
            {
                ordered.Replace(entry.Cell.StableKey, entry);
                reference[at] = entry;
            }

            if (step % 500 == 0 || step == 5999) AssertSame(reference, ordered);
        }
    }

    [Fact]
    public void A_missing_key_reports_where_it_would_be_inserted()
    {
        var ordered = new OrderedArchiveEntries<TestGenome>(new[] { Entry(10, 0), Entry(30, 0), Entry(20, 0) }
            .OrderBy(e => e.Cell.StableKey, StringComparer.Ordinal));
        Assert.Equal(1, ordered.IndexOf(Entry(20, 0).Cell.StableKey));
        int missing = ordered.IndexOf(Entry(25, 0).Cell.StableKey);
        Assert.True(missing < 0);
        Assert.Equal(2, ~missing);
        Assert.Throws<ArgumentOutOfRangeException>(() => ordered[3]);
    }

    private static void AssertSame(List<EvolutionArchiveEntry<TestGenome>> reference, OrderedArchiveEntries<TestGenome> ordered)
    {
        Assert.Equal(reference.Count, ordered.Count);
        for (int i = 0; i < reference.Count; i++)
        {
            Assert.Same(reference[i], ordered[i]);
            Assert.Equal(i, ordered.IndexOf(reference[i].Cell.StableKey));
        }

        Assert.Equal(reference, ordered.Items());
    }

    private static EvolutionArchiveEntry<TestGenome> Entry(int cell, int step)
    {
        (EvolutionCandidate<TestGenome> candidate, EvolutionEvaluation evaluation) = MapElitesArchiveTests.Create(step, "g" + step, step, 0.5);
        return new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { cell }), candidate, evaluation);
    }
}
