// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Enums/ProgramMetricAggregationStrategy.cs
// Original license retained in src/AiDotNet.Evolution.Programs/Legacy/AIDOTNET-LICENSE.txt.
using System.Globalization;
namespace AiDotNet.Evolution.Programs;

/// <summary>Standalone ProgramMetricAggregationStrategy.</summary>
public enum ProgramMetricAggregationStrategy
{
    /// <summary>Uses a metric named <c>combined_score</c> when present, and otherwise the mean of numeric metrics.</summary>
    /// <remarks>The reference OpenEvolve rule, reproduced including its handling of flags, text, and missing values.</remarks>
    CombinedScoreOrMean = 0,

    /// <summary>Always averages the numeric metrics, ignoring any metric named <c>combined_score</c>.</summary>
    /// <remarks>The same averaging rule as <see cref="CombinedScoreOrMean"/> without the preferred-key shortcut.</remarks>
    Mean = 1,

    /// <summary>Combines metrics as a weighted mean using explicitly declared, validated weights.</summary>
    /// <remarks>Larger values are better. Metrics with no declared weight are reported rather than silently dropped.</remarks>
    Weighted = 2,

    /// <summary>Scores the largest weighted shortfall from a declared reference point.</summary>
    /// <remarks>
    /// Smaller values are better. This is the weighted Chebyshev achievement scalarizing function, optionally
    /// augmented by a small multiple of the summed shortfalls to exclude weakly efficient solutions.
    /// </remarks>
    Tchebycheff = 3
}
