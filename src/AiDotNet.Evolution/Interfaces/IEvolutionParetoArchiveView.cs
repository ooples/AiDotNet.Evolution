namespace AiDotNet.Evolution;

/// <summary>Optional front metadata; null explicitly identifies a scalar snapshot.</summary>
public interface IEvolutionParetoArchiveView<TGenome> : IEvolutionArchiveView<TGenome>
{
    /// <summary>Gets front semantics, or null for a scalar archive snapshot.</summary>
    EvolutionParetoDefinition? ParetoDefinition { get; }
    /// <summary>Gets separately retained, non-deployable exploration entries; null when exploration is disabled.</summary>
    IReadOnlyList<EvolutionArchiveEntry<TGenome>>? InfeasibleEntries { get; }
}
