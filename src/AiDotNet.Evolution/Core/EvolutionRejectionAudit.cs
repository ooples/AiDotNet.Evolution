using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Preselected fresh full-evaluation audit of a frozen set of screen-rejected candidates.</summary>
/// <remarks>Freeze the rejection set and an independent seed before observing audit results. Sampling is
/// deterministic from that seed; keeping it unpredictable to candidate generation is a caller custody obligation.
/// This audits a single population, never trains the screen or inserts rejected candidates into an archive.</remarks>
public sealed class EvolutionRejectionAudit<TGenome>
{
    private readonly EvolutionReplicateRunner<TGenome> _runner;
    private readonly EvolutionResourceLedger _ledger;
    private readonly int _auditCount;
    private readonly double _threshold, _confidence;
    private readonly EvolutionOptimizationDirection _direction;

    /// <summary>Creates a fixed audit preset; usefulness means true full-fidelity mean strictly beats the threshold.</summary>
    public EvolutionRejectionAudit(string evaluatorVersion, EvolutionResourceLedger ledger, int auditCandidates,
        int samplesPerCandidate, double minimumQuality, double maximumQuality, double usefulThreshold,
        decimal maximumCostPerSample, Func<TGenome, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> evaluateFull,
        double confidence = .95, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        if (auditCandidates is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(auditCandidates));
        if (!EvolutionDescriptorDefinition.IsFinite(confidence) || confidence < .8 || confidence > .9999)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        if (!EvolutionDescriptorDefinition.IsFinite(usefulThreshold) || usefulThreshold < minimumQuality || usefulThreshold > maximumQuality)
            throw new ArgumentOutOfRangeException(nameof(usefulThreshold));
        var plan = new EvolutionReplicationPlan(samplesPerCandidate, samplesPerCandidate, minimumQuality, maximumQuality,
            maximumCostPerSample, confidence: 1 - (1 - confidence) / (2 * auditCandidates), direction: direction);
        _runner = new(evaluatorVersion, plan, ledger, evaluateFull);
        _ledger = ledger; _auditCount = auditCandidates; _threshold = usefulThreshold; _confidence = confidence; _direction = direction;
        VersionHash = EvolutionHash.Combine(new[] { "screen-rejection-audit-v1", _runner.VersionHash,
            auditCandidates.ToString(CultureInfo.InvariantCulture), EvolutionHash.EncodeDouble(usefulThreshold) });
    }
    /// <summary>Gets the complete audit/measurement policy identity.</summary>
    public string VersionHash { get; }

    /// <summary>Freezes selection before calling the full evaluator; charges all audits independently of engine refund options.</summary>
    public async ValueTask<EvolutionRejectionAuditReport> RunAsync(string auditId, IReadOnlyList<EvolutionCanonicalGenome<TGenome>> rejected,
        ulong auditSeed, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(auditId); Guard.NotNull(rejected);
        if (auditId.Length > 128 || auditId.Any(char.IsControl)) throw new ArgumentException("Invalid audit identity.", nameof(auditId));
        var population = EvolutionCollection.CopyBounded(rejected, 4096, nameof(rejected));
        if (population.Length < 1 || population.Any(candidate => candidate is null) ||
            population.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != population.Length)
            throw new ArgumentException("Require 1..4096 distinct rejected candidates.", nameof(rejected));
        cancellationToken.ThrowIfCancellationRequested();
        population = population.OrderBy(candidate => candidate.Id, StringComparer.Ordinal).ToArray();
        // Canonical population order makes selection invariant to caller enumeration.
        var shuffled = population.ToArray();
        var random = new EvolutionEvaluationContext(0, auditSeed, auditSeed, 1).CreateRandom();
        for (int index = 0; index < Math.Min(_auditCount, shuffled.Length); index++)
        {
            int other = index + random.NextInt(shuffled.Length - index);
            (shuffled[index], shuffled[other]) = (shuffled[other], shuffled[index]);
        }
        var selected = shuffled.Take(_auditCount).ToArray();
        string identity = EvolutionHash.Combine(new[] { VersionHash, auditId });
        using (var claim = _ledger.TryReserve("audit/" + identity, EvolutionResourceStage.Setup,
            EvolutionResources.Empty, EvolutionResources.Empty) ?? throw new EvolutionResourceBudgetException(identity))
            claim.Complete(EvolutionResources.Empty);
        var rows = new List<EvolutionRejectionAuditEntry>();
        for (int index = 0; index < selected.Length; index++)
        {
            EvolutionReplicationReport? report = null;
            if (!cancellationToken.IsCancellationRequested)
                report = await _runner.RunAsync(selected[index], new EvolutionEvaluationContext(index, auditSeed, 0, 1),
                    identity, EvolutionReplicationPurpose.Confirmation, cancellationToken).ConfigureAwait(false);
            bool useful = report?.IsComplete == true && (_direction == EvolutionOptimizationDirection.Maximize
                ? report.LowerBound > _threshold : report.UpperBound < _threshold);
            bool notUseful = report?.IsComplete == true && (_direction == EvolutionOptimizationDirection.Maximize
                ? report.UpperBound <= _threshold : report.LowerBound >= _threshold);
            rows.Add(new(selected[index].Id, report, useful, !useful && !notUseful));
        }
        return new(identity, EvolutionHash.Combine(population.Select(candidate => candidate.Id)), auditSeed, population.Length, rows, _confidence);
    }
}

