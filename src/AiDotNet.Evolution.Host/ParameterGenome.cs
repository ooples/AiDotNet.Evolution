using System.Globalization;
using System.Text;

namespace AiDotNet.Evolution.Host;

/// <summary>A flat set of named numeric parameters: the genome this host evolves.</summary>
/// <remarks>
/// <para>
/// WHY A PARAMETER SET AND NOT AN OPAQUE BLOB. A host that accepted arbitrary
/// JSON genomes would have nothing to vary them with -- the engine needs an
/// <see cref="IVariationOperator{TGenome}"/>, and no operator can meaningfully mutate a
/// structure it knows nothing about. The choice is between asking the client to propose
/// variations too, which doubles the round trips and complicates the protocol, or picking
/// a genome shape general enough to be useful and specific enough to mutate.
/// </para>
/// <para>
/// A flat parameter set is that shape. It covers the case this binding exists for --
/// evolving configurations, thresholds and hyperparameters, which is what quality-diversity
/// search over a preset space actually is -- and it is exactly what a variation operator can
/// reason about, because every dimension declares its own bounds and step.
/// </para>
/// <para><b>For Beginners:</b> Think of a genome here as one row of settings: a name and a
/// number for each knob. Evolution tries different rows, you score each one, and the archive
/// keeps the best row found in each region of the space.</para>
/// </remarks>
internal sealed class ParameterGenome : IImmutableEvolutionGenome<ParameterGenome>
{
    private readonly double[] _values;

    internal ParameterGenome(ParameterSpace space, double[] values)
    {
        Space = space;
        _values = values;
    }

    internal ParameterSpace Space { get; }

    /// <summary>The value of each parameter, in the space's declared order.</summary>
    internal IReadOnlyList<double> Values => _values;

    /// <summary>
    /// A new instance holding its own array.
    /// </summary>
    /// <remarks>
    /// The engine refuses a snapshot that returns <c>this</c>, and it is right to: the
    /// caller's array could be mutated after the archive retained it, which would rewrite
    /// history that was supposed to be immutable.
    /// </remarks>
    public ParameterGenome CreateOwnedSnapshot() => new(Space, (double[])_values.Clone());

    /// <summary>
    /// A canonical identity, and a genuinely canonical one.
    /// </summary>
    /// <remarks>
    /// Round-trip-stable text over the ORDERED values, formatted with the invariant culture
    /// and a fixed precision, so two genomes are the same id exactly when they are the same
    /// point in the space. This is not the `ToString` trap the session guards against: there
    /// the default returned a type name shared by every instance, whereas here the string IS
    /// the content, and the fixed precision is what makes float noise below the parameter's
    /// own resolution collapse rather than manufacture false novelty.
    /// </remarks>
    internal string CanonicalId()
    {
        var text = new StringBuilder();
        for (int i = 0; i < _values.Length; i += 1)
        {
            if (i > 0) text.Append('|');
            text.Append(Space.Parameters[i].Name);
            text.Append('=');
            text.Append(_values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return text.ToString();
    }

    public override string ToString() => CanonicalId();
}
