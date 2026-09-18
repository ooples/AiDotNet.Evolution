namespace AiDotNet.Evolution;

/// <summary>One reading of an early-stopping criterion: a measured value, or the reason there was nothing to measure.</summary>
/// <remarks>
/// This is the two-valued input the engine turns into a three-valued <see cref="EvolutionEarlyStoppingOutcome"/>:
/// a measured reading is <c>Improved</c> or <c>NotImproved</c> depending on the running best, and an unmeasurable one
/// is always <c>Unmeasurable</c>. Keeping the reason on the reading is what lets the run report afterwards why its
/// criterion was skipped instead of collapsing every such case into a null.
/// </remarks>
internal readonly struct EvolutionEarlyStoppingReading
{
    private EvolutionEarlyStoppingReading(bool measured, double value, EvolutionEarlyStoppingUnmeasurableReason reason)
    {
        IsMeasured = measured;
        Value = value;
        Reason = reason;
    }

    /// <summary>Creates a reading that produced a value.</summary>
    internal static EvolutionEarlyStoppingReading Measured(double value) =>
        new(true, value, EvolutionEarlyStoppingUnmeasurableReason.MetricNotReported);

    /// <summary>Creates a reading that had nothing to measure, for the stated reason.</summary>
    internal static EvolutionEarlyStoppingReading Unmeasurable(EvolutionEarlyStoppingUnmeasurableReason reason) =>
        new(false, 0, reason);

    /// <summary>Gets whether the criterion produced a value.</summary>
    internal bool IsMeasured { get; }

    /// <summary>Gets the measured value, normalized so that larger is better; meaningless when unmeasured.</summary>
    internal double Value { get; }

    /// <summary>Gets why there was nothing to measure; meaningful only when <see cref="IsMeasured"/> is false.</summary>
    internal EvolutionEarlyStoppingUnmeasurableReason Reason { get; }
}