/// <summary>One selected reject; unresolved, incomplete and undispatched audits are never counted as harmless rejections.</summary>
public sealed class EvolutionRejectionAuditEntry
{
    internal EvolutionRejectionAuditEntry(string candidateId, EvolutionReplicationReport? fullEvaluation, bool useful, bool unresolved)
    { CandidateId = candidateId; FullEvaluation = fullEvaluation; DefinitelyUseful = useful; Unresolved = unresolved; }
    /// <summary>Gets the selected canonical identity.</summary>
    public string CandidateId { get; }
    /// <summary>Gets fresh full-evaluation receipts, or null if cancellation prevented dispatch.</summary>
    public EvolutionReplicationReport? FullEvaluation { get; }
    /// <summary>Gets whether the full-mean confidence bound establishes usefulness at the declared threshold.</summary>
    public bool DefinitelyUseful { get; }
    /// <summary>Gets whether incomplete work or uncertainty prevents classification.</summary>
    public bool Unresolved { get; }
}

/// <summary>False-rejection evidence conditional on a frozen rejected population and predeclared audit selection.</summary>
public sealed class EvolutionRejectionAuditReport
{
    internal EvolutionRejectionAuditReport(string identity, string populationHash, ulong seed, int population,
        IEnumerable<EvolutionRejectionAuditEntry> entries, double confidence)
    {
        Identity = identity; PopulationHash = populationHash; AuditSeed = seed; RejectedCandidates = population;
        Entries = Array.AsReadOnly(entries.ToArray()); Confidence = confidence;
        double lower = (double)Entries.Count(row => row.DefinitelyUseful) / Entries.Count;
        double upper = (double)Entries.Count(row => row.DefinitelyUseful || row.Unresolved) / Entries.Count;
        // Half alpha was allocated across candidate full-mean intervals; the
        // remaining half bounds sampling of bounded useful/not-useful labels.
        double radius = Entries.Count == population ? 0 : Math.Sqrt(Math.Log(4 / (1 - confidence)) / (2 * Entries.Count));
        FalseRejectionRateLower = Math.Max(0, lower - radius);
        FalseRejectionRateUpper = Math.Min(1, upper + radius);
    }
    /// <summary>Gets the one-use audit identity.</summary>
    public string Identity { get; }
    /// <summary>Gets the frozen rejected-set hash.</summary>
    public string PopulationHash { get; }
    /// <summary>Gets the predeclared pseudorandom audit seed.</summary>
    public ulong AuditSeed { get; }
    /// <summary>Gets every screen-rejected candidate in the frozen population.</summary>
    public int RejectedCandidates { get; }
    /// <summary>Gets every selected audit, including incomplete/unstarted ones.</summary>
    public IReadOnlyList<EvolutionRejectionAuditEntry> Entries { get; }
    /// <summary>Gets the nominal confidence under the stated sampling and fresh-measurement assumptions.</summary>
    public double Confidence { get; }
    /// <summary>Gets a conservative lower bound on useful candidates among this rejected population.</summary>
    public double FalseRejectionRateLower { get; }
    /// <summary>Gets the upper bound, treating every unresolved audit as potentially useful.</summary>
    public double FalseRejectionRateUpper { get; }
    /// <summary>Gets known and conservative unknown dispatched full-evaluation charges.</summary>
    public decimal ChargedCostUnits => Entries.Sum(row => row.FullEvaluation?.ChargedCostUnits ?? 0);
}
