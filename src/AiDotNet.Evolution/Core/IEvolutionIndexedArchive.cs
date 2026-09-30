namespace AiDotNet.Evolution;

/// <summary>
/// Positional access to an archive's entries in exactly the order <see cref="IEvolutionArchiveView{TGenome}.Entries"/>
/// lists them, without materialising that list.
/// </summary>
/// <remarks>
/// Selection runs once per proposal. Reading <c>Entries</c> there copied the whole archive whenever its version had
/// changed, which in steady state is about every other evaluation, so per-evaluation cost grew with archive size
/// (V1-72). Built-in policies use this when the archive offers it and fall back to <c>Entries</c> otherwise.
/// </remarks>
internal interface IEvolutionIndexedArchive<TGenome>
{
    /// <summary>Gets the number of entries, equal to <c>Entries.Count</c>.</summary>
    int Count { get; }

    /// <summary>Gets the entry at <paramref name="index"/>, equal to <c>Entries[index]</c>.</summary>
    EvolutionArchiveEntry<TGenome> EntryAt(int index);

    /// <summary>Returns the position of this exact entry, or -1 when it is not currently in the archive.</summary>
    int IndexOf(EvolutionArchiveEntry<TGenome> entry);

    /// <summary>Returns whether exactly one entry holds this genome; selection excludes all of a parent's copies.</summary>
    bool HoldsGenomeOnce(string genomeId);
}
