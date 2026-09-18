namespace AiDotNet.Evolution;

/// <summary>A non-deployable exploration candidate and its owning island, separate from result elites.</summary>
public sealed class EvolutionInfeasibleEntry<TGenome>
{
    /// <summary>Creates an explicitly infeasible result record; feasible or incomplete evaluations are rejected.</summary>
    public EvolutionInfeasibleEntry(int island, EvolutionArchiveEntry<TGenome> entry)
    {
        if (island < 0) throw new ArgumentOutOfRangeException(nameof(island));
        Guard.NotNull(entry);
        if (entry.Evaluation.Status != EvolutionEvaluationStatus.Completed || !entry.Evaluation.ConstraintViolations.Any(value => value > 0))
            throw new ArgumentException("Exploration records require a completed infeasible evaluation.", nameof(entry));
        Island = island; Entry = entry;
    }
    /// <summary>Gets the owning island index.</summary>
    public int Island { get; }
    /// <summary>Gets the explicitly non-deployable candidate and its measured violations.</summary>
    public EvolutionArchiveEntry<TGenome> Entry { get; }
}
