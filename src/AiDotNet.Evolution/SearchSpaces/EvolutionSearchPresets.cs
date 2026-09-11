namespace AiDotNet.Evolution;

/// <summary>Explicit typed-space presets; research/adaptive choices never replace the simple default implicitly.</summary>
public enum EvolutionSearchPreset
{
    /// <summary>Normalized Gaussian mutation with probability 0.2 and scale 0.1; the conservative API default.</summary>
    Mutation = 0,
    /// <summary>One initial trial per mutation/crossover/restart operator, then uniform operator allocation.</summary>
    UniformMixed = 1,
    /// <summary>The same three operators with opt-in archive-success credit and 0.1 exploration.</summary>
    AdaptiveMixed = 2,
    /// <summary>Opt-in diagonal CMA for supported unconditional, nonconstant continuous domains.</summary>
    DiagonalCma = 3
}

/// <summary>A small factory catalog for independently owned, versioned standard typed-search operators.</summary>
/// <remarks>
/// Each call returns a fresh operator; never share adaptive instances between independent runs. Configure engine
/// inspirations to enable crossover. Local refinement remains explicit through <see cref="SearchSpaceLocalRefiner"/>,
/// because it requires an objective, evaluator identity and shared resource ledger. Direct operator constructors
/// remain available for tuning. These defaults describe behavior, not a universal quality or deployment guarantee.
/// </remarks>
public static class EvolutionSearchPresets
{
    /// <summary>Creates a fresh preset using the exact supplied schema; unsupported CMA domains fail before search.</summary>
    /// <param name="space">Validated mixed or continuous search space.</param>
    /// <param name="preset">Explicit operator choice; the default does not enable learning.</param>
    /// <param name="direction">Scalar direction for CMA learning. Only <see cref="EvolutionSearchPreset.DiagonalCma"/>
    /// uses one, so supplying a direction for any other preset is rejected instead of silently ignored.</param>
    /// <returns>A newly owned operator whose version includes its domain and behavioral settings. The concrete type is
    /// <see cref="SearchSpaceMutation"/>, <see cref="AdaptiveVariationPortfolio{TGenome}"/> for both mixed presets, or
    /// <see cref="DiagonalCmaEmitter"/>; the typed factory methods return those types without a cast.</returns>
    public static IVariationOperator<EvolutionSearchGenome> Create(EvolutionSearchSpace space,
        EvolutionSearchPreset preset = EvolutionSearchPreset.Mutation,
        EvolutionOptimizationDirection? direction = null)
    {
        Guard.NotNull(space);
        if (!Enum.IsDefined(typeof(EvolutionSearchPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
        if (direction.HasValue && !Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction.Value))
            throw new ArgumentOutOfRangeException(nameof(direction));
        if (direction.HasValue && preset != EvolutionSearchPreset.DiagonalCma)
            throw new ArgumentException("Only the DiagonalCma preset uses a scalar direction.", nameof(direction));
        return preset switch
        {
            EvolutionSearchPreset.Mutation => CreateMutation(space),
            EvolutionSearchPreset.UniformMixed => CreateUniformMixed(space),
            EvolutionSearchPreset.DiagonalCma => CreateDiagonalCma(space, direction ?? EvolutionOptimizationDirection.Maximize),
            _ => CreateAdaptiveMixed(space)
        };
    }

    /// <summary>Creates the conservative default: normalized Gaussian mutation with probability 0.2 and scale 0.1.</summary>
    /// <param name="space">Validated mixed or continuous search space.</param>
    /// <returns>A newly owned mutation operator.</returns>
    public static SearchSpaceMutation CreateMutation(EvolutionSearchSpace space)
    {
        Guard.NotNull(space);
        return new SearchSpaceMutation(space);
    }

    /// <summary>Creates the mutation/crossover/restart catalog with uniform allocation after one trial each.</summary>
    /// <param name="space">Validated mixed or continuous search space.</param>
    /// <returns>A newly owned portfolio; its statistics are readable without a cast.</returns>
    public static AdaptiveVariationPortfolio<EvolutionSearchGenome> CreateUniformMixed(EvolutionSearchSpace space) => CreateMixed(space, 1);

    /// <summary>Creates the same catalog with archive-success credit and 0.1 exploration.</summary>
    /// <param name="space">Validated mixed or continuous search space.</param>
    /// <returns>A newly owned portfolio; its statistics are readable without a cast.</returns>
    public static AdaptiveVariationPortfolio<EvolutionSearchGenome> CreateAdaptiveMixed(EvolutionSearchSpace space) => CreateMixed(space, 0.1);

    /// <summary>Creates the opt-in diagonal CMA emitter for supported unconditional, nonconstant continuous domains.</summary>
    /// <param name="space">Validated continuous search space; unsupported domains fail before search.</param>
    /// <param name="direction">Scalar direction the emitter learns from; mismatching evaluations cannot train it.</param>
    /// <returns>A newly owned emitter.</returns>
    public static DiagonalCmaEmitter CreateDiagonalCma(EvolutionSearchSpace space,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(space);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        return new DiagonalCmaEmitter(space, direction: direction);
    }

    private static AdaptiveVariationPortfolio<EvolutionSearchGenome> CreateMixed(EvolutionSearchSpace space, double explorationProbability)
    {
        Guard.NotNull(space);
        return new AdaptiveVariationPortfolio<EvolutionSearchGenome>(new IVariationOperator<EvolutionSearchGenome>[]
        {
            new SearchSpaceMutation(space), new SearchSpaceCrossover(space), new SearchSpaceRestart(space)
        }, explorationProbability: explorationProbability);
    }
}
