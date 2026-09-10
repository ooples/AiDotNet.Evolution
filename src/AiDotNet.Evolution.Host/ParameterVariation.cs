namespace AiDotNet.Evolution.Host;

/// <summary>Proposes a neighbour by nudging a few parameters of a parent.</summary>
/// <remarks>
/// <para>
/// A GAUSSIAN STEP ON A SUBSET, which is the standard move for a continuous quality-diversity
/// search and is chosen over the two obvious alternatives for stated reasons. Perturbing EVERY
/// parameter each time makes the offspring unrelated to its parent, so the archive stops being
/// a map of a neighbourhood and becomes random sampling. Perturbing exactly ONE makes progress
/// along diagonals impossibly slow, because every useful combination has to be discovered one
/// axis at a time.
/// </para>
/// <para>
/// Randomness comes from the engine's <see cref="StableRandom"/> stream, never from ambient
/// state, which is what keeps a run reproducible from its seed -- the same property the engine
/// requires of any operator.
/// </para>
/// </remarks>
internal sealed class ParameterVariation : IVariationOperator<ParameterGenome>
{
    /// <summary>Chance that any one parameter is touched, given at least one always is.</summary>
    private const double MutationRate = 0.3;

    /// <summary>Step size as a fraction of each parameter's range.</summary>
    private const double Sigma = 0.15;

    public string Id => "parameter-gaussian";

    public string VersionHash => "parameter-gaussian-v1";

    public ValueTask<ParameterGenome> ProposeAsync(
        EvolutionVariationContext<ParameterGenome> context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ParameterGenome parent = context.Parent.Candidate.CanonicalGenome.Genome;
        ParameterSpace space = parent.Space;
        StableRandom random = context.Random;

        var values = new double[space.Parameters.Count];
        bool touchedAny = false;

        for (int i = 0; i < values.Length; i += 1)
        {
            ParameterDefinition parameter = space.Parameters[i];
            double current = parent.Values[i];

            if (random.NextDouble() >= MutationRate)
            {
                values[i] = current;
                continue;
            }

            double range = parameter.Maximum - parameter.Minimum;
            values[i] = current + Gaussian(random) * Sigma * range;
            touchedAny = true;
        }

        // AT LEAST ONE, or the proposal is its own parent and the engine spends an
        // evaluation slot rediscovering a candidate it already has.
        if (!touchedAny)
        {
            int index = (int)(random.NextDouble() * values.Length);
            if (index >= values.Length) index = values.Length - 1;
            ParameterDefinition parameter = space.Parameters[index];
            double range = parameter.Maximum - parameter.Minimum;
            values[index] = parent.Values[index] + Gaussian(random) * Sigma * range;
        }

        return new ValueTask<ParameterGenome>(space.Create(values));
    }

    /// <summary>
    /// A standard normal sample, by Box-Muller.
    /// </summary>
    /// <remarks>
    /// Drawn from the engine's stream rather than <c>Random.Shared</c>, so the same seed
    /// replays the same search. The lower bound on <c>u1</c> avoids <c>log(0)</c>, which
    /// would produce infinity and then a non-finite parameter the space would silently clamp
    /// back to its minimum -- a mutation that looks like a jump to the floor.
    /// </remarks>
    private static double Gaussian(StableRandom random)
    {
        double u1 = Math.Max(1e-12, random.NextDouble());
        double u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
