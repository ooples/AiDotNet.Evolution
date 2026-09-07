namespace AiDotNet.Evolution;

/// <summary>Internal archive delta used to maintain engine aggregates without rescanning occupied cells.</summary>
internal sealed class EvolutionArchiveMutation<TGenome>
{
    internal EvolutionArchiveMutation(
        EvolutionArchiveInsertionResult result,
        EvolutionArchiveEntry<TGenome>? added,
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> removed)
    {
        Result = result;
        Added = added;
        Removed = removed;
    }

    internal EvolutionArchiveInsertionResult Result { get; }
    internal EvolutionArchiveEntry<TGenome>? Added { get; }
    internal IReadOnlyList<EvolutionArchiveEntry<TGenome>> Removed { get; }
}

/// <summary>Implemented by built-in archives that can report their exact accepted mutation.</summary>
internal interface IEvolutionArchiveMutationSource<TGenome>
{
    EvolutionArchiveMutation<TGenome> TryAddWithMutation(
        EvolutionCandidate<TGenome> candidate,
        EvolutionEvaluation evaluation);
}
