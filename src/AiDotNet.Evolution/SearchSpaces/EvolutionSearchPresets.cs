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
    /// <param name="direction">Required scalar direction for CMA learning; other presets are direction-independent.</param>
    /// <returns>A newly owned operator whose version includes its domain and behavioral settings.</returns>
    public static IVariationOperator<EvolutionSearchGenome> Create(EvolutionSearchSpace space,
        EvolutionSearchPreset preset = EvolutionSearchPreset.Mutation,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(space);
        if (!Enum.IsDefined(typeof(EvolutionSearchPreset), preset)) throw new ArgumentOutOfRangeException(nameof(preset));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        return preset switch
        {
            EvolutionSearchPreset.Mutation => new SearchSpaceMutation(space),
            EvolutionSearchPreset.DiagonalCma => new DiagonalCmaEmitter(space, direction: direction),
            _ => new AdaptiveVariationPortfolio<EvolutionSearchGenome>(new IVariationOperator<EvolutionSearchGenome>[]
            {
                new SearchSpaceMutation(space), new SearchSpaceCrossover(space), new SearchSpaceRestart(space)
            }, explorationProbability: preset == EvolutionSearchPreset.UniformMixed ? 1 : 0.1)
        };
    }
}
