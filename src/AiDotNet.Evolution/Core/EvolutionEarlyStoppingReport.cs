using System.Collections.ObjectModel;
using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>What the configured early-stopping criterion actually did during one run.</summary>
/// <remarks>
/// <para>
/// Every committed batch takes one reading of the criterion, and each reading is
/// <see cref="EvolutionEarlyStoppingOutcome.Improved"/>, <see cref="EvolutionEarlyStoppingOutcome.NotImproved"/> or
/// <see cref="EvolutionEarlyStoppingOutcome.Unmeasurable"/>. Only <c>NotImproved</c> charges patience, so this report
/// is how a caller sees after the fact that a criterion was skipped, how often, over how many evaluations, and why.
/// A criterion that was never measurable in a run that took at least one reading is a configuration mistake and ends
/// the run with an exception rather than a result, so a report on a returned result always satisfies
/// <see cref="WasEverMeasurable"/> when <see cref="Enabled"/> and <see cref="Readings"/> are set.
/// </para>
/// <para>
/// The counts describe this run only. A resumed run reports the readings it took itself; the checkpoint carries the
/// plateau counters, not this history. The report is deliberately excluded from the run's state hash for that reason.
/// </para>
/// <para><b>For Beginners:</b> If you asked the search to stop early when a number stops improving, this tells you
/// whether that number was ever actually available. <see cref="UnmeasurableReadings"/> greater than zero means the
/// search ran for a while with nothing to judge - usually early on, before the first result arrived.</para>
/// </remarks>
public sealed class EvolutionEarlyStoppingReport
{
    private readonly ReadOnlyDictionary<EvolutionEarlyStoppingUnmeasurableReason, long> _reasons;

    /// <summary>Initializes a report.</summary>
    /// <param name="enabled">Whether early stopping was configured with positive patience.</param>
    /// <param name="metric">The built-in criterion; meaningful only when <paramref name="metricName"/> is <c>null</c>.</param>
    /// <param name="metricName">The watched evaluator metric, or <c>null</c> when the built-in criterion was used.</param>
    /// <param name="improvedReadings">The non-negative number of readings that improved.</param>
    /// <param name="notImprovedReadings">The non-negative number of measured readings that did not improve.</param>
    /// <param name="unmeasurableReadings">The non-negative number of readings with nothing to measure.</param>
    /// <param name="unmeasurableEvaluations">The evaluations covered by unmeasurable readings, which charged no patience.</param>
    /// <param name="unmeasurableReasons">
    /// Counts per reason; copied defensively. The counts must sum to <paramref name="unmeasurableReadings"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative or <paramref name="metric"/> is undefined.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="metricName"/> is empty or whitespace, a reason is undefined, or the reason counts do not sum to
    /// <paramref name="unmeasurableReadings"/>.
    /// </exception>
    public EvolutionEarlyStoppingReport(bool enabled, EvolutionEarlyStoppingMetric metric, string? metricName,
        long improvedReadings, long notImprovedReadings, long unmeasurableReadings, long unmeasurableEvaluations,
        IReadOnlyDictionary<EvolutionEarlyStoppingUnmeasurableReason, long>? unmeasurableReasons = null)
    {
        if (!Enum.IsDefined(typeof(EvolutionEarlyStoppingMetric), metric)) throw new ArgumentOutOfRangeException(nameof(metric));
        if (metricName is not null && string.IsNullOrWhiteSpace(metricName))
            throw new ArgumentException("A watched metric name cannot be empty.", nameof(metricName));
        if (improvedReadings < 0) throw new ArgumentOutOfRangeException(nameof(improvedReadings));
        if (notImprovedReadings < 0) throw new ArgumentOutOfRangeException(nameof(notImprovedReadings));
        if (unmeasurableReadings < 0) throw new ArgumentOutOfRangeException(nameof(unmeasurableReadings));
        if (unmeasurableEvaluations < 0) throw new ArgumentOutOfRangeException(nameof(unmeasurableEvaluations));
        var reasons = new Dictionary<EvolutionEarlyStoppingUnmeasurableReason, long>();
        long counted = 0;
        if (unmeasurableReasons is not null)
        {
            foreach (KeyValuePair<EvolutionEarlyStoppingUnmeasurableReason, long> reason in unmeasurableReasons)
            {
                if (!Enum.IsDefined(typeof(EvolutionEarlyStoppingUnmeasurableReason), reason.Key))
                    throw new ArgumentException("An unmeasurable reason is undefined.", nameof(unmeasurableReasons));
                if (reason.Value < 0) throw new ArgumentException("Reason counts cannot be negative.", nameof(unmeasurableReasons));
                if (reason.Value == 0) continue;
                reasons[reason.Key] = reason.Value;
                counted = checked(counted + reason.Value);
            }
        }
        if (counted != unmeasurableReadings)
            throw new ArgumentException("Reason counts must account for every unmeasurable reading.", nameof(unmeasurableReasons));
        Enabled = enabled;
        Metric = metric;
        MetricName = metricName;
        ImprovedReadings = improvedReadings;
        NotImprovedReadings = notImprovedReadings;
        UnmeasurableReadings = unmeasurableReadings;
        UnmeasurableEvaluations = unmeasurableEvaluations;
        _reasons = new ReadOnlyDictionary<EvolutionEarlyStoppingUnmeasurableReason, long>(reasons);
    }

