namespace AiDotNet.Evolution;

/// <summary>Fixed inner-run limits shared by every policy and baseline in a campaign.</summary>
public sealed class EvolutionPolicyTrialBudget
{
    /// <summary>Creates finite work, restart, elapsed-time and same-unit resource caps.</summary>
    /// <remarks>Resource names such as cost_units and input_tokens are application-defined units, not inferred prices.</remarks>
    public EvolutionPolicyTrialBudget(int maximumEvaluations, int maximumProposals, int maximumRestarts,
        TimeSpan timeout, EvolutionResources maximumResources)
    {
        Guard.NotNull(maximumResources);
        if (maximumEvaluations < 1 || maximumEvaluations > 100_000) throw new ArgumentOutOfRangeException(nameof(maximumEvaluations));
        if (maximumProposals < maximumEvaluations || maximumProposals > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumProposals));
        if (maximumRestarts < 0 || maximumRestarts > 16) throw new ArgumentOutOfRangeException(nameof(maximumRestarts));
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!maximumResources.Amounts.ContainsKey("cost_units") || maximumResources.Amounts.Count > 28)
            throw new ArgumentException("Declare cost_units and at most twenty-eight producer resources.", nameof(maximumResources));
        if (maximumResources.Amounts.Keys.Any(PolicyResources.IsCounter))
            throw new ArgumentException("Producer resources cannot redefine policy accounting counters.", nameof(maximumResources));
        MaximumEvaluations = maximumEvaluations; MaximumProposals = maximumProposals; MaximumRestarts = maximumRestarts;
        Timeout = timeout; MaximumResources = maximumResources;
        ReservedResources = PolicyResources.WithCounters(maximumResources, maximumEvaluations, maximumProposals, maximumRestarts);
    }
    /// <summary>Gets the engine evaluation-attempt allowance across all restarts.</summary>
    public int MaximumEvaluations { get; }
    /// <summary>Gets the proposal allowance, including seeds and duplicates, across all restarts.</summary>
    public int MaximumProposals { get; }
    /// <summary>Gets the maximum number of fresh restarts after the initial run.</summary>
    public int MaximumRestarts { get; }
    /// <summary>Gets the caller's maximum wait for one inner trial.</summary>
    public TimeSpan Timeout { get; }
    /// <summary>Gets producer resource caps, independent of engine counters.</summary>
    public EvolutionResources MaximumResources { get; }
    internal EvolutionResources ReservedResources { get; }
}

/// <summary>A fresh inner trial result; absent quality and unknown consumption are never fabricated as measured zero.</summary>
public sealed class EvolutionPolicyObservation
{
    /// <summary>Creates a normalized higher-is-better utility and complete same-unit receipt for one inner trial.</summary>
    /// <remarks>The fixed task protocol owns normalization, correctness and the underlying raw evidence identified by evidenceHash.</remarks>
    public EvolutionPolicyObservation(double? utility, EvolutionResources actualResources, string evidenceHash,
        bool valid = true, bool fresh = true, string? diagnostic = null, string? evidenceJson = null, bool unknownConsumption = false)
    {
        Guard.NotNull(actualResources);
        PolicyContract.Hash(evidenceHash);
        if (utility.HasValue && (double.IsNaN(utility.Value) || double.IsInfinity(utility.Value) || utility.Value < 0 || utility.Value > 1))
            throw new ArgumentOutOfRangeException(nameof(utility), "Normalize task utility into [0,1] before cross-task comparisons.");
        if (valid && !utility.HasValue) throw new ArgumentException("A valid trial needs measured utility.", nameof(utility));
        if (valid && unknownConsumption) throw new ArgumentException("Unknown consumption cannot qualify a policy.", nameof(unknownConsumption));
        if (diagnostic is not null && (diagnostic.Length > 1024 || diagnostic.Any(char.IsControl)))
            throw new ArgumentException("Diagnostic text must be bounded and single-line.", nameof(diagnostic));
        if (evidenceJson is not null)
        {
            if (evidenceJson.Length > 128 * 1024 || new System.Text.UTF8Encoding(false, true).GetByteCount(evidenceJson) > 128 * 1024)
                throw new ArgumentException("Inline trial evidence exceeds 128 KiB.", nameof(evidenceJson));
            using var document = System.Text.Json.JsonDocument.Parse(evidenceJson, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            if (EvolutionHash.Compute(evidenceJson) != evidenceHash) throw new ArgumentException("Trial evidence digest differs.", nameof(evidenceHash));
        }
        Utility = utility; ActualResources = actualResources; EvidenceHash = evidenceHash;
        IsValid = valid; IsFresh = fresh; Diagnostic = diagnostic; EvidenceJson = evidenceJson; HasUnknownConsumption = unknownConsumption;
    }
    /// <summary>Gets normalized measured utility, or null when unavailable.</summary>
    public double? Utility { get; }
    /// <summary>Gets reported charges and explicit engine counters; unknown conservative components are identified separately.</summary>
    public EvolutionResources ActualResources { get; }
    /// <summary>Gets the digest of the underlying task/engine evidence.</summary>
    public string EvidenceHash { get; }
    /// <summary>Gets whether the task's fixed correctness and receipt checks passed.</summary>
    public bool IsValid { get; }
    /// <summary>Gets whether this trial was newly executed, not reused from policy selection.</summary>
    public bool IsFresh { get; }
    /// <summary>Gets bounded diagnostic context, not arbitrary executable instructions.</summary>
    public string? Diagnostic { get; }
    /// <summary>Gets optional bounded, hash-verified inline evidence; otherwise the protocol must retain the referenced artifact externally.</summary>
    public string? EvidenceJson { get; }
    /// <summary>Gets whether reported charges include unobserved work conservatively charged at reserved maxima.</summary>
    public bool HasUnknownConsumption { get; }
}

/// <summary>A versioned task protocol assigned to one semantic family before policy search begins.</summary>
/// <remarks>Use the engine-backed adapter for actual declarative execution. Custom runners must enforce the supplied limits and report all nested costs.</remarks>
public sealed class EvolutionPolicyTrial
{
    private readonly Func<EvolutionSearchPolicy, EvolutionPolicyTrialBudget, ulong, CancellationToken, Task<EvolutionPolicyObservation>> _run;

