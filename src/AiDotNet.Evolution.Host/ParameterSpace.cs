using System.Globalization;

namespace AiDotNet.Evolution.Host;

/// <summary>One knob: its name, its bounds, and how coarsely it is quantised.</summary>
internal sealed class ParameterDefinition
{
    private readonly double _normalizationMinimum;
    private readonly double _normalizationMaximum;

    internal ParameterDefinition(string name, double minimum, double maximum, double step, bool integral)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A parameter needs a name.", nameof(name));
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum))
            throw new ArgumentException($"Parameter '{name}' has a non-finite bound.", nameof(name));
        if (maximum <= minimum)
            throw new ArgumentException($"Parameter '{name}' has maximum <= minimum.", nameof(name));
        double span = maximum - minimum;
        if (!double.IsFinite(span))
            throw new ArgumentException($"Parameter '{name}' range must be representable as a finite double; use narrower bounds.", nameof(maximum));
        if (!double.IsFinite(step) || step <= 0)
            throw new ArgumentException($"Parameter '{name}' requires a finite, positive step.", nameof(step));
        if (!double.IsFinite(span / step))
            throw new ArgumentException($"Parameter '{name}' range divided by step must be representable as a finite double; use a larger step.", nameof(step));

        _normalizationMinimum = integral ? Math.Ceiling(minimum) : minimum;
        _normalizationMaximum = integral ? Math.Floor(maximum) : maximum;
        if (_normalizationMinimum > _normalizationMaximum)
            throw new ArgumentException($"Parameter '{name}' has no integral value within its bounds.", nameof(integral));

        Name = name;
        Minimum = minimum;
        Maximum = maximum;
        Step = step;
        Integral = integral;
    }

    internal string Name { get; }
    internal double Minimum { get; }
    internal double Maximum { get; }

    /// <summary>
    /// The resolution of this dimension.
    /// </summary>
    /// <remarks>
    /// QUANTISATION IS WHAT MAKES THE IDENTITY MEAN ANYTHING. Without it, two genomes
    /// differing by 1e-17 are distinct candidates with distinct ids, so the archive fills
    /// with float noise and deduplication never fires. Snapping to a step turns "nearly the
    /// same settings" into "the same settings", which is what a reader means by it.
    /// </remarks>
    internal double Step { get; }

    /// <summary>True when only whole numbers are meaningful, such as a count of rows.</summary>
    internal bool Integral { get; }

    /// <summary>Clamps to the bounds and snaps to the step.</summary>
    internal double Normalize(double value)
    {
        if (!double.IsFinite(value)) return _normalizationMinimum;
        double clamped = Math.Min(_normalizationMaximum, Math.Max(_normalizationMinimum, value));
        // The first admissible integer also anchors an integral grid. Keeping a fractional
        // origin could move an already normalized value again (min=.5, step=1: 1 -> 2 -> 3).
        double snapped = _normalizationMinimum
            + Math.Round((clamped - _normalizationMinimum) / Step, MidpointRounding.AwayFromZero) * Step;
        if (Integral) snapped = Math.Round(snapped, MidpointRounding.AwayFromZero);
        // Rounding can leave the value a hair outside after the snap.
        return Math.Min(_normalizationMaximum, Math.Max(_normalizationMinimum, snapped));
    }
}

/// <summary>The ordered set of knobs a run searches over.</summary>
internal sealed class ParameterSpace
{
    internal ParameterSpace(IReadOnlyList<ParameterDefinition> parameters)
    {
        if (parameters is null || parameters.Count == 0)
            throw new ArgumentException("A parameter space needs at least one parameter.", nameof(parameters));

        ParameterDefinition[] snapshot = parameters.ToArray();
        _names = new HashSet<string>(StringComparer.Ordinal);
        foreach (ParameterDefinition parameter in snapshot)
        {
            if (!_names.Add(parameter.Name))
            {
                throw new ArgumentException(
                    $"Parameter '{parameter.Name}' is declared more than once; identities would collide.",
                    nameof(parameters));
            }
        }
        // Keep both the source list and the exposed read-only view from changing the
        // dimension order after names and existing genomes have been bound to it.
        Parameters = Array.AsReadOnly(snapshot);
    }

    /// <summary>Declared names, kept so a seed can be checked against them.</summary>
    private readonly HashSet<string> _names;

    /// <summary>Order is significant: it fixes the layout of every genome and identity.</summary>
    internal IReadOnlyList<ParameterDefinition> Parameters { get; }

    /// <summary>Builds a genome from raw values, normalising each one.</summary>
    internal ParameterGenome Create(IReadOnlyList<double> values)
    {
        if (values.Count != Parameters.Count)
        {
            throw new ArgumentException(
                $"Expected {Parameters.Count} values, got {values.Count}.",
                nameof(values));
        }

        var normalized = new double[values.Count];
        for (int i = 0; i < values.Count; i += 1)
            normalized[i] = Parameters[i].Normalize(values[i]);
        return new ParameterGenome(this, normalized);
    }

    /// <summary>Builds a genome from a name/value map, defaulting anything absent to the midpoint.</summary>
    /// <remarks>
    /// AN UNKNOWN NAME IS AN ERROR, not something to ignore. Absent names default to the
    /// midpoint, so a client that misspells one gets a seed at the midpoint of that
    /// parameter rather than where it asked -- a different starting point, a different
    /// search, and an <c>ok: true</c> saying it all went fine. The typo never appears in
    /// the results, which is precisely why it has to be caught here.
    /// </remarks>
    /// <exception cref="ArgumentException">A name in <paramref name="values"/> is not declared.</exception>
    internal ParameterGenome Create(IReadOnlyDictionary<string, double> values)
    {
        foreach (string name in values.Keys)
        {
            if (!_names.Contains(name))
            {
                throw new ArgumentException(
                    $"Seed name '{name}' is not a declared parameter. Declared: {Describe()}.",
                    nameof(values));
            }
        }

        var raw = new double[Parameters.Count];
        for (int i = 0; i < Parameters.Count; i += 1)
        {
            ParameterDefinition parameter = Parameters[i];
            raw[i] = values.TryGetValue(parameter.Name, out double value)
                ? value
                : parameter.Minimum + (parameter.Maximum - parameter.Minimum) / 2;
        }
        return Create(raw);
    }

    /// <summary>The genome as a name/value map, for handing back over the wire.</summary>
    internal Dictionary<string, double> ToMap(ParameterGenome genome)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < Parameters.Count; i += 1)
            map[Parameters[i].Name] = genome.Values[i];
        return map;
    }

    internal string Describe() =>
        string.Join(
            ", ",
            Parameters.Select(p =>
                $"{p.Name}[{p.Minimum.ToString(CultureInfo.InvariantCulture)}..{p.Maximum.ToString(CultureInfo.InvariantCulture)}]"));
}
