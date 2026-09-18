using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Predeclared fresh incumbent challenges with separately owned confirmation callbacks.</summary>
/// <remarks>Confirmation error is allocated across two candidates and all declared challenge slots.
/// Neither search nor confirmation evidence is inserted into an archive. Trusted callers own correctness,
/// actual sample independence, hidden-data custody and eventual incumbent replacement.</remarks>
public sealed class EvolutionIncumbentChallenge<TGenome>
{
    private readonly EvolutionReplicateRunner<TGenome> _search, _confirm;
    private readonly EvolutionResourceLedger _ledger;
    private readonly double _minimumImprovement;
    private readonly EvolutionOptimizationDirection _direction;
    private readonly int _maximumChallenges;

    /// <summary>Creates a fixed-count policy. Bounds and minimum improvement use the same declared quality units.</summary>
    public EvolutionIncumbentChallenge(string searchVersion, string confirmationVersion, EvolutionResourceLedger ledger,
        int searchSamples, int confirmationSamples, int maximumChallenges, double minimumQuality, double maximumQuality,
        double minimumImprovement, decimal maximumCostPerSample,
        Func<TGenome, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> search,
        Func<TGenome, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> confirm,
        double confidence = .95, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(ledger); Guard.NotNull(search); Guard.NotNull(confirm);
        if (maximumChallenges is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(maximumChallenges));
        if (!EvolutionDescriptorDefinition.IsFinite(confidence) || confidence < .8 || confidence > .999999)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        if (!EvolutionDescriptorDefinition.IsFinite(minimumImprovement) || minimumImprovement < 0 || minimumImprovement >= maximumQuality - minimumQuality)
            throw new ArgumentOutOfRangeException(nameof(minimumImprovement));
        var searchPlan = new EvolutionReplicationPlan(searchSamples, searchSamples, minimumQuality, maximumQuality,
            maximumCostPerSample, direction: direction);
        var confirmationPlan = new EvolutionReplicationPlan(confirmationSamples, confirmationSamples, minimumQuality, maximumQuality,
            maximumCostPerSample, confidence: 1 - (1 - confidence) / (2d * maximumChallenges), direction: direction);
        _search = new(searchVersion, searchPlan, ledger, search);
        _confirm = new(confirmationVersion, confirmationPlan, ledger, confirm);
        _ledger = ledger; _minimumImprovement = minimumImprovement; _direction = direction; _maximumChallenges = maximumChallenges;
        VersionHash = EvolutionHash.Combine(new[] { "incumbent-challenge-v1", _search.VersionHash, _confirm.VersionHash,
            maximumChallenges.ToString(CultureInfo.InvariantCulture), EvolutionHash.EncodeDouble(minimumImprovement) });
    }

    /// <summary>Gets the complete callback, sampling and finite-challenge policy identity.</summary>
    public string VersionHash { get; }

