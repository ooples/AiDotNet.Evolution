namespace AiDotNet.Evolution;

/// <summary>Controls where the bin boundaries of one descriptor axis lie.</summary>
/// <remarks>
/// <para>
/// <see cref="Linear"/> and <see cref="Logarithmic"/> divide a fixed range evenly in value or in log-value;
/// <see cref="ExplicitEdges"/> takes the boundaries verbatim. The two adaptive modes learn the boundaries from the
/// descriptor values the archive is offered, in the engine's deterministic commit order, and re-key every elite when
/// the boundaries move, keeping the better elite whenever two land in one cell. <see cref="AdaptiveMinMax"/> spans
/// the observed range evenly, as OpenEvolve's default feature scaling does; <see cref="AdaptiveQuantile"/> places the
/// boundaries at observed quantiles so each bin holds about the same share of what the search produced, which keeps
/// cells populated when the values bunch up. A re-key can put two elites in one cell, and only the better one stays,
/// so an adaptive archive can hold fewer elites than its final bins would have room for; quantile edges move with
/// almost every early observation, so this is most visible there. The learned state travels in checkpoints, so a
/// resumed run bins exactly as the uninterrupted one would. The mode is part of the archive definition hash.
/// </para>
/// <para><b>For Beginners:</b> A descriptor axis is a ruler cut into slots. These modes choose where the cuts go:
/// evenly (<see cref="Linear"/>), evenly on a log scale for values spanning orders of magnitude
/// (<see cref="Logarithmic"/>), exactly where you say (<see cref="ExplicitEdges"/>), or wherever the search's own
/// results make them useful (<see cref="AdaptiveMinMax"/>, <see cref="AdaptiveQuantile"/>). Adaptive modes are the
/// right choice when you cannot guess the range: a fixed range that is far too wide puts every candidate in one slot,
/// and the archive then keeps a single program instead of a diverse set.</para>
/// </remarks>
public enum EvolutionDescriptorBinning
{
    /// <summary>Equal-width bins between the configured minimum and maximum.</summary>
    Linear = 0,
    /// <summary>Bins of equal width in log-value between a positive minimum and the maximum.</summary>
    Logarithmic = 1,
    /// <summary>Bins bounded by a caller-supplied, strictly increasing list of edges.</summary>
    ExplicitEdges = 2,
    /// <summary>Equal-width bins spanning the smallest and largest value observed so far.</summary>
    AdaptiveMinMax = 3,
    /// <summary>Bins bounded by quantiles of the values observed so far.</summary>
    AdaptiveQuantile = 4
}
