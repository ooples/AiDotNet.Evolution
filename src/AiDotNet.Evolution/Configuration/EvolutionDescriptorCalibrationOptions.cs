namespace AiDotNet.Evolution;

/// <summary>Configures how descriptor bounds are derived from what a seed population measured.</summary>
/// <remarks>
/// Calibration reads deterministic bounds from fixed seeds. Later candidates outside the seeded span can widen the
/// grid in whole bins under <see cref="OutOfRangePolicy"/> rather than being silently collapsed or discarded.
/// </remarks>
public sealed class EvolutionDescriptorCalibrationOptions
{
    /// <summary>Gets or sets how many bins each calibrated axis contains.</summary>
    public int BinCount { get; set; } = 16;

    /// <summary>Gets or sets the fraction of the observed span added at each end.</summary>
    public double Padding { get; set; } = 0.25;

    /// <summary>Gets or sets the span used when every seed reported the same value.</summary>
    public double DegenerateSpan { get; set; } = 1.0;

    /// <summary>Gets or sets what happens to a value outside the calibrated bounds.</summary>
    public EvolutionOutOfRangePolicy OutOfRangePolicy { get; set; } = EvolutionOutOfRangePolicy.Grow;

    /// <summary>Creates an independent copy.</summary>
    public EvolutionDescriptorCalibrationOptions Clone() => new()
    {
        BinCount = BinCount,
        Padding = Padding,
        DegenerateSpan = DegenerateSpan,
        OutOfRangePolicy = OutOfRangePolicy
    };

    /// <summary>Validates all settings.</summary>
    public void Validate()
    {
        if (BinCount < 1 || BinCount > 100_000)
            throw new ArgumentOutOfRangeException(nameof(BinCount), BinCount, "Value must be between 1 and 100000.");
        if (double.IsNaN(Padding) || double.IsInfinity(Padding) || Padding < 0 || Padding > 100)
            throw new ArgumentOutOfRangeException(nameof(Padding), Padding, "Value must be between 0 and 100.");
        if (double.IsNaN(DegenerateSpan) || double.IsInfinity(DegenerateSpan) || DegenerateSpan <= 0)
            throw new ArgumentOutOfRangeException(nameof(DegenerateSpan), DegenerateSpan,
                "Value must be finite and positive.");
        if (!Enum.IsDefined(typeof(EvolutionOutOfRangePolicy), OutOfRangePolicy))
            throw new ArgumentOutOfRangeException(nameof(OutOfRangePolicy), OutOfRangePolicy,
                "Value must be a defined policy.");
    }
}
