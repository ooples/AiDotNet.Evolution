using System.Collections.ObjectModel;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>One auditable policy/task/seed execution, including failed, denied and abandoned work.</summary>
public sealed class EvolutionPolicyTrialRecord
{
    internal EvolutionPolicyTrialRecord(int sequence, EvolutionPolicyTrialPhase phase, EvolutionPolicyTrial task, EvolutionSearchPolicy policy,
        int replicate, ulong seed, TimeSpan elapsed, EvolutionResourceOutcome outcome,
        EvolutionResources charged, EvolutionPolicyObservation? observation, string? failureCode)
    {
        Sequence = sequence; Phase = phase; TaskId = task.Id; Family = task.Family; TaskVersionHash = task.VersionHash;
        PolicyId = policy.Id; Replicate = replicate; Seed = seed; Elapsed = elapsed; Outcome = outcome;
        ChargedResources = charged; Observation = observation; FailureCode = failureCode;
    }
    /// <summary>Gets the zero-based deterministic dispatch order.</summary>
    public int Sequence { get; }
    /// <summary>Gets development or holdout; only development observations influence candidate selection.</summary>
    public EvolutionPolicyTrialPhase Phase { get; }
    /// <summary>Gets the task identity.</summary>
    public string TaskId { get; }
    /// <summary>Gets the predeclared independent task family.</summary>
    public string Family { get; }
    /// <summary>Gets the task/data/normalization fingerprint.</summary>
    public string TaskVersionHash { get; }
    /// <summary>Gets the tested policy identity.</summary>
    public string PolicyId { get; }
    /// <summary>Gets the replicate ordinal within the fixed task panel.</summary>
    public int Replicate { get; }
    /// <summary>Gets the actual common root seed shared by policy and baseline trials.</summary>
    public ulong Seed { get; }
    /// <summary>Gets caller-observed elapsed time, including setup and cleanup; it is not used as a deterministic score.</summary>
    public TimeSpan Elapsed { get; }
    /// <summary>Gets the accounting outcome; unknown charges are conservative upper bounds, not measured costs.</summary>
    public EvolutionResourceOutcome Outcome { get; }
    /// <summary>Gets the actual charge, or the explicit maximum when consumption is unknown.</summary>
    public EvolutionResources ChargedResources { get; }
    /// <summary>Gets the returned receipt and underlying evidence, if the provider returned one before its deadline.</summary>
    public EvolutionPolicyObservation? Observation { get; }
    /// <summary>Gets a bounded exception type/code without copying potentially sensitive exception messages.</summary>
    public string? FailureCode { get; }
}

/// <summary>One equal-weight held-out family comparison, not one pseudo-independent seed replicate.</summary>
public sealed class EvolutionPolicyFamilyGain
{
    internal EvolutionPolicyFamilyGain(string family, double candidate, double baseline)
    { Family = family; CandidateMean = candidate; BaselineMean = baseline; Gain = candidate - baseline; }
    /// <summary>Gets the independent family label.</summary>
    public string Family { get; }
    /// <summary>Gets the candidate's within-family mean normalized utility.</summary>
    public double CandidateMean { get; }
    /// <summary>Gets the baseline's within-family mean normalized utility.</summary>
    public double BaselineMean { get; }
    /// <summary>Gets candidate minus baseline utility.</summary>
    public double Gain { get; }
}

/// <summary>Predeclared held-out comparison against one baseline, corrected for the registered baseline family.</summary>
public sealed class EvolutionPolicyBaselineComparison
{
    internal EvolutionPolicyBaselineComparison(EvolutionPolicyBaseline baseline, EvolutionPolicyFamilyGain[] families,
        double maximumPValue, double minimumMeanGain, bool stable)
    {
        BaselineName = baseline.Name; BaselinePolicyId = baseline.Policy.Id; Families = Array.AsReadOnly(families);
        int wins = families.Count(value => value.Gain > 0), nonTies = families.Count(value => value.Gain != 0);
        double probability = Math.Pow(0.5, nonTies), tail = 0;
        for (int k = 0; k <= nonTies; k++)
        {
            if (k >= wins) tail += probability;
            if (k < nonTies) probability *= (nonTies - k) / (k + 1d);
        }
        SignTestPValue = tail; CorrectedThreshold = maximumPValue; MeanGain = families.Average(value => value.Gain);
        Stable = stable; Passed = stable && MeanGain > 0 && MeanGain >= minimumMeanGain && tail <= maximumPValue;
    }
    /// <summary>Gets the predeclared baseline name.</summary>
    public string BaselineName { get; }
    /// <summary>Gets its frozen policy identity.</summary>
    public string BaselinePolicyId { get; }
    /// <summary>Gets raw equal-weight family comparisons; replicates are not counted as independent families.</summary>
    public IReadOnlyList<EvolutionPolicyFamilyGain> Families { get; }
    /// <summary>Gets the one-sided exact sign-test probability, excluding ties.</summary>
    public double SignTestPValue { get; }
    /// <summary>Gets the campaign alpha divided by the number of predeclared baselines.</summary>
    public double CorrectedThreshold { get; }
    /// <summary>Gets the mean gain across independent families.</summary>
    public double MeanGain { get; }
    /// <summary>Gets whether both candidate and baseline satisfy within-task replicate stability.</summary>
    public bool Stable { get; }
    /// <summary>Gets whether practical, stability and corrected significance gates all passed.</summary>
    public bool Passed { get; }
}

