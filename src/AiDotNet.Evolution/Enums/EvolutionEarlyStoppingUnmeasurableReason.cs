namespace AiDotNet.Evolution;

/// <summary>Why one early-stopping reading had nothing to measure.</summary>
/// <remarks>
/// Each value names a state of the search, never a failure of the criterion itself: the criterion is well formed and
/// simply has no value yet. The reasons are counted per run in <see cref="EvolutionEarlyStoppingReport"/>, so a run
/// that skipped its criterion says so afterwards instead of leaving it to be discovered by reading the engine source.
/// </remarks>
public enum EvolutionEarlyStoppingUnmeasurableReason
{
    /// <summary>No completed evaluation reported the watched evaluator metric.</summary>
    MetricNotReported = 0,

    /// <summary>The union of the island Pareto fronts holds no feasible member yet.</summary>
    EmptyFeasibleFront = 1,

    /// <summary>No archived elite carries a value the criterion can aggregate yet.</summary>
    EmptyArchive = 2,

    /// <summary>The archives report no cells, so occupancy has no denominator.</summary>
    /// <remarks>
    /// Defensive: the archive geometry contract already refuses an archive that reports a non-positive cell count,
    /// and the descriptor fallback floors at one cell, so a valid run cannot reach this state today. Occupancy of a
    /// valid grid is always measurable - zero occupied cells is a measurement, not a gap.
    /// </remarks>
    NoArchiveCells = 3
}
