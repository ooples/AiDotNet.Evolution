namespace AiDotNet.Evolution;

/// <summary>Optional fixed-pool destination scheduling supplied by a checkpointable variation operator.</summary>
/// <remarks>
/// Selection must not mutate state: the engine may find no usable parent and therefore never prepare a proposal.
/// Account for a decision only when ProposeAsync actually begins. The engine serializes scheduling, proposals and
/// outcomes; external concurrent use is unsupported. Seeds retain round-robin assignment. The operator's version
/// must identify all scheduling semantics, and its checkpoint must preserve allocation state.
/// </remarks>
public interface IEvolutionIslandProposalScheduler
{
    /// <summary>Gets the fixed island count, which must match the engine configuration.</summary>
    int IslandCount { get; }
    /// <summary>Returns an island in [0,IslandCount), using only current policy state and the supplied stable stream.</summary>
    int SelectIsland(long evaluationId, StableRandom random);
}
