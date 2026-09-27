// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Metrics/ProgramMetricIssue.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.Globalization;

namespace AiDotNet.Evolution.Programs.Metrics;

/// <summary>A bounded explanation of why a reported metric was not combined.</summary>
public sealed class ProgramMetricIssue
{
    /// <summary>The longest description carried by an issue, in characters.</summary>
    public const int MaxDescriptionLength = 256;

    /// <summary>Initializes an issue.</summary>
    /// <param name="metricName">The metric the issue concerns.</param>
    /// <param name="reason">Why the metric was set aside.</param>
    /// <param name="description">A short bounded note; longer text is truncated.</param>
    /// <exception cref="ArgumentNullException"><paramref name="metricName"/> or <paramref name="description"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="metricName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="reason"/> is not a defined enumeration value.</exception>
    public ProgramMetricIssue(string metricName, ProgramMetricIssueReason reason, string description)
    {
        ProgramGuard.NotNullOrWhiteSpace(metricName);
        ProgramGuard.NotNull(description);
        if (!Enum.IsDefined(typeof(ProgramMetricIssueReason), reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        MetricName = metricName.Trim();
        Reason = reason;
        Description = description.Length > MaxDescriptionLength
            ? description.Substring(0, MaxDescriptionLength)
            : description;
    }

    /// <summary>Gets the name of the metric the issue concerns.</summary>
    public string MetricName { get; }

    /// <summary>Gets why the metric was set aside.</summary>
    public ProgramMetricIssueReason Reason { get; }

    /// <summary>Gets a short bounded note describing the issue.</summary>
    public string Description { get; }

    /// <summary>Returns the metric name and reason.</summary>
    /// <returns>A short diagnostic label.</returns>
    public override string ToString() => MetricName + ": " + Reason.ToString();
}
