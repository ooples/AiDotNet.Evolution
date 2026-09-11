namespace AiDotNet.Evolution;

/// <summary>Receives committed proposal outcomes and persists the state learned from them.</summary>
/// <typeparam name="TGenome">The immutable genome proposed by the operator.</typeparam>
/// <remarks>
/// <para>
/// The engine calls <see cref="Observe"/> once for each committed non-seed proposal, in commit order.
/// Failed proposals, duplicates, and cache hits are included so an operator can account for unsuccessful work.
/// Retries produce one terminal notification, not one notification per attempt. Migration does not propose a
/// new genome and produces no notification. The evaluation's lineage identifies the proposal generation.
/// </para>
/// <para>
/// Outcome feedback is part of the search, unlike an observer used only for diagnostics. Implementations must
/// update state deterministically, must not reenter the engine, and must include learned state in
/// <see cref="ICheckpointableVariationOperator{TGenome}.CaptureState"/>. An exception propagates and fails the
/// run; it is not silently ignored. Wall-clock costs must not influence a policy claiming deterministic replay.
/// </para>
/// </remarks>
public interface IOutcomeAwareVariationOperator<TGenome> : ICheckpointableVariationOperator<TGenome>
{
    /// <summary>Updates operator state after a proposal reaches its terminal committed outcome.</summary>
    /// <param name="evaluation">The immutable outcome, including proposal lineage and evaluation costs.</param>
    /// <param name="insertionResult">
    /// The archive decision, or <see langword="null"/> when the evaluation did not complete.
    /// A completed cached result is identifiable through <see cref="EvolutionEvaluation.CacheStatus"/>.
    /// </param>
    void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult);
}
