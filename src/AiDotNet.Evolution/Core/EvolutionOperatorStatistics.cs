namespace AiDotNet.Evolution;

/// <summary>An immutable snapshot of an adaptive portfolio arm's proposal attribution and reward.</summary>
public sealed class EvolutionOperatorStatistics
{
    internal EvolutionOperatorStatistics(string operatorId, long proposals, long outcomes, double rewardSum)
    {
        OperatorId = operatorId;
        Proposals = proposals;
        Outcomes = outcomes;
        RewardSum = rewardSum;
    }

    /// <summary>Gets the child variation operator's identity.</summary>
    public string OperatorId { get; }
    /// <summary>Gets all proposals assigned to this operator, including failed and pending proposals.</summary>
    public long Proposals { get; }
    /// <summary>Gets the number of terminal outcomes committed for this operator.</summary>
    public long Outcomes { get; }
    /// <summary>Gets accumulated bounded, cost-normalized reward under the portfolio's declared policy.</summary>
    public double RewardSum { get; }
    /// <summary>Gets reward per committed outcome, or zero before the first outcome.</summary>
    public double MeanReward => Outcomes == 0 ? 0 : RewardSum / Outcomes;
}
