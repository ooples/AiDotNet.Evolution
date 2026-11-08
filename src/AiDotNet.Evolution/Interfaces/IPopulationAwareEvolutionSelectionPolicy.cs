namespace AiDotNet.Evolution;

/// <summary>Receives every island archive before each parent selection.</summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// A plain <see cref="ISelectionPolicy{TGenome}"/> sees only the archive it is asked to select from. A policy that also
/// implements this interface is handed the engine's island archives, in island order, immediately before every
/// <see cref="ISelectionPolicy{TGenome}.Select"/> call, so it can draw a parent from the whole population, as
/// OpenEvolve's <c>_sample_random_parent</c> does. After the selection the engine passes an empty list, so a policy
/// may keep what it was given until its next call, but no longer. The archives are owned by the engine: a policy may
/// read them but must not mutate them.
/// </para>
/// <para><b>For Beginners:</b> With several islands, the code that picks parents normally sees only one of them. This
/// interface lets a policy look at all of them, so a parent can come from any island. Implement it only if your policy
/// needs that whole view; <see cref="RatioEvolutionSelectionPolicy{TGenome}"/> uses it for
/// <see cref="EvolutionEliteBranch.UniformPopulation"/>.</para>
/// </remarks>
public interface IPopulationAwareEvolutionSelectionPolicy<TGenome> : ISelectionPolicy<TGenome>
{
    /// <summary>Supplies the island archives the next selection may draw from.</summary>
    /// <param name="islands">Every island archive, indexed by island.</param>
    void UsePopulation(IReadOnlyList<IEvolutionArchive<TGenome>> islands);
}
