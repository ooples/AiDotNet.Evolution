using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Ptx;

/// <summary>How a buffer's seeded test contents are drawn.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxValueDistribution
{
    /// <summary>Creates a distribution.</summary>
    /// <param name="kind">The kind of distribution.</param>
    /// <param name="minimum">The lower bound, or the constant.</param>
    /// <param name="maximum">The upper bound; ignored for a constant.</param>
    /// <exception cref="ArgumentException">A bound is not finite or the range is empty.</exception>
    [JsonConstructor]
    public PtxValueDistribution(PtxDistributionKind kind, double minimum, double maximum)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || (kind != PtxDistributionKind.Constant && maximum < minimum))
            throw new ArgumentException("A distribution needs finite, ordered bounds.");
        Kind = kind;
        Minimum = minimum;
        Maximum = kind == PtxDistributionKind.Constant ? minimum : maximum;
    }

    /// <summary>Gets the kind.</summary>
    public PtxDistributionKind Kind { get; }
    /// <summary>Gets the lower bound, or the constant.</summary>
    public double Minimum { get; }
    /// <summary>Gets the upper bound.</summary>
    public double Maximum { get; }

    /// <summary>Uniform real values in [minimum, maximum).</summary>
    /// <param name="minimum">The lower bound.</param>
    /// <param name="maximum">The upper bound.</param>
    /// <returns>The distribution.</returns>
    public static PtxValueDistribution Uniform(double minimum, double maximum) => new(PtxDistributionKind.Uniform, minimum, maximum);

    /// <summary>Uniform integers in [minimum, maximum].</summary>
    /// <param name="minimum">The lower bound.</param>
    /// <param name="maximum">The upper bound.</param>
    /// <returns>The distribution.</returns>
    public static PtxValueDistribution Integers(long minimum, long maximum) => new(PtxDistributionKind.Integers, minimum, maximum);

    /// <summary>Every element equal to one value.</summary>
    /// <param name="value">The value.</param>
    /// <returns>The distribution.</returns>
    public static PtxValueDistribution Constant(double value) => new(PtxDistributionKind.Constant, value, value);
}

/// <summary>The accepted numerical difference from the reference: <c>|candidate - reference| &lt;= absolute + relative * |reference|</c>.</summary>
[Experimental("AIDEVO005")]
public sealed class PtxTolerance
{
    /// <summary>Creates a tolerance.</summary>
    /// <param name="absolute">The absolute term; finite and not negative.</param>
    /// <param name="relative">The relative term; finite and not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException">A term is negative or not finite.</exception>
    [JsonConstructor]
    public PtxTolerance(double absolute, double relative)
    {
        if (!double.IsFinite(absolute) || absolute < 0) throw new ArgumentOutOfRangeException(nameof(absolute));
        if (!double.IsFinite(relative) || relative < 0) throw new ArgumentOutOfRangeException(nameof(relative));
        Absolute = absolute;
        Relative = relative;
    }

    /// <summary>Gets the absolute term.</summary>
    public double Absolute { get; }
    /// <summary>Gets the relative term.</summary>
    public double Relative { get; }

    /// <summary>Exact equality, for integer outputs and bit-exact kernels.</summary>
    public static PtxTolerance Exact { get; } = new(0, 0);

    internal bool Accepts(double candidate, double reference)
    {
        if (double.IsNaN(reference)) return double.IsNaN(candidate);
        if (double.IsInfinity(reference)) return candidate == reference;
        if (!double.IsFinite(candidate)) return false;
        return Math.Abs(candidate - reference) <= Absolute + Relative * Math.Abs(reference);
    }
}