namespace AiDotNet.Evolution;

/// <summary>Detached attribution for one terminal committed proposal; diagnostic evidence, never hidden learning input.</summary>
public sealed class EvolutionOperatorCredit
{
    internal EvolutionOperatorCredit(long generation, string operatorId, string operatorVersion, string policyVersion,
        double? parentQuality, EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertion,
        EvolutionProposalCost? proposalCost, double reward)
    {
        Generation = generation; OperatorId = operatorId; OperatorVersionHash = operatorVersion; PolicyVersionHash = policyVersion;
        ParentQuality = parentQuality; Quality = evaluation.Quality; Direction = evaluation.Direction; Status = evaluation.Status;
        CacheStatus = evaluation.CacheStatus; Insertion = insertion; EvaluationCostUnits = evaluation.Cost.CostUnits;
        ProposalCost = proposalCost; Reward = reward;
    }
    /// <summary>Gets the proposal generation, independent of worker completion order.</summary>
    public long Generation { get; }
    /// <summary>Gets the selected child operator, not only the containing portfolio.</summary>
    public string OperatorId { get; }
    /// <summary>Gets the selected child's model/prompt/tool/configuration identity.</summary>
    public string OperatorVersionHash { get; }
    /// <summary>Gets fixed gain, normalization and included-cost semantics.</summary>
    public string PolicyVersionHash { get; }
    /// <summary>Gets the proposal-time parent baseline when an explicit policy captures it.</summary>
    public double? ParentQuality { get; }
    /// <summary>Gets the terminal candidate quality, if available.</summary>
    public double? Quality { get; }
    /// <summary>Gets the candidate's scalar direction.</summary>
    public EvolutionOptimizationDirection Direction { get; }
    /// <summary>Gets the terminal outcome, including failures and rejected work.</summary>
    public EvolutionEvaluationStatus Status { get; }
    /// <summary>Gets cache provenance; cached successes never receive credit.</summary>
    public EvolutionCacheStatus CacheStatus { get; }
    /// <summary>Gets the committed archive insertion decision, if applicable.</summary>
    public EvolutionArchiveInsertionResult? Insertion { get; }
    /// <summary>Gets accumulated evaluator attempt charges, including any declared conservative charges.</summary>
    public double EvaluationCostUnits { get; }
    /// <summary>Gets proposal resource evidence only when included by the selected policy; null does not mean free work.</summary>
    public EvolutionProposalCost? ProposalCost { get; }
    /// <summary>Gets the bounded [0,1] credit actually applied to this arm.</summary>
    public double Reward { get; }
}
