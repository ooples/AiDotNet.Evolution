namespace AiDotNet.Evolution;

/// <summary>Restores deployable and exploratory populations without confusing their admission semantics.</summary>
public interface ICheckpointableParetoArchive<TGenome> : ICheckpointableEvolutionArchive<TGenome>, IEvolutionParetoArchiveView<TGenome>
{
    /// <summary>Validates both sets before adopting their exact storage slots and shared mutation version.</summary>
    void RestoreWithExploration(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries,
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> infeasibleEntries, IReadOnlyList<EvolutionDescriptorDefinition> descriptors, long version);
}
