namespace AiDotNet.Evolution;

/// <summary>Fences one external delivery by run, evaluation, attempt and an opaque lease token.</summary>
/// <remarks>A token correlates trusted work; it is not authentication or proof that physical work was canceled.</remarks>
public sealed class EvolutionWorkIdentity
{
    /// <summary>Creates an immutable transport identity. Local sessions issue a fresh token for every attempt.</summary>
    public EvolutionWorkIdentity(string runId, long evaluationId, int attempt, string leaseId)
    {
        Guard.NotNullOrWhiteSpace(runId); Guard.NotNullOrWhiteSpace(leaseId);
        if (runId.Length > 1024) throw new ArgumentOutOfRangeException(nameof(runId));
        if (evaluationId < 0) throw new ArgumentOutOfRangeException(nameof(evaluationId));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (leaseId.Length != 32 || leaseId.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Lease identity must be 32 lowercase hexadecimal characters.", nameof(leaseId));
        RunId = runId; EvaluationId = evaluationId; Attempt = attempt; LeaseId = leaseId;
    }
    /// <summary>Gets the caller's run identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the stable candidate allocation identity.</summary>
    public long EvaluationId { get; }
    /// <summary>Gets the one-based engine attempt.</summary>
    public int Attempt { get; }
    /// <summary>Gets the opaque delivery token; it must be returned unchanged.</summary>
    public string LeaseId { get; }
    internal bool Matches(EvolutionWorkIdentity other) => RunId == other.RunId && EvaluationId == other.EvaluationId &&
        Attempt == other.Attempt && LeaseId == other.LeaseId;
}

/// <summary>Caller-owned task/canonicalizer and evaluator fingerprints for an external evaluation session.</summary>
/// <remarks>TaskVersionHash must cover the canonicalizer and representation/codec semantics. Constant display names are not version evidence.</remarks>
public sealed class EvolutionExternalTaskIdentity
{
    /// <summary>Creates a bounded external task compatibility contract.</summary>
    public EvolutionExternalTaskIdentity(string taskId, string taskVersionHash, string evaluatorVersionHash)
    {
        Guard.NotNullOrWhiteSpace(taskId); Guard.NotNullOrWhiteSpace(taskVersionHash); Guard.NotNullOrWhiteSpace(evaluatorVersionHash);
        if (taskId.Length > 1024 || taskVersionHash.Length > 1024 || evaluatorVersionHash.Length > 1024)
            throw new ArgumentException("External task identities must be bounded to 1024 characters.");
        TaskId = taskId; TaskVersionHash = taskVersionHash; EvaluatorVersionHash = evaluatorVersionHash;
    }
    /// <summary>Gets the task identifier.</summary>
    public string TaskId { get; }
    /// <summary>Gets the task, canonicalizer and representation version.</summary>
    public string TaskVersionHash { get; }
    /// <summary>Gets the evaluator/environment protocol version.</summary>
    public string EvaluatorVersionHash { get; }
}
