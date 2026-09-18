namespace AiDotNet.Evolution;

/// <summary>A versioned proposal backend returning actual resource receipts and explicitly checkpointing all learning.</summary>
/// <typeparam name="TGenome">The immutable genome produced by this backend.</typeparam>
/// <remarks>Use with ResourceMeteredVariationOperator. Include model/compiler/repair work performed inside this proposal
/// in one aggregate receipt; do not also meter those same charges elsewhere. Return actual costs even when work fails.
/// Missing receipts are charged conservatively. Enforce resource/isolation bounds in the backend. Stateless backends
/// return a fixed nonempty state and reject different state. Wall-clock measurements must not drive deterministic policies.</remarks>
public interface ICostedEvolutionProposalSource<TGenome>
{
    /// <summary>Gets the bounded stable backend identity.</summary>
    string Id { get; }
    /// <summary>Gets the immutable model/prompt/tool/configuration fingerprint.</summary>
    string VersionHash { get; }
    /// <summary>Produces one proposal and its actual same-unit resource receipt.</summary>
    ValueTask<EvolutionResourceResult<TGenome>> ProposeAsync(EvolutionVariationContext<TGenome> context, CancellationToken cancellationToken = default);
    /// <summary>Receives exactly one terminal committed outcome for each dispatched proposal, including failures.</summary>
    void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult);
    /// <summary>Captures all deterministic backend state, including any pending learning attribution.</summary>
    string CaptureState();
    /// <summary>Restores compatible state or rejects malformed/incompatible input.</summary>
    void RestoreState(string state);
}

/// <summary>Explicitly permits overlapping snapshot-local costed proposal callbacks while learning remains serialized.</summary>
/// <typeparam name="TGenome">The immutable genome produced by the backend.</typeparam>
/// <remarks>Callbacks must not mutate shared learning/random state or depend on arrival order. Source VersionHash
/// must describe this capability and all external response semantics. The resource adapter enables parallelism only
/// inside an engine-coordinated, pre-reserved pipeline phase; ordinary direct adapter calls remain serialized.</remarks>
public interface IDeterministicConcurrentCostedEvolutionProposalSource<TGenome> : ICostedEvolutionProposalSource<TGenome>
{
    /// <summary>Gets whether the configured backend supports deterministic overlapping proposal callbacks.</summary>
    bool SupportsDeterministicConcurrency { get; }
}

/// <summary>Supplies owned pending proposal costs before terminal outcome feedback consumes them.</summary>
/// <remarks>The source must also implement checkpointable variation so pending costs participate in replay.</remarks>
public interface IEvolutionProposalCostProvider
{
    /// <summary>Gets the declared cost_units conversion/measurement semantics, shared with the evaluator and credit policy.</summary>
    string CostUnitVersionHash { get; }
    /// <summary>Gets the complete proposal receipt for this generation or rejects unknown/already consumed attribution.</summary>
    EvolutionProposalCost GetProposalCost(long generation);
}
