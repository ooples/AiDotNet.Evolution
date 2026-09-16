namespace AiDotNet.Evolution;

/// <summary>Immutable objective axis with a fixed admissible range and optional normalized tolerance bins.</summary>
/// <remarks>Values outside the declared range are rejected, not clipped. Tolerance is a bin width in
/// normalized loss space, anchored at the ideal bound. Zero uses exact comparison. Quantization is transitive,
/// unlike pairwise approximate equality. Hypervolume always uses unquantized normalized values.</remarks>
public sealed class EvolutionObjectiveDefinition
{
    /// <summary>Defines an axis. Bounds must be finite with a finite positive span; tolerance is zero or in [1e-12, 1].</summary>
    public EvolutionObjectiveDefinition(string name, EvolutionOptimizationDirection direction, double minimum, double maximum, double tolerance = 0)
    {
        Guard.NotNullOrWhiteSpace(name);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!EvolutionDescriptorDefinition.IsFinite(minimum) || !EvolutionDescriptorDefinition.IsFinite(maximum) ||
            !EvolutionDescriptorDefinition.IsFinite(maximum - minimum) || maximum <= minimum)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        if (!EvolutionDescriptorDefinition.IsFinite(tolerance) || tolerance < 0 || tolerance > 1 || (tolerance > 0 && tolerance < 1e-12))
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        Name = name.Trim(); Direction = direction; Minimum = minimum; Maximum = maximum; Tolerance = tolerance;
    }

    /// <summary>Gets the unique ordinal axis name.</summary>
    public string Name { get; }
    /// <summary>Gets the direction of improvement.</summary>
    public EvolutionOptimizationDirection Direction { get; }
    /// <summary>Gets the lower admissible bound.</summary>
    public double Minimum { get; }
    /// <summary>Gets the upper admissible bound.</summary>
    public double Maximum { get; }
    /// <summary>Gets the normalized comparison bin width; zero means exact comparison.</summary>
    public double Tolerance { get; }
    /// <summary>Converts a valid raw value to normalized loss in [0, 1]; smaller is better.</summary>
    public double Normalize(double value)
    {
        if (!EvolutionDescriptorDefinition.IsFinite(value) || value < Minimum || value > Maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "Objective value is outside its declared domain.");
        return Direction == EvolutionOptimizationDirection.Minimize
            ? (value - Minimum) / (Maximum - Minimum) : (Maximum - value) / (Maximum - Minimum);
    }
    internal double ComparisonValue(double value)
    {
        double loss = Normalize(value);
        return Tolerance == 0 ? (Direction == EvolutionOptimizationDirection.Minimize ? value : -value) : Math.Floor(loss / Tolerance);
    }
    internal string Canonical => EvolutionHash.Combine(new[] { Name, Direction.ToString(), EvolutionHash.EncodeDouble(Minimum),
        EvolutionHash.EncodeDouble(Maximum), EvolutionHash.EncodeDouble(Tolerance) });
}
