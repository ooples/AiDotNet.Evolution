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
            values[i] = ClampGeneratedValue(parameter, current + Gaussian(random) * Sigma * range);
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
            values[index] = ClampGeneratedValue(parameter, parent.Values[index] + Gaussian(random) * Sigma * range);
        }

        return new ValueTask<ParameterGenome>(EnsureDifferent(space.Create(values), parent, random));
    }

    /// <summary>Guarantees the proposal is not its own parent.</summary>
    /// <remarks>
    /// <para>
    /// TOUCHING A VALUE IS NOT THE SAME AS CHANGING IT. <see cref="ParameterSpace.Create"/>
    /// clamps to the bounds and snaps to the step, so a Gaussian step smaller than half a
    /// step, or one that overshoots a bound the parent already sits on, normalises straight
    /// back to the parent's value -- and every dimension doing that yields the parent's
    /// canonical id. The `touchedAny` guard above cannot see this: it knows a value was
    /// perturbed, not that the perturbation survived.
    /// </para>
    /// <para>
    /// The engine notices the duplicate and never spends an evaluation on it, so nothing is
    /// scored twice. What it does spend is a PROPOSAL, and a run whose budget is proposals
    /// simply searches less. So one dimension is moved up or down a step. If both steps
    /// collapse under integral rounding, its normalized endpoints are tried instead. The
    /// normalizer is monotone, so equal endpoints mean that dimension has only one value.
    /// </para>
    /// <para>
    /// Starting from a random dimension rather than the first keeps the forced move from
    /// always landing on the same axis, which would bias the search along it.
    /// </para>
    /// </remarks>
    // Internal rather than private so it can be tested directly: building an
    // EvolutionVariationContext by hand means constructing four engine DTOs, and a test
    // that fragile would break on unrelated signature changes without ever failing for
    // the reason it exists.
    internal static ParameterGenome EnsureDifferent(
        ParameterGenome child,
        ParameterGenome parent,
        StableRandom random)
    {
        if (!string.Equals(child.CanonicalId(), parent.CanonicalId(), StringComparison.Ordinal))
            return child;

        ParameterSpace space = child.Space;
        int count = space.Parameters.Count;
        int start = Math.Min(count - 1, (int)(random.NextDouble() * count));

        for (int n = 0; n < count; n += 1)
        {
            int i = (start + n) % count;
            ParameterDefinition parameter = space.Parameters[i];
            double current = child.Values[i];

            // Endpoints are only evaluated when both local probes collapse. Unlike random
            // retries this is bounded, and it also handles a default subunit step on a
            // binary integral parameter without spending every proposal on the parent.
            ParameterGenome? moved = TryMove(child, i, current + parameter.Step)
                ?? TryMove(child, i, current - parameter.Step)
                ?? TryMove(child, i, parameter.Minimum)
                ?? TryMove(child, i, parameter.Maximum);
            if (moved is not null) return moved;
        }

        // Every dimension is a single representable point, so there is no neighbour to
        // propose. Returning the parent's twin is honest; the engine folds it as a
        // duplicate.
        return child;
    }

    private static ParameterGenome? TryMove(ParameterGenome child, int index, double value)
    {
        ParameterSpace space = child.Space;
        ParameterDefinition parameter = space.Parameters[index];
        double moved = parameter.Normalize(ClampGeneratedValue(parameter, value));
        if (moved == child.Values[index]) return null;

        var values = new double[space.Parameters.Count];
        for (int i = 0; i < values.Length; i += 1) values[i] = child.Values[i];
        values[index] = moved;
        return space.Create(values);
    }

    /// <summary>Clamps generated arithmetic before the external-input normalization policy applies.</summary>
    /// <remarks>
    /// Finite operands can overflow at a boundary. A positive overflow is an upper-bound
    /// overshoot, not an unusable external value that should fall back to the minimum.
    /// </remarks>
    internal static double ClampGeneratedValue(ParameterDefinition parameter, double value) =>
        Math.Min(parameter.Maximum, Math.Max(parameter.Minimum, value));

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