/// <summary>A detached campaign result; a suggested policy is never automatically activated in production.</summary>
public sealed class EvolutionPolicyOptimizationReport
{
    internal EvolutionPolicyOptimizationReport(string planHash, EvolutionPolicyCampaignOutcome outcome, EvolutionPolicyOptimizationOptions options,
        EvolutionPolicySpace space, EvolutionPolicyTrial[] developmentTasks, EvolutionPolicyTrial[] heldOutTasks,
        EvolutionSearchPolicy stablePreset, EvolutionSearchPolicy? champion, EvolutionPolicyBaseline[] baselines,
        EvolutionPolicyTrialRecord[] trials, EvolutionPolicyBaselineComparison[] comparisons,
        EvolutionResourceSnapshot resources, TimeSpan elapsed, TimeSpan innerElapsed, string? failureCode)
    {
        PlanHash = planHash; Outcome = outcome; Options = options; Space = space;
        DevelopmentTasks = Array.AsReadOnly(developmentTasks); HeldOutTasks = Array.AsReadOnly(heldOutTasks);
        StablePreset = stablePreset; DevelopmentChampion = champion;
        Baselines = Array.AsReadOnly(baselines); Trials = Array.AsReadOnly(trials); Comparisons = Array.AsReadOnly(comparisons);
        Resources = resources; Elapsed = elapsed; InnerElapsed = innerElapsed; OuterElapsed = elapsed > innerElapsed ? elapsed - innerElapsed : TimeSpan.Zero;
        FailureCode = failureCode;
        GeneralizationPassed = outcome == EvolutionPolicyCampaignOutcome.GeneralizationPassed && comparisons.Length == baselines.Length && comparisons.All(value => value.Passed);
        SuggestedPolicy = GeneralizationPassed ? champion! : stablePreset;
        var prior = new SortedDictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var baseline in baselines)
            foreach (var item in baseline.PriorTuningResources.Amounts)
                prior[item.Key] = (prior.TryGetValue(item.Key, out decimal value) ? value : 0) + item.Value;
        PriorTuningResources = new ReadOnlyDictionary<string, decimal>(prior);
    }
    /// <summary>Gets the immutable plan identity, excluding measured outcomes and wall-clock timing.</summary>
    public string PlanHash { get; }
    /// <summary>Gets the terminal disposition, including inconclusive, denied and abandoned campaigns.</summary>
    public EvolutionPolicyCampaignOutcome Outcome { get; }
    /// <summary>Gets the exact predeclared limits and thresholds.</summary>
    public EvolutionPolicyOptimizationOptions Options { get; }
    /// <summary>Gets the complete finite catalogue needed to reproduce proposal sampling.</summary>
    public EvolutionPolicySpace Space { get; }
    /// <summary>Gets predeclared development task metadata; private executable delegates are not serialized.</summary>
    public IReadOnlyList<EvolutionPolicyTrial> DevelopmentTasks { get; }
    /// <summary>Gets predeclared held-out task metadata, including tasks not run after an early stop.</summary>
    public IReadOnlyList<EvolutionPolicyTrial> HeldOutTasks { get; }
    /// <summary>Gets the caller-declared stable fallback.</summary>
    public EvolutionSearchPolicy StablePreset { get; }
    /// <summary>Gets the candidate selected strictly on development data, if any.</summary>
    public EvolutionSearchPolicy? DevelopmentChampion { get; }
    /// <summary>Gets the separately declared baseline designs, provenance and historical costs.</summary>
    public IReadOnlyList<EvolutionPolicyBaseline> Baselines { get; }
    /// <summary>Gets every dispatched/denied trial in deterministic dispatch order.</summary>
    public IReadOnlyList<EvolutionPolicyTrialRecord> Trials { get; }
    /// <summary>Gets all predeclared held-out baseline comparisons.</summary>
    public IReadOnlyList<EvolutionPolicyBaselineComparison> Comparisons { get; }
    /// <summary>Gets complete new-work totals and retained resource receipts, including unknown upper-bound charges.</summary>
    public EvolutionResourceSnapshot Resources { get; }
    /// <summary>Gets historical baseline tuning charges separately, so they are reported without being charged twice.</summary>
    public IReadOnlyDictionary<string, decimal> PriorTuningResources { get; }
    /// <summary>Gets total caller-observed campaign elapsed time.</summary>
    public TimeSpan Elapsed { get; }
    /// <summary>Gets total time spent awaiting inner trials, including their setup and cleanup.</summary>
    public TimeSpan InnerElapsed { get; }
    /// <summary>Gets remaining campaign orchestration/analysis wall time; this is not inferred CPU time or money.</summary>
    public TimeSpan OuterElapsed { get; }
    /// <summary>Gets whether the selected candidate passed this campaign's independent family-level protocol.</summary>
    public bool GeneralizationPassed { get; }
    /// <summary>Gets the candidate only after every gate passes, otherwise the unchanged stable preset.</summary>
    public EvolutionSearchPolicy SuggestedPolicy { get; }
    /// <summary>Gets a bounded failure type/code when a campaign could not complete.</summary>
    public string? FailureCode { get; }
    /// <summary>Serializes the complete detached report for caller-owned offline retention.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, EvolutionJson.Compact);
}