    /// <summary>Declares task identity, family, protocol version and a private trial factory.</summary>
    public EvolutionPolicyTrial(string id, string family, string versionHash, string costUnitVersionHash,
        Func<EvolutionSearchPolicy, EvolutionPolicyTrialBudget, ulong, CancellationToken, Task<EvolutionPolicyObservation>> run)
    {
        PolicyContract.Label(id); PolicyContract.Label(family); PolicyContract.Hash(versionHash); PolicyContract.Label(costUnitVersionHash);
        _run = run ?? throw new ArgumentNullException(nameof(run));
        Id = id; Family = family; VersionHash = versionHash; CostUnitVersionHash = costUnitVersionHash;
    }
    /// <summary>Gets the unique task identity.</summary>
    public string Id { get; }
    /// <summary>Gets the predeclared semantic family used as an independent validation unit.</summary>
    public string Family { get; }
    /// <summary>Gets the fixed task/data/normalization protocol fingerprint.</summary>
    public string VersionHash { get; }
    /// <summary>Gets the shared cost-unit contract required before aggregating tasks and baseline tuning receipts.</summary>
    public string CostUnitVersionHash { get; }
    internal Task<EvolutionPolicyObservation> RunAsync(EvolutionSearchPolicy policy, EvolutionPolicyTrialBudget budget, ulong seed, CancellationToken token) =>
        _run(policy, budget, seed, token);
}

/// <summary>A predeclared manually chosen baseline with explicit rationale, provenance and prior tuning charges.</summary>
/// <remarks>Metadata does not prove a baseline is strong. Researchers must retain the referenced tuning evidence and justify its task-specific competitiveness.</remarks>
public sealed class EvolutionPolicyBaseline
{
    /// <summary>Creates a baseline without hiding its earlier tuning cost from the campaign report.</summary>
    public EvolutionPolicyBaseline(string name, EvolutionSearchPolicy policy, string rationale,
        string tuningEvidenceHash, EvolutionResources priorTuningResources, string costUnitVersionHash)
    {
        PolicyContract.Label(name); PolicyContract.Hash(tuningEvidenceHash); PolicyContract.Label(costUnitVersionHash);
        if (string.IsNullOrWhiteSpace(rationale) || rationale.Length > 2048) throw new ArgumentException("A bounded baseline rationale is required.", nameof(rationale));
        Policy = policy ?? throw new ArgumentNullException(nameof(policy));
        PriorTuningResources = priorTuningResources ?? throw new ArgumentNullException(nameof(priorTuningResources));
        Name = name; Rationale = rationale; TuningEvidenceHash = tuningEvidenceHash; CostUnitVersionHash = costUnitVersionHash;
    }
    /// <summary>Gets the registered display name.</summary>
    public string Name { get; }
    /// <summary>Gets the frozen manually chosen recipe.</summary>
    public EvolutionSearchPolicy Policy { get; }
    /// <summary>Gets the rationale for including this baseline rather than a weak strawman.</summary>
    public string Rationale { get; }
    /// <summary>Gets the digest of externally retained tuning/design evidence.</summary>
    public string TuningEvidenceHash { get; }
    /// <summary>Gets earlier reported tuning charges, recorded separately from new inner trials.</summary>
    public EvolutionResources PriorTuningResources { get; }
    /// <summary>Gets the unit contract used by historical tuning charges.</summary>
    public string CostUnitVersionHash { get; }
}

internal static class PolicyResources
{
    internal const string Evaluations = "policy_inner_evaluations", Proposals = "policy_inner_proposals",
        Restarts = "policy_inner_restarts", OuterProposals = "policy_outer_proposals";
    internal static bool IsCounter(string name) => name is Evaluations or Proposals or Restarts or OuterProposals;
    internal static EvolutionResources WithCounters(EvolutionResources resources, long evaluations, long proposals, long restarts) =>
        new(resources.Amounts.Concat(new[]
        {
            new KeyValuePair<string, decimal>(Evaluations, evaluations),
            new KeyValuePair<string, decimal>(Proposals, proposals),
            new KeyValuePair<string, decimal>(Restarts, restarts)
        }));
}
