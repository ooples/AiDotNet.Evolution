using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// E-4: a champion must beat the reference by more than its measurement noise. The pilot's selection score on 80 rows
/// picked programs worse than the seed held-out in 3 of 6 cells; this rule keeps the seed unless a gain clears
/// k * sqrt(se^2 + se_reference^2).
/// </summary>
public sealed class EvolutionChampionSelectionTests
{
    private const string Metric = "quality_se";

    [Fact]
    public void The_best_entry_whose_gain_clears_the_combined_standard_error_is_chosen()
    {
        // Reference 1.0 with se 0.3; every entry se 0.4, so the combined se is 0.5 and k = 2 needs a gain over 1.0.
        EvolutionArchiveEntry<TestGenome>[] entries = { Entry(1, 1.9, 0.4), Entry(2, 2.2, 0.4), Entry(3, 2.1, 0.4) };

        Assert.Equal("g2", Choose(entries, 1.0, 0.3, 2)!.Evaluation.GenomeId);
        Assert.Null(Choose(entries, 1.0, 0.3, 2.5)); // 1.2 / 0.5 = 2.4 standard errors, short of 2.5
    }

    [Fact]
    public void A_gain_equal_to_the_threshold_is_not_enough()
    {
        // Gain 1.0 against a combined se of 0.5 is exactly 2 standard errors.
        Assert.Null(Choose(new[] { Entry(1, 2.0, 0.4) }, 1.0, 0.3, 2));
    }

    [Fact]
    public void A_noisier_entry_needs_a_larger_gain()
    {
        // Same quality, different noise: only the quieter one clears 2 standard errors.
        EvolutionArchiveEntry<TestGenome>[] entries = { Entry(1, 2.0, 1.0), Entry(2, 1.9, 0.1) };

        Assert.Equal("g2", Choose(entries, 1.0, 0.1, 2)!.Evaluation.GenomeId);
    }

    [Fact]
    public void An_entry_without_a_usable_standard_error_is_never_chosen()
    {
        EvolutionArchiveEntry<TestGenome>[] entries =
        {
            Entry(1, 9.0, null), Entry(2, 9.0, double.NaN), Entry(3, 9.0, -0.1), Entry(4, 1.5, 0.01)
        };

        Assert.Equal("g4", Choose(entries, 1.0, 0.01, 2)!.Evaluation.GenomeId);
    }

    [Fact]
    public void Gains_follow_a_minimizing_direction()
    {
        EvolutionArchiveEntry<TestGenome>[] entries =
        {
            Entry(1, 0.2, 0.1, EvolutionOptimizationDirection.Minimize),
            Entry(2, 1.8, 0.1, EvolutionOptimizationDirection.Minimize)
        };

        Assert.Equal("g1", Choose(entries, 1.0, 0.1, 2)!.Evaluation.GenomeId);
    }

    [Fact]
    public void The_choice_does_not_depend_on_the_order_entries_arrive_in()
    {
        EvolutionArchiveEntry<TestGenome>[] entries = Enumerable.Range(1, 12)
            .Select(index => Entry(index, 1.0 + (index * 7 % 12) * 0.1, 0.05 + (index % 3) * 0.05)).ToArray();
        string expected = Choose(entries, 1.0, 0.05, 2)!.Evaluation.GenomeId;

        for (int seed = 0; seed < 20; seed++)
        {
            var random = new Random(seed);
            Assert.Equal(expected, Choose(entries.OrderBy(_ => random.Next()).ToArray(), 1.0, 0.05, 2)!.Evaluation.GenomeId);
        }
    }

    [Fact]
    public void Invalid_arguments_are_refused()
    {
        EvolutionArchiveEntry<TestGenome>[] entries = { Entry(1, 2.0, 0.1) };

        Assert.Throws<ArgumentNullException>(() => EvolutionChampionSelection.SelectSignificant<TestGenome>(null!, 1, 0, Metric, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Choose(entries, double.NaN, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Choose(entries, 1, -1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Choose(entries, 1, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Choose(entries, 1, 0, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() => EvolutionChampionSelection.SelectSignificant(entries, 1, 0, " ", 1));
    }

    private static EvolutionArchiveEntry<TestGenome>? Choose(EvolutionArchiveEntry<TestGenome>[] entries, double reference,
        double referenceError, double standardErrors) =>
        EvolutionChampionSelection.SelectSignificant(entries, reference, referenceError, Metric, standardErrors);

    private static EvolutionArchiveEntry<TestGenome> Entry(int cell, double quality, double? standardError,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        string id = "g" + cell;
        var lineage = new EvolutionLineage(null, null, "test", null, 0, 0, (ulong)cell);
        var candidate = new EvolutionCandidate<TestGenome>(cell, new EvolutionCanonicalGenome<TestGenome>(new TestGenome(cell), id), lineage);
        var metrics = new Dictionary<string, double>();
        if (standardError is double error && !double.IsNaN(error)) metrics[Metric] = error;
        else if (standardError is double) metrics["other"] = 1; // NaN cannot be a metric; stands in for a missing one
        var evaluation = new EvolutionEvaluation(cell, id, EvolutionEvaluationStatus.Completed, quality, direction,
            new Dictionary<string, double> { ["x"] = cell + 0.5 }, Array.Empty<double>(), Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(),
            "task", "eval", "config", metrics);
        return new(new EvolutionCellKey(new[] { cell }), candidate, evaluation);
    }
}