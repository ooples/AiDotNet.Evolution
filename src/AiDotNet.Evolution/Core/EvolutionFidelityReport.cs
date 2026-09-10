namespace AiDotNet.Evolution;

/// <summary>Why a bounded promotion bracket ended.</summary>
public enum EvolutionFidelityStopReason
{
    /// <summary>All planned search levels and final confirmation batches were processed.</summary>
    Completed,
    /// <summary>No feasible complete candidate remained at a search level.</summary>
    NoEligibleCandidates,
    /// <summary>Final independent confirmation produced no complete feasible candidate.</summary>
    NoConfirmedCandidate,
    /// <summary>The ledger refused another measurement before dispatch.</summary>
    BudgetExhausted,
    /// <summary>Cancellation ended the bracket; dispatched work remains charged.</summary>
    Canceled,
    /// <summary>A producer exceeded its declared measurement maximum.</summary>
    MaximumCostExceeded
}

/// <summary>One genome's actual replicate batch at an identified fidelity and purpose.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
public sealed class EvolutionFidelityBatch<TGenome>
{
    internal EvolutionFidelityBatch(EvolutionCanonicalGenome<TGenome> candidate, EvolutionFidelityLevel level,
        EvolutionReplicationPurpose purpose, EvolutionReplicationReport measurements, IEnumerable<string?> priorSamples,
        int acceptedContinuationTokens, int rejectedContinuationTokens)
    {
        Candidate = candidate; Level = level; Purpose = purpose; Measurements = measurements;
        ResumedFromSampleIdentities = Array.AsReadOnly(priorSamples.ToArray());
        AcceptedContinuationTokens = acceptedContinuationTokens; RejectedContinuationTokens = rejectedContinuationTokens;
    }
    /// <summary>Gets the exact candidate identity and owned genome.</summary>
    public EvolutionCanonicalGenome<TGenome> Candidate { get; }
    /// <summary>Gets cumulative resource semantics and per-replicate upper bound.</summary>
    public EvolutionFidelityLevel Level { get; }
    /// <summary>Gets search or independent-confirmation purpose.</summary>
    public EvolutionReplicationPurpose Purpose { get; }
    /// <summary>Gets all actual measurements, costs and incomplete-batch reasons.</summary>
    /// <remarks>Resumed/adaptively selected search batches need not satisfy IID assumptions; their intervals are
    /// exploratory, not selection-corrected confidence or deployment evidence.</remarks>
    public EvolutionReplicationReport Measurements { get; }
    /// <summary>Gets each dispatched replicate's prior sample identity, or null for a fresh start.</summary>
    public IReadOnlyList<string?> ResumedFromSampleIdentities { get; }
    /// <summary>Gets tokens adopted only from a complete successful search batch.</summary>
    public int AcceptedContinuationTokens { get; }
    /// <summary>Gets returned search tokens discarded for incompatible state version or incomplete measurement batches.</summary>
    public int RejectedContinuationTokens { get; }
}

/// <summary>An auditable allocation decision from one measured level to the next.</summary>
public sealed class EvolutionFidelityPromotion
{
    internal EvolutionFidelityPromotion(string genomeId, string sourceBatchIdentity, string from, string to, int rank, bool exploration)
    { GenomeId = genomeId; SourceBatchIdentity = sourceBatchIdentity; FromLevel = from; ToLevel = to; Rank = rank; IsExploration = exploration; }
    /// <summary>Gets the promoted genome identity.</summary>
    public string GenomeId { get; }
    /// <summary>Gets the actual measured batch supporting this decision.</summary>
    public string SourceBatchIdentity { get; }
    /// <summary>Gets source fidelity identity.</summary>
    public string FromLevel { get; }
    /// <summary>Gets requested next fidelity identity.</summary>
    public string ToLevel { get; }
    /// <summary>Gets one-based quality rank among valid candidates at this level.</summary>
    public int Rank { get; }
    /// <summary>Gets whether this survivor was sampled from outside the ordinary greedy survivor set.</summary>
    public bool IsExploration { get; }
}

