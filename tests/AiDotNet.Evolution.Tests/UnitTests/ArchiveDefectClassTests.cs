using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-55 regressions for the archive defect classes reproduced in OpenEvolve 0.3.2 (D1 NaN capture, D3 cell losers
/// kept as members, D4 drifting bins, D7 overridden bin counts). Each asserts our archive cannot exhibit it.
/// </summary>
public sealed class ArchiveDefectClassTests
{
    private static MapElitesArchive<TestGenome> Archive(int bins = 10) =>
        new(new[] { new EvolutionDescriptorDefinition("x", 0, 10, bins) });

    [Fact]
    public void D1_a_non_finite_quality_cannot_complete_so_it_can_never_hold_a_cell()
    {
        var descriptors = new Dictionary<string, double> { ["x"] = 5 };
        Assert.ThrowsAny<ArgumentException>(() => EvolutionTaskResult.Completed(double.NaN, descriptors));
        Assert.ThrowsAny<ArgumentException>(() => EvolutionTaskResult.Completed(double.PositiveInfinity, descriptors));
    }

    [Fact]
    public void D3_a_program_that_loses_its_cell_is_not_an_archive_member()
    {
        var archive = Archive();
        var (winner, winnerEvaluation) = MapElitesArchiveTests.Create(1, "winner", 1.0, 5);
        var (loser, loserEvaluation) = MapElitesArchiveTests.Create(2, "loser", 0.5, 5);
        Assert.Equal(EvolutionArchiveInsertionResult.Inserted, archive.TryAdd(winner, winnerEvaluation));
        Assert.Equal(EvolutionArchiveInsertionResult.NotImproved, archive.TryAdd(loser, loserEvaluation));
        Assert.Equal(new[] { "winner" }, archive.Entries.Select(entry => entry.Evaluation.GenomeId));
    }

    [Fact]
    public void D4_an_occupant_keeps_its_cell_when_later_values_arrive_outside_the_range()
    {
        var archive = Archive();
        var (first, firstEvaluation) = MapElitesArchiveTests.Create(1, "first", 1.0, 5);
        archive.TryAdd(first, firstEvaluation);
        string cell = archive.Entries.Single().Cell.StableKey;
        foreach (var (id, value) in new[] { (2L, 0.0), (3L, 10.0), (4L, 1000.0), (5L, -1000.0) })
        {
            var (candidate, evaluation) = MapElitesArchiveTests.Create(id, "g" + id, 0.1, value);
            archive.TryAdd(candidate, evaluation);
        }
        Assert.Equal(cell, archive.Entries.Single(entry => entry.Evaluation.GenomeId == "first").Cell.StableKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(37)]
    public void D7_the_grid_has_exactly_the_requested_bins(int bins)
    {
        Assert.Equal(bins, Archive(bins).TotalGridCells);
    }
}
