namespace AiDotNet.Evolution;

/// <summary>Frozen limits and predeclared family-level acceptance gates for one opt-in offline campaign.</summary>
public sealed class EvolutionPolicyOptimizationOptions
{
    /// <summary>Creates a bounded campaign with independent development and held-out panels.</summary>
    /// <remarks>Campaign resources cap new work; separately reported historical baseline tuning costs are not charged twice.</remarks>
    public EvolutionPolicyOptimizationOptions(EvolutionPolicyTrialBudget trialBudget, EvolutionResources campaignResources,
        int maximumDevelopmentPolicies = 16, int maximumOuterProposals = 64, int replicates = 5,
        int maximumInnerTrials = 1024, ulong seed = 1234, TimeSpan? timeout = null,
        double minimumMeanGain = 0, double maximumPValue = 0.05, double maximumWithinTaskRange = 0.5,
        int maximumInlineEvidenceBytes = 16 * 1024 * 1024)
    {
        TrialBudget = trialBudget ?? throw new ArgumentNullException(nameof(trialBudget));
        CampaignResources = campaignResources ?? throw new ArgumentNullException(nameof(campaignResources));
        if (maximumDevelopmentPolicies < 3 || maximumDevelopmentPolicies > 128) throw new ArgumentOutOfRangeException(nameof(maximumDevelopmentPolicies));
        if (maximumOuterProposals < maximumDevelopmentPolicies || maximumOuterProposals > 4096) throw new ArgumentOutOfRangeException(nameof(maximumOuterProposals));
        if (replicates < 2 || replicates > 32) throw new ArgumentOutOfRangeException(nameof(replicates));
        if (maximumInnerTrials < 1 || maximumInnerTrials > 4096) throw new ArgumentOutOfRangeException(nameof(maximumInnerTrials));
        if (maximumInlineEvidenceBytes < 128 * 1024 || maximumInlineEvidenceBytes > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumInlineEvidenceBytes));
        if (!trialBudget.MaximumResources.Amounts.Keys.SequenceEqual(campaignResources.Amounts.Keys))
            throw new ArgumentException("Inner and campaign ledgers must declare the same producer resource units.", nameof(campaignResources));
        TimeSpan duration = timeout ?? TimeSpan.FromMinutes(10);
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!Finite(minimumMeanGain) || minimumMeanGain < 0 || minimumMeanGain > 1 ||
            !Finite(maximumPValue) || maximumPValue <= 0 || maximumPValue >= 0.5 ||
            !Finite(maximumWithinTaskRange) || maximumWithinTaskRange < 0 || maximumWithinTaskRange > 1)
            throw new ArgumentException("Declare finite practical, significance and stability thresholds.");
        MaximumDevelopmentPolicies = maximumDevelopmentPolicies; MaximumOuterProposals = maximumOuterProposals;
        Replicates = replicates; MaximumInnerTrials = maximumInnerTrials; Seed = seed; Timeout = duration;
        MinimumMeanGain = minimumMeanGain; MaximumPValue = maximumPValue; MaximumWithinTaskRange = maximumWithinTaskRange;
        MaximumInlineEvidenceBytes = maximumInlineEvidenceBytes;
    }
    /// <summary>Gets fixed per-inner-trial allowances shared with all baselines.</summary>
    public EvolutionPolicyTrialBudget TrialBudget { get; }
    /// <summary>Gets total same-unit producer limits for new campaign work.</summary>
    public EvolutionResources CampaignResources { get; }
    /// <summary>Gets the maximum development policies, including registered baselines.</summary>
    public int MaximumDevelopmentPolicies { get; }
    /// <summary>Gets the proposal-attempt bound, including duplicate or unselected recipes.</summary>
    public int MaximumOuterProposals { get; }
    /// <summary>Gets independent predeclared seed replicates per task.</summary>
    public int Replicates { get; }
    /// <summary>Gets the total inner-trial and retained-observation bound.</summary>
    public int MaximumInnerTrials { get; }
    /// <summary>Gets the accumulated inline evidence admission limit; the final rejected receipt may add at most another 128 KiB.</summary>
    /// <remarks>This bounds raw UTF-8 evidence, not total process memory or JSON escaping overhead.</remarks>
    public int MaximumInlineEvidenceBytes { get; }
    /// <summary>Gets the reproducible campaign seed.</summary>
    public ulong Seed { get; }
    /// <summary>Gets the total cooperative campaign deadline.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>Gets the practical minimum normalized gain required against every baseline.</summary>
    public double MinimumMeanGain { get; }
    /// <summary>Gets the family-wise error allowance, divided by the predeclared baseline count.</summary>
    public double MaximumPValue { get; }
    /// <summary>Gets the maximum held-out replicate range within any one task, for both candidate and baselines.</summary>
    public double MaximumWithinTaskRange { get; }
    private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