    /// <summary>Gets whether early stopping was configured with positive patience.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the built-in criterion; meaningful only when <see cref="MetricName"/> is <c>null</c>.</summary>
    public EvolutionEarlyStoppingMetric Metric { get; }

    /// <summary>Gets the watched evaluator metric, or <c>null</c> when the built-in criterion was used.</summary>
    public string? MetricName { get; }

    /// <summary>Gets a stable description of the criterion that was watched.</summary>
    public string Criterion => MetricName is null
        ? Metric.ToString()
        : string.Concat("metric:", MetricName);

    /// <summary>Gets how many readings improved the criterion by at least the configured minimum.</summary>
    public long ImprovedReadings { get; }

    /// <summary>Gets how many measured readings did not improve and therefore charged patience.</summary>
    public long NotImprovedReadings { get; }

    /// <summary>Gets how many readings had nothing to measure and therefore charged no patience.</summary>
    public long UnmeasurableReadings { get; }

    /// <summary>Gets how many committed evaluations were covered by unmeasurable readings.</summary>
    public long UnmeasurableEvaluations { get; }

    /// <summary>Gets how many readings produced a value.</summary>
    public long MeasuredReadings => ImprovedReadings + NotImprovedReadings;

    /// <summary>Gets the total number of readings taken.</summary>
    public long Readings => MeasuredReadings + UnmeasurableReadings;

    /// <summary>Gets whether the criterion produced a value at least once.</summary>
    public bool WasEverMeasurable => MeasuredReadings > 0;

    /// <summary>Gets how many unmeasurable readings each reason accounts for; reasons that never occurred are absent.</summary>
    public IReadOnlyDictionary<EvolutionEarlyStoppingUnmeasurableReason, long> UnmeasurableReasons => _reasons;

    /// <summary>Describes the recorded reasons in a stable, message-friendly form.</summary>
    internal string DescribeReasons()
    {
        if (_reasons.Count == 0) return "no readings were taken";
        return string.Join(", ", _reasons.OrderBy(reason => reason.Key)
            .Select(reason => string.Concat(reason.Key.ToString(), " x",
                reason.Value.ToString(CultureInfo.InvariantCulture))));
    }

    /// <summary>Creates the report of a run that did not configure early stopping.</summary>
    internal static EvolutionEarlyStoppingReport Disabled(EvolutionEarlyStoppingOptions options) =>
        new(false, options.Metric, options.MetricName, 0, 0, 0, 0);
}
