namespace AiDotNet.Evolution;

/// <summary>One position in an evaluation's objective vector, with fixed units and comparison precision.</summary>
public sealed class EvolutionObjectiveDefinition
{
    /// <summary>Defines a finite closed reporting interval and an optional normalized epsilon-box width.</summary>
    /// <remarks>
    /// Values outside the interval are rejected, never silently clipped. Resolution zero means exact dominance;
    /// otherwise comparisons use floor(normalized loss / resolution). This transitive box order deliberately
    /// avoids pairwise epsilon comparisons, which can cycle. Bounds and resolution must be fixed before a run.
    /// </remarks>
    public EvolutionObjectiveDefinition(string name, EvolutionOptimizationDirection direction,
        double minimum, double maximum, double resolution = 0)
    {
        Guard.NotNullOrWhiteSpace(name);
        if (name.Trim().Length > 128) throw new ArgumentException("Objective names are limited to 128 characters.", nameof(name));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (!EvolutionDescriptorDefinition.IsFinite(minimum) || !EvolutionDescriptorDefinition.IsFinite(maximum) ||
            maximum <= minimum || !EvolutionDescriptorDefinition.IsFinite(maximum - minimum))
            throw new ArgumentOutOfRangeException(nameof(maximum), "Objective bounds need a positive finite width.");
        if (!EvolutionDescriptorDefinition.IsFinite(resolution) || resolution < 0 || resolution > 1 ||
            (resolution > 0 && resolution < 1e-12)) throw new ArgumentOutOfRangeException(nameof(resolution));
        Name = name.Trim(); Direction = direction; Minimum = minimum; Maximum = maximum; Resolution = resolution;
    }

    /// <summary>Gets the unique case-sensitive objective name.</summary>
    public string Name { get; }
    /// <summary>Gets whether this objective is minimized or maximized, independently of scalar quality.</summary>
    public EvolutionOptimizationDirection Direction { get; }
    /// <summary>Gets the inclusive lower bound in the objective's original units.</summary>
    public double Minimum { get; }
    /// <summary>Gets the inclusive upper bound in the objective's original units.</summary>
    public double Maximum { get; }
    /// <summary>Gets the normalized epsilon-box width, or zero for exact comparisons.</summary>
    public double Resolution { get; }

    /// <summary>Converts a valid value to loss: zero is ideal and one is worst.</summary>
    public double Normalize(double value)
    {
        if (!EvolutionDescriptorDefinition.IsFinite(value) || value < Minimum || value > Maximum)
            throw new ArgumentOutOfRangeException(nameof(value), "Objective value is outside the fixed reporting interval.");
        return Direction == EvolutionOptimizationDirection.Minimize
            ? (value - Minimum) / (Maximum - Minimum) : (Maximum - value) / (Maximum - Minimum);
    }

    internal double CompareValue(double value)
    {
        double loss = Normalize(value);
        return Resolution == 0 ? loss : Math.Floor(loss / Resolution);
    }

    internal string Canonical => EvolutionHash.Combine(new[] { Name, Direction.ToString(),
        EvolutionHash.EncodeDouble(Minimum), EvolutionHash.EncodeDouble(Maximum), EvolutionHash.EncodeDouble(Resolution) });
}
