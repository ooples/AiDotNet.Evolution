namespace AiDotNet.Evolution;

/// <summary>Selects what the third branch of a ratio selection policy, drawn with probability
/// <see cref="EvolutionSelectionOptions.EliteRatio"/>, returns as the parent.</summary>
/// <remarks>
/// <para>
/// A ratio selection policy splits parent draws into exploration, exploitation and a third, remainder branch.
/// <see cref="IslandBest"/> makes that branch the target island's current best entry, a pure hill-climbing step.
/// OpenEvolve's remainder branch is different: its <c>_sample_random_parent</c> picks uniformly from every program in
/// the database, on any island. <see cref="UniformGlobal"/> is the bounded counterpart: it picks uniformly from the
/// engine's cross-island elite index, which retains only the best <c>GlobalEliteCount</c> entries (an imported
/// OpenEvolve config sizes it to <c>archive_size</c>), not every program. <see cref="UniformIsland"/> is a uniform
/// draw restricted to the target island. The value participates in the policy version hash, so changing it invalidates
/// older checkpoints by design.
/// </para>
/// <para><b>For Beginners:</b> Most parent picks either explore (pick anything on this island) or exploit (pick one of
/// the strongest). The remaining share decides how adventurous the rest are: always the current champion, which
/// converges fastest but narrowly, or any program at all, which keeps weaker but different ideas in play. Choose
/// <see cref="UniformGlobal"/> for the closest match to OpenEvolve.</para>
/// </remarks>
public enum EvolutionEliteBranch
{
    /// <summary>The target island's current best entry.</summary>
    IslandBest = 0,
    /// <summary>A uniformly random entry of the target island.</summary>
    UniformIsland = 1,
    /// <summary>A uniformly random entry of the engine's capacity-bounded cross-island elite index, falling back to the
    /// target island when the index is empty; the bounded counterpart of OpenEvolve's remainder branch.</summary>
    UniformGlobal = 2
}