    /// <summary>Consumes one unique challenge slot before any evaluator call; repeats are refused by the persisted ledger.</summary>
    public async ValueTask<EvolutionIncumbentChallengeReport> RunAsync(int slot, EvolutionCanonicalGenome<TGenome> candidate,
        EvolutionCanonicalGenome<TGenome> incumbent, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(candidate); Guard.NotNull(incumbent); Guard.NotNull(context);
        if (slot < 0 || slot >= _maximumChallenges) throw new ArgumentOutOfRangeException(nameof(slot));
        if (candidate.Id == incumbent.Id) throw new ArgumentException("A challenge requires distinct canonical identities.", nameof(candidate));
        cancellationToken.ThrowIfCancellationRequested();
        string identity = EvolutionHash.Combine(new[] { VersionHash, slot.ToString(CultureInfo.InvariantCulture) });
        EvolutionIncumbentChallengeReport Report(EvolutionReplicationReport first, EvolutionReplicationReport? second,
            EvolutionReplicationReport? third, EvolutionReplicationReport? fourth, double? lower, string outcome) =>
            new(identity, candidate.Id, incumbent.Id, first, second, third, fourth, lower, outcome);
        // A zero-cost durable tombstone accounts for the finite inference slot,
        // not a physical evaluator call. Changing candidate/context cannot reuse it.
        using (var claim = _ledger.TryReserve("challenge/" + identity, EvolutionResourceStage.Setup,
            EvolutionResources.Empty, EvolutionResources.Empty) ?? throw new EvolutionResourceBudgetException(identity))
            claim.Complete(EvolutionResources.Empty);
        var candidateSearch = await _search.RunAsync(candidate, context, identity, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return Report(candidateSearch, null, null, null, null, "canceled");
        if (!candidateSearch.IsComplete) return Report(candidateSearch, null, null, null, null, "candidate-search-incomplete");
        var incumbentSearch = await _search.RunAsync(incumbent, context, identity, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return Report(candidateSearch, incumbentSearch, null, null, null, "canceled");
        if (!incumbentSearch.IsComplete) return Report(candidateSearch, incumbentSearch, null, null, null, "incumbent-search-incomplete");
        double searchGain = _direction == EvolutionOptimizationDirection.Maximize
            ? candidateSearch.MeanQuality!.Value - incumbentSearch.MeanQuality!.Value
            : incumbentSearch.MeanQuality!.Value - candidateSearch.MeanQuality!.Value;
        if (searchGain <= _minimumImprovement) return Report(candidateSearch, incumbentSearch, null, null, null, "not-promising");
        var candidateConfirmation = await _confirm.RunAsync(candidate, context, identity, EvolutionReplicationPurpose.Confirmation, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return Report(candidateSearch, incumbentSearch, candidateConfirmation, null, null, "canceled");
        if (!candidateConfirmation.IsComplete) return Report(candidateSearch, incumbentSearch, candidateConfirmation, null, null, "candidate-confirmation-incomplete");
        var incumbentConfirmation = await _confirm.RunAsync(incumbent, context, identity, EvolutionReplicationPurpose.Confirmation, cancellationToken).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) return Report(candidateSearch, incumbentSearch, candidateConfirmation, incumbentConfirmation, null, "canceled");
        if (!incumbentConfirmation.IsComplete) return Report(candidateSearch, incumbentSearch, candidateConfirmation, incumbentConfirmation, null, "incumbent-confirmation-incomplete");
        double lowerGain = _direction == EvolutionOptimizationDirection.Maximize
            ? candidateConfirmation.LowerBound!.Value - incumbentConfirmation.UpperBound!.Value
            : incumbentConfirmation.LowerBound!.Value - candidateConfirmation.UpperBound!.Value;
        return Report(candidateSearch, incumbentSearch, candidateConfirmation, incumbentConfirmation, lowerGain,
            lowerGain > _minimumImprovement ? "confirmed-improvement" : "not-confirmed");
    }
}

/// <summary>Fresh challenger/incumbent evidence, with confirmation isolated from search reports.</summary>
public sealed class EvolutionIncumbentChallengeReport
{
    internal EvolutionIncumbentChallengeReport(string identity, string candidateId, string incumbentId, EvolutionReplicationReport candidateSearch,
        EvolutionReplicationReport? incumbentSearch, EvolutionReplicationReport? candidateConfirmation,
        EvolutionReplicationReport? incumbentConfirmation, double? lowerGain, string outcome)
    {
        Identity = identity; CandidateId = candidateId; IncumbentId = incumbentId; CandidateSearch = candidateSearch; IncumbentSearch = incumbentSearch;
        CandidateConfirmation = candidateConfirmation; IncumbentConfirmation = incumbentConfirmation;
        LowerImprovementBound = lowerGain; Outcome = outcome;
    }
    /// <summary>Gets the finite-slot policy identity.</summary>
    public string Identity { get; }
    /// <summary>Gets the challenged canonical candidate identity.</summary>
    public string CandidateId { get; }
    /// <summary>Gets the incumbent identity that must still match before any external replacement.</summary>
    public string IncumbentId { get; }
    /// <summary>Gets fresh challenger search measurements.</summary>
    public EvolutionReplicationReport CandidateSearch { get; }
    /// <summary>Gets fresh incumbent search measurements, or null if not dispatched.</summary>
    public EvolutionReplicationReport? IncumbentSearch { get; }
    /// <summary>Gets independently requested challenger confirmation, not proposer feedback.</summary>
    public EvolutionReplicationReport? CandidateConfirmation { get; }
    /// <summary>Gets independently requested incumbent confirmation, or null if not dispatched.</summary>
    public EvolutionReplicationReport? IncumbentConfirmation { get; }
    /// <summary>Gets the conservative lower improvement bound in declared quality units.</summary>
    public double? LowerImprovementBound { get; }
    /// <summary>Gets an explicit incomplete/nonpromising/nonconfirmed/confirmed outcome.</summary>
    public string Outcome { get; }
    /// <summary>Gets statistical confirmation only; correctness/applicability and archive replacement remain external gates.</summary>
    public bool IsConfirmed => Outcome == "confirmed-improvement";
    /// <summary>Gets all reported actual and conservative unknown sample charges.</summary>
    public decimal ChargedCostUnits => CandidateSearch.ChargedCostUnits + (IncumbentSearch?.ChargedCostUnits ?? 0)
        + (CandidateConfirmation?.ChargedCostUnits ?? 0) + (IncumbentConfirmation?.ChargedCostUnits ?? 0);
}
