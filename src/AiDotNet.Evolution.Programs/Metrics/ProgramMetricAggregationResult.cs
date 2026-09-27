// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Metrics/ProgramMetricAggregationResult.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;
using System.Collections.ObjectModel;

namespace AiDotNet.Evolution.Programs.Metrics;

/// <summary>A scalar score with its contributing metrics and discarded-value diagnostics.</summary>
public sealed class ProgramMetricAggregationResult
{
    private readonly ReadOnlyCollection<ProgramMetricIssue> _issues;
    private readonly ReadOnlyCollection<string> _contributingMetrics;

    /// <summary>Initializes an aggregation result.</summary>
    /// <param name="value">The scalar quality, which may be non-finite when the inputs were.</param>
    /// <param name="strategy">The rule that produced <paramref name="value"/>.</param>
    /// <param name="usedCombinedScore">Whether a preferred combined-score metric short-circuited the calculation.</param>
    /// <param name="contributingMetrics">The names of the metrics that were combined, in ordinal order.</param>
    /// <param name="issues">The metrics that were set aside, with a reason each.</param>
    /// <exception cref="ArgumentNullException"><paramref name="contributingMetrics"/> or <paramref name="issues"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="strategy"/> is not a defined enumeration value.</exception>
    /// <exception cref="ArgumentException">A contributing metric name is empty, or an issue is <c>null</c>.</exception>
    public ProgramMetricAggregationResult(
        double value,
        ProgramMetricAggregationStrategy strategy,
        bool usedCombinedScore,
        IEnumerable<string> contributingMetrics,
        IEnumerable<ProgramMetricIssue> issues)
    {
        ProgramGuard.NotNull(contributingMetrics);
        ProgramGuard.NotNull(issues);
        if (!Enum.IsDefined(typeof(ProgramMetricAggregationStrategy), strategy))
            throw new ArgumentOutOfRangeException(nameof(strategy));

        string[] contributing = contributingMetrics.ToArray();
        if (contributing.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Contributing metric names cannot be empty.", nameof(contributingMetrics));
        ProgramMetricIssue[] issueCopy = issues.ToArray();
        if (issueCopy.Any(item => item is null))
            throw new ArgumentException("Issues cannot contain null entries.", nameof(issues));

        Value = value;
        Strategy = strategy;
        UsedCombinedScore = usedCombinedScore;
        _contributingMetrics = Array.AsReadOnly(contributing);
        _issues = Array.AsReadOnly(issueCopy);
    }

    /// <summary>Gets the scalar quality.</summary>
    public double Value { get; }

    /// <summary>Gets whether <see cref="Value"/> is a finite number and therefore usable as an archive quality.</summary>
    public bool HasFiniteValue => !double.IsNaN(Value) && !double.IsInfinity(Value);

    /// <summary>Gets the rule that produced <see cref="Value"/>.</summary>
    public ProgramMetricAggregationStrategy Strategy { get; }

    /// <summary>Gets whether a preferred combined-score metric short-circuited the calculation.</summary>
    public bool UsedCombinedScore { get; }

    /// <summary>Gets the names of the metrics that were combined, in ordinal order.</summary>
    public IReadOnlyList<string> ContributingMetrics => _contributingMetrics;

    /// <summary>Gets the metrics that were set aside, with a reason for each.</summary>
    public IReadOnlyList<ProgramMetricIssue> Issues => _issues;

    /// <summary>Returns the value, the strategy, and how many metrics were used and set aside.</summary>
    /// <returns>A short diagnostic label that never echoes metric content.</returns>
    public override string ToString() =>
        "ProgramMetricAggregationResult(" + Value.ToString("R", CultureInfo.InvariantCulture) +
        ", " + Strategy.ToString() +
        ", used=" + _contributingMetrics.Count.ToString(CultureInfo.InvariantCulture) +
        ", issues=" + _issues.Count.ToString(CultureInfo.InvariantCulture) + ")";
}
