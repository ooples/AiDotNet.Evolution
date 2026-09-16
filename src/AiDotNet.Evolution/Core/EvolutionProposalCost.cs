namespace AiDotNet.Evolution;

/// <summary>Actual or conservative proposal-only costs, separate from evaluator attempts.</summary>
public sealed class EvolutionProposalCost
{
    /// <summary>Creates explicit proposal cost evidence; providers must preserve the corresponding ledger receipt.</summary>
    public EvolutionProposalCost(string operationId, EvolutionResources charged, EvolutionResourceOutcome outcome, bool exceededMaximum = false)
    {
        Guard.NotNullOrWhiteSpace(operationId); Guard.NotNull(charged);
        if (!charged.Amounts.ContainsKey("cost_units")) throw new ArgumentException("Explicit proposal cost_units are required, including zero.", nameof(charged));
        if (operationId.Length > 256 || operationId.Any(char.IsControl)) throw new ArgumentException("Invalid cost operation identity.", nameof(operationId));
        if (!Enum.IsDefined(typeof(EvolutionResourceOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        if (exceededMaximum && outcome is EvolutionResourceOutcome.Completed or EvolutionResourceOutcome.Unknown)
            throw new ArgumentException("An overrun requires a known unsuccessful receipt.", nameof(exceededMaximum));
        OperationId = operationId; Charged = charged; Outcome = outcome; ExceededMaximum = exceededMaximum;
    }
    /// <summary>Gets the operation identity bound to this operator configuration and generation.</summary>
    public string OperationId { get; }
    /// <summary>Gets owned actual resource amounts, or the reserved maximum when consumption is unknown.</summary>
    public EvolutionResources Charged { get; }
    /// <summary>Gets whether dispatched work succeeded, failed or has unknown consumption.</summary>
    public EvolutionResourceOutcome Outcome { get; }
    /// <summary>Gets whether consumption is conservatively bounded instead of actually reported.</summary>
    public bool IsUnknown => Outcome == EvolutionResourceOutcome.Unknown;
    /// <summary>Gets whether the producer's actual receipt exceeded its declared maximum.</summary>
    public bool ExceededMaximum { get; }
}
