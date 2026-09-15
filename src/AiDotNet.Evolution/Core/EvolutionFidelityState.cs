namespace AiDotNet.Evolution;

/// <summary>A measurement receipt and optional small opaque continuation token, not an executable checkpoint.</summary>
/// <remarks>Large model state belongs in a consumer-owned verified store; the token can identify that state and its content hash.
/// The consumer must validate external storage, enforce isolation and report actual incremental or full-restart costs.</remarks>
public sealed class EvolutionFidelityEvaluationResult
{
    private readonly byte[]? _token;
    /// <summary>Creates an outcome with an independently owned continuation token of at most 4096 bytes.</summary>
    public EvolutionFidelityEvaluationResult(EvolutionTaskResult measurement, byte[]? continuationToken = null, string? stateVersionHash = null)
    {
        Guard.NotNull(measurement);
        if (continuationToken is not null)
        {
            if (continuationToken.Length is < 1 or > 4096) throw new ArgumentException("Continuation token must contain 1..4096 bytes.", nameof(continuationToken));
            Guard.NotNullOrWhiteSpace(stateVersionHash);
            if (stateVersionHash!.Length > 128 || stateVersionHash.Any(char.IsControl)) throw new ArgumentException("Invalid state version.", nameof(stateVersionHash));
            _token = (byte[])continuationToken.Clone();
        }
        else if (stateVersionHash is not null) throw new ArgumentException("A state version requires a continuation token.", nameof(stateVersionHash));
        Measurement = measurement; StateVersionHash = stateVersionHash;
    }
    /// <summary>Gets the actual same-unit measurement receipt.</summary>
    public EvolutionTaskResult Measurement { get; }
    /// <summary>Gets the token's consumer-defined compatibility version, if present.</summary>
    public string? StateVersionHash { get; }
    /// <summary>Gets an independently owned copy of the token; no payload is emitted by ordinary property serialization.</summary>
    public byte[]? CopyContinuationToken() => _token is null ? null : (byte[])_token.Clone();
}

/// <summary>An opaque continuation token bound by the scheduler to one genome, replicate and measured source level.</summary>
public sealed class EvolutionFidelityResumeState
{
    private readonly byte[] _token;
    internal EvolutionFidelityResumeState(string genomeId, string evaluatorVersionHash, string stateVersionHash,
        EvolutionFidelityLevel sourceLevel, EvolutionReplicateContext sample, byte[] token)
    {
        GenomeId = genomeId; EvaluatorVersionHash = evaluatorVersionHash; StateVersionHash = stateVersionHash;
        SourceLevel = sourceLevel; ReplicateIndex = sample.Index; SourceSampleIdentity = sample.SampleIdentity;
        _token = (byte[])token.Clone();
        TokenHash = EvolutionHash.Combine(new[] { stateVersionHash, Convert.ToBase64String(_token) });
    }
    /// <summary>Gets the exact candidate identity whose evaluator state may resume.</summary>
    public string GenomeId { get; }
    /// <summary>Gets the source evaluator/data/environment identity.</summary>
    public string EvaluatorVersionHash { get; }
    /// <summary>Gets consumer token compatibility semantics.</summary>
    public string StateVersionHash { get; }
    /// <summary>Gets the measured cumulative source fidelity.</summary>
    public EvolutionFidelityLevel SourceLevel { get; }
    /// <summary>Gets the replicate index; independent replicate states cannot cross over.</summary>
    public int ReplicateIndex { get; }
    /// <summary>Gets the exact originating measurement identity.</summary>
    public string SourceSampleIdentity { get; }
    /// <summary>Gets the opaque payload fingerprint, not the payload or a proof of external state validity.</summary>
    public string TokenHash { get; }
    /// <summary>Gets an independently owned token copy for the consumer to validate and use.</summary>
    public byte[] CopyToken() => (byte[])_token.Clone();
}

/// <summary>Inputs to one actual fidelity-specific measurement.</summary>
public sealed class EvolutionFidelityEvaluationContext
{
    internal EvolutionFidelityEvaluationContext(EvolutionFidelityLevel level, EvolutionReplicateContext replicate, EvolutionFidelityResumeState? resume)
    { Level = level; Replicate = replicate; Resume = resume; }
    /// <summary>Gets the requested cumulative fidelity.</summary>
    public EvolutionFidelityLevel Level { get; }
    /// <summary>Gets the full measurement identity, purpose and fresh stream.</summary>
    public EvolutionReplicateContext Replicate { get; }
    /// <summary>Gets validated-boundary search state, or null for a restart and always for independent confirmation.</summary>
    public EvolutionFidelityResumeState? Resume { get; }
}
