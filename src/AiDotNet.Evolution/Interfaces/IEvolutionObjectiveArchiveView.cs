namespace AiDotNet.Evolution;

/// <summary>Optional objective semantics preserved by live archives and detached snapshots.</summary>
public interface IEvolutionObjectiveArchiveView
{
    /// <summary>Gets the Pareto contract, or null for a scalar archive. Entries form the retained feasible front.</summary>
    EvolutionParetoDefinition? ParetoDefinition { get; }
}
