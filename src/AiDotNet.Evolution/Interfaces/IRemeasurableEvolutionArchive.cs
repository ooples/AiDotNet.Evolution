namespace AiDotNet.Evolution;

/// <summary>An archive that can re-file everything it holds against new descriptor values.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// The engine uses this when an <see cref="IRebasingEvolutionTask{TGenome}"/> changes its reading. An archive that
/// does not implement it cannot be used with a task that rebases its descriptors, and the engine refuses that
/// combination when it is constructed.
/// </remarks>
public interface IRemeasurableEvolutionArchive<TGenome> : IEvolutionArchive<TGenome>
{
    /// <summary>Re-measures every entry and re-files the archive, transactionally.</summary>
    /// <param name="measure">Produces an entry's complete replacement descriptors; <see langword="null"/> keeps them.</param>
    /// <returns>How many entries remain after collisions are resolved.</returns>
    int RemeasureEntries(Func<EvolutionArchiveEntry<TGenome>, IReadOnlyDictionary<string, double>?> measure);
}
