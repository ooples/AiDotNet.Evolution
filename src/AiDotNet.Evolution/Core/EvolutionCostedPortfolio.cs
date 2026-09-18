namespace AiDotNet.Evolution;

/// <summary>One consumer-supplied proposal strategy with a declared admission bound.</summary>
/// <typeparam name="TGenome">The immutable proposal genome.</typeparam>
public sealed class EvolutionCostedPortfolioArm<TGenome>
{
    /// <summary>Creates an arm; the source must include all proposal work in its actual receipt.</summary>
    public EvolutionCostedPortfolioArm(ICostedEvolutionProposalSource<TGenome> source, EvolutionResources maximum)
    {
        Guard.NotNull(source); Guard.NotNull(maximum);
        Source = source; Maximum = maximum;
    }

    /// <summary>Gets the versioned mutation, crossover, restart, refinement or model backend.</summary>
    public ICostedEvolutionProposalSource<TGenome> Source { get; }
    /// <summary>Gets the maximum reserved before invoking the backend.</summary>
    public EvolutionResources Maximum { get; }
}

/// <summary>Builds optional cost-aware portfolios whose arms share one resource ledger.</summary>
public static class EvolutionCostedPortfolio
{
    /// <summary>Wraps every source with admission and receipt accounting on the same ledger.</summary>
    /// <remarks>Use this ledger for the evaluator too. Backend receipts include proposal-time model, parsing,
    /// repair and refinement work, not the subsequent evaluation. Quality and cost scales are fixed before
    /// search. This factory does not execute untrusted code or supply model credentials. At a coordinated
    /// engine checkpoint boundary, persist and restore the ledger together with the engine checkpoint.</remarks>
    public static AdaptiveVariationPortfolio<TGenome> Create<TGenome>(
        IEnumerable<EvolutionCostedPortfolioArm<TGenome>> arms, EvolutionResourceLedger ledger,
        EvolutionOperatorRewardPolicy rewardPolicy, double explorationProbability = 0.1)
    {
        Guard.NotNull(arms); Guard.NotNull(ledger); Guard.NotNull(rewardPolicy);
        if (rewardPolicy.CostBasis != EvolutionOperatorCostBasis.ProposalAndEvaluation)
            throw new ArgumentException("A metered portfolio requires proposal and evaluation costs.", nameof(rewardPolicy));
        var materialized = arms.Take(257).ToArray();
        if (materialized.Length is < 1 or > 256 || materialized.Any(arm => arm is null))
            throw new ArgumentException("One to 256 non-null arms are required.", nameof(arms));
        return new AdaptiveVariationPortfolio<TGenome>(materialized.Select(arm =>
            new ResourceMeteredVariationOperator<TGenome>(arm.Source, ledger, arm.Maximum, rewardPolicy.CostUnitVersionHash)),
            rewardPolicy, explorationProbability);
    }
}