/// <summary>Failure-inclusive bracket evidence, not a deployment or statistical-superiority authorization.</summary>
/// <typeparam name="TGenome">The immutable genome representation.</typeparam>
public sealed class EvolutionFidelityReport<TGenome>
{
    internal EvolutionFidelityReport(string identity, string schedulerVersion, string searchVersion, string confirmationVersion,
        IEnumerable<string> initialCandidateIds, EvolutionFidelityPlan plan, EvolutionFidelityStopReason stop,
        IEnumerable<EvolutionFidelityBatch<TGenome>> batches, IEnumerable<EvolutionFidelityPromotion> promotions,
        EvolutionResourceSnapshot resources)
    {
        RunIdentity = identity; SchedulerVersionHash = schedulerVersion; SearchEvaluatorVersionHash = searchVersion;
        ConfirmationEvaluatorVersionHash = confirmationVersion; InitialCandidateIds = Array.AsReadOnly(initialCandidateIds.ToArray());
        Plan = plan; StopReason = stop; Batches = Array.AsReadOnly(batches.ToArray());
        Promotions = Array.AsReadOnly(promotions.ToArray()); Resources = resources;
        ChargedCostUnits = Batches.Sum(batch => batch.Measurements.ChargedCostUnits);
        if (stop == EvolutionFidelityStopReason.Completed)
        {
            var confirmed = Batches.Where(batch => batch.Purpose == EvolutionReplicationPurpose.Confirmation && batch.Measurements.IsComplete);
            BestConfirmed = (plan.Direction == EvolutionOptimizationDirection.Maximize
                ? confirmed.OrderByDescending(batch => batch.Measurements.MeanQuality)
                : confirmed.OrderBy(batch => batch.Measurements.MeanQuality)).ThenBy(batch => batch.Candidate.Id, StringComparer.Ordinal).FirstOrDefault();
        }
    }
    /// <summary>Gets the identity binding input candidates, seed, plan and evaluator versions.</summary>
    public string RunIdentity { get; }
    /// <summary>Gets the scheduler/plan/callback configuration fingerprint.</summary>
    public string SchedulerVersionHash { get; }
    /// <summary>Gets the declared search evaluator/data/environment version.</summary>
    public string SearchEvaluatorVersionHash { get; }
    /// <summary>Gets the declared independent confirmation evaluator/data/environment version.</summary>
    public string ConfirmationEvaluatorVersionHash { get; }
    /// <summary>Gets every initial candidate in order, including candidates never dispatched before interruption.</summary>
    public IReadOnlyList<string> InitialCandidateIds { get; }
    /// <summary>Gets the immutable bracket plan.</summary>
    public EvolutionFidelityPlan Plan { get; }
    /// <summary>Gets the terminal bracket reason.</summary>
    public EvolutionFidelityStopReason StopReason { get; }
    /// <summary>Gets whether all levels and confirmation were processed and at least one confirmation completed.</summary>
    public bool IsComplete => StopReason == EvolutionFidelityStopReason.Completed && BestConfirmed is not null;
    /// <summary>Gets every dispatched search/confirmation batch, including failures.</summary>
    public IReadOnlyList<EvolutionFidelityBatch<TGenome>> Batches { get; }
    /// <summary>Gets all greedy/exploratory promotion decisions.</summary>
    public IReadOnlyList<EvolutionFidelityPromotion> Promotions { get; }
    /// <summary>Gets the best fresh full-fidelity confirmation only after bracket completion; still not deployment approval.</summary>
    public EvolutionFidelityBatch<TGenome>? BestConfirmed { get; }
    /// <summary>Gets this bracket's actual/conservative measurement charges, not preexisting shared-ledger work.</summary>
    public decimal ChargedCostUnits { get; }
    /// <summary>Gets the shared ledger snapshot, which can also contain work outside this bracket.</summary>
    public EvolutionResourceSnapshot Resources { get; }
}
