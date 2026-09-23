using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

/// <summary>One recorded escalation to a stronger configured tier, with the cost it added.</summary>
public sealed class EvolutionEscalation
{
    internal EvolutionEscalation(long generation, int fromTier, int toTier, string fromOperatorId, string toOperatorId,
        int consecutiveFailures)
    {
        Generation = generation; FromTier = fromTier; ToTier = toTier;
        FromOperatorId = fromOperatorId; ToOperatorId = toOperatorId; ConsecutiveFailures = consecutiveFailures;
    }

    /// <summary>Gets the generation whose outcome triggered the escalation.</summary>
    public long Generation { get; }
    /// <summary>Gets the tier escalated from.</summary>
    public int FromTier { get; }
    /// <summary>Gets the tier escalated to.</summary>
    public int ToTier { get; }
    /// <summary>Gets the operator identity escalated from.</summary>
    public string FromOperatorId { get; }
    /// <summary>Gets the operator identity escalated to.</summary>
    public string ToOperatorId { get; }
    /// <summary>Gets the consecutive failed or non-improving outcomes that met the criterion.</summary>
    public int ConsecutiveFailures { get; }
    /// <summary>
    /// Gets the first escalated proposal's actual charge minus the lower tier's mean charge, per resource; null until that
    /// proposal's outcome arrives, or when either tier does not report proposal receipts.
    /// </summary>
    public EvolutionResources? IncrementalCost { get; internal set; }
}

/// <summary>
/// Routes proposals up an ordered ladder of configured tiers (for example Haiku, Sonnet, Opus) when the current
/// tier keeps failing, and back to the cheapest tier after a success (US-19).
/// </summary>
/// <remarks>
/// A tier fails an outcome when the proposal failed, was invalid or infeasible, or did not improve the archive.
/// After <c>failuresBeforeEscalation</c> consecutive failures at the current tier, later proposals use the next
/// tier; a success at any tier returns to tier 0. Every escalation is recorded, and when tiers report proposal
/// receipts (<see cref="IEvolutionProposalCostProvider"/>), its incremental cost is measured from actual charges.
/// A fixed single-model baseline is simply one tier used directly, without this operator.
/// </remarks>
public sealed class EscalatingVariationOperator<TGenome> : IOutcomeAwareVariationOperator<TGenome>
{
    private const int MaximumEscalations = 1024;
    private const int MaximumPending = 65536;
    private readonly IVariationOperator<TGenome>[] _tiers;
    private readonly int _failuresBeforeEscalation;
    private readonly List<EvolutionEscalation> _escalations = new();
    private State _state;

    /// <summary>Creates a ladder from cheapest to strongest tier.</summary>
    /// <param name="tiers">At least two distinct operators, cheapest first.</param>
    /// <param name="failuresBeforeEscalation">Consecutive failed or non-improving outcomes that trigger escalation.</param>
    public EscalatingVariationOperator(IReadOnlyList<IVariationOperator<TGenome>> tiers, int failuresBeforeEscalation)
    {
        Guard.NotNull(tiers);
        if (tiers.Count < 2) throw new ArgumentException("An escalation ladder needs at least two tiers.", nameof(tiers));
        if (tiers.Any(tier => tier is null)) throw new ArgumentException("Tiers cannot be null.", nameof(tiers));
        if (tiers.Select(tier => tier.Id).Distinct(StringComparer.Ordinal).Count() != tiers.Count)
            throw new ArgumentException("Tier identities must be distinct.", nameof(tiers));
        if (failuresBeforeEscalation < 1) throw new ArgumentOutOfRangeException(nameof(failuresBeforeEscalation));
        _tiers = tiers.ToArray();
        _failuresBeforeEscalation = failuresBeforeEscalation;
        _state = new State { Proposals = new long[_tiers.Length], Outcomes = new long[_tiers.Length], Successes = new long[_tiers.Length],
            Charged = new Dictionary<string, decimal>[_tiers.Length], Receipts = new long[_tiers.Length] };
        for (int i = 0; i < _tiers.Length; i++) _state.Charged[i] = new Dictionary<string, decimal>(StringComparer.Ordinal);
        Id = "escalating(" + string.Join(",", _tiers.Select(tier => tier.Id)) + ")";
        VersionHash = EvolutionHash.Combine(new[] { "escalating-variation-v1",
            failuresBeforeEscalation.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(_tiers.SelectMany(tier => new[] { tier.Id, tier.VersionHash })));
    }

    /// <inheritdoc/>
    public string Id { get; }
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <summary>Gets the tier the next proposal will use.</summary>
    public int CurrentTier => _state.Tier;
    /// <summary>Gets every recorded escalation, oldest first (at most 1024 are retained).</summary>
    public IReadOnlyList<EvolutionEscalation> Escalations => _escalations.AsReadOnly();
    /// <summary>Gets proposals assigned to each tier, cheapest first.</summary>
    public IReadOnlyList<long> TierProposals => Array.AsReadOnly(_state.Proposals);
    /// <summary>Gets successful (archive-improving, valid) outcomes per tier.</summary>
    public IReadOnlyList<long> TierSuccesses => Array.AsReadOnly(_state.Successes);

    /// <inheritdoc/>
    public ValueTask<TGenome> ProposeAsync(EvolutionVariationContext<TGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Generation <= 0 || _state.Pending.ContainsKey(context.Generation))
            throw new ArgumentException("A proposal requires a unique positive generation.", nameof(context));
        if (_state.Pending.Count >= MaximumPending) throw new InvalidOperationException("The escalation pending-outcome limit was reached.");
        int tier = _state.Tier;
        _state.Pending.Add(context.Generation, tier);
        _state.Proposals[tier] = checked(_state.Proposals[tier] + 1);
        return _tiers[tier].ProposeAsync(context, cancellationToken);
    }

    /// <inheritdoc/>
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        Guard.NotNull(evaluation);
        long generation = evaluation.Lineage.Generation;
        if (!_state.Pending.TryGetValue(generation, out int tier))
            throw new InvalidOperationException("The escalation operator received an unknown or repeated outcome.");
        // Read the receipt before the tier consumes it on its own terminal outcome.
        EvolutionResources? charged = _tiers[tier] is IEvolutionProposalCostProvider provider
            ? provider.GetProposalCost(generation).Charged : null;
        if (_tiers[tier] is IOutcomeAwareVariationOperator<TGenome> aware) aware.Observe(evaluation, insertionResult);
        _state.Pending.Remove(generation);
        _state.Outcomes[tier] = checked(_state.Outcomes[tier] + 1);
        bool success = evaluation.Status == EvolutionEvaluationStatus.Completed && evaluation.Quality.HasValue &&
            evaluation.ConstraintViolations.All(value => value <= 0) &&
            insertionResult is EvolutionArchiveInsertionResult.Inserted or EvolutionArchiveInsertionResult.Replaced
                or EvolutionArchiveInsertionResult.InsertedWithEviction;
        if (success) _state.Successes[tier] = checked(_state.Successes[tier] + 1);
        if (charged is not null)
        {
            EvolutionEscalation? awaiting = _state.AwaitingCost is int index && index < _escalations.Count ? _escalations[index] : null;
            if (awaiting is not null && awaiting.ToTier == tier && _state.Receipts[awaiting.FromTier] > 0)
            {
                var incremental = charged.Amounts.Keys.Union(_state.Charged[awaiting.FromTier].Keys, StringComparer.Ordinal)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .Select(key => new KeyValuePair<string, decimal>(key, charged[key] -
                        (_state.Charged[awaiting.FromTier].TryGetValue(key, out decimal sum) ? sum / _state.Receipts[awaiting.FromTier] : 0)));
                awaiting.IncrementalCost = new EvolutionResources(incremental.Where(pair => pair.Value >= 0));
                _state.AwaitingCost = null;
            }
            foreach (KeyValuePair<string, decimal> amount in charged.Amounts)
                _state.Charged[tier][amount.Key] = (_state.Charged[tier].TryGetValue(amount.Key, out decimal sum) ? sum : 0) + amount.Value;
            _state.Receipts[tier] = checked(_state.Receipts[tier] + 1);
        }
        if (tier != _state.Tier) return; // a late outcome from an older tier does not move the ladder
        if (success)
        {
            _state.Tier = 0;
            _state.ConsecutiveFailures = 0;
            return;
        }
        _state.ConsecutiveFailures++;
        if (_state.ConsecutiveFailures < _failuresBeforeEscalation || _state.Tier == _tiers.Length - 1) return;
        if (_escalations.Count < MaximumEscalations)
        {
            _escalations.Add(new EvolutionEscalation(generation, _state.Tier, _state.Tier + 1, _tiers[_state.Tier].Id,
                _tiers[_state.Tier + 1].Id, _state.ConsecutiveFailures));
            _state.AwaitingCost = _escalations.Count - 1;
        }
        _state.Tier++;
        _state.ConsecutiveFailures = 0;
    }

    /// <inheritdoc/>
    public string CaptureState()
    {
        _state.Children = _tiers.Select(tier => (tier as ICheckpointableVariationOperator<TGenome>)?.CaptureState()).ToArray();
        _state.Log = _escalations.Select(e => new LoggedEscalation
        {
            Generation = e.Generation, FromTier = e.FromTier, ToTier = e.ToTier, ConsecutiveFailures = e.ConsecutiveFailures,
            IncrementalCost = e.IncrementalCost is null ? null : new Dictionary<string, decimal>(e.IncrementalCost.Amounts.ToDictionary(p => p.Key, p => p.Value), StringComparer.Ordinal)
        }).ToArray();
        _state.VersionHash = VersionHash;
        string json = JsonSerializer.Serialize(_state, EvolutionJson.Compact);
        _state.Children = null; _state.Log = null;
        return json;
    }

    /// <inheritdoc/>
    public void RestoreState(string state)
    {
        Guard.NotNull(state);
        State? restored;
        try { restored = JsonSerializer.Deserialize<State>(state, EvolutionJson.Compact); }
        catch (JsonException exception) { throw new InvalidDataException("The escalation state is invalid.", exception); }
        int n = _tiers.Length;
        if (restored is null || restored.VersionHash != VersionHash || restored.Tier < 0 || restored.Tier >= n ||
            restored.ConsecutiveFailures < 0 || restored.ConsecutiveFailures >= _failuresBeforeEscalation ||
            restored.Proposals?.Length != n || restored.Outcomes?.Length != n || restored.Successes?.Length != n ||
            restored.Charged?.Length != n || restored.Receipts?.Length != n || restored.Children?.Length != n ||
            restored.Pending is null || restored.Log is null || restored.Log.Length > MaximumEscalations ||
            restored.Pending.Values.Any(tier => tier < 0 || tier >= n) || restored.Pending.Keys.Any(key => key <= 0) ||
            Enumerable.Range(0, n).Any(i => restored.Outcomes[i] < 0 || restored.Successes[i] > restored.Outcomes[i] ||
                restored.Proposals[i] - restored.Outcomes[i] != restored.Pending.Values.Count(tier => tier == i) ||
                restored.Receipts[i] < 0 || restored.Receipts[i] > restored.Outcomes[i] || restored.Charged[i] is null ||
                (_tiers[i] is ICheckpointableVariationOperator<TGenome>) != (restored.Children[i] is not null)) ||
            restored.Log.Any(e => e is null || e.FromTier < 0 || e.ToTier != e.FromTier + 1 || e.ToTier >= n) ||
            (restored.AwaitingCost is int awaiting && (awaiting < 0 || awaiting >= restored.Log.Length)))
            throw new InvalidDataException("The escalation state is incompatible or incomplete.");
        for (int i = 0; i < n; i++)
            if (_tiers[i] is ICheckpointableVariationOperator<TGenome> child) child.RestoreState(restored.Children[i]!);
        _escalations.Clear();
        foreach (LoggedEscalation e in restored.Log)
            _escalations.Add(new EvolutionEscalation(e.Generation, e.FromTier, e.ToTier, _tiers[e.FromTier].Id, _tiers[e.ToTier].Id,
                e.ConsecutiveFailures) { IncrementalCost = e.IncrementalCost is null ? null : new EvolutionResources(e.IncrementalCost) });
        restored.Children = null; restored.Log = null;
        _state = restored;
    }

    private sealed class State
    {
        public string? VersionHash { get; set; }
        public int Tier { get; set; }
        public int ConsecutiveFailures { get; set; }
        public SortedDictionary<long, int> Pending { get; set; } = new();
        public long[] Proposals { get; set; } = Array.Empty<long>();
        public long[] Outcomes { get; set; } = Array.Empty<long>();
        public long[] Successes { get; set; } = Array.Empty<long>();
        public long[] Receipts { get; set; } = Array.Empty<long>();
        public Dictionary<string, decimal>[] Charged { get; set; } = Array.Empty<Dictionary<string, decimal>>();
        public int? AwaitingCost { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string?[]? Children { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LoggedEscalation[]? Log { get; set; }
    }

    private sealed class LoggedEscalation
    {
        public long Generation { get; set; }
        public int FromTier { get; set; }
        public int ToTier { get; set; }
        public int ConsecutiveFailures { get; set; }
        public Dictionary<string, decimal>? IncrementalCost { get; set; }
    }
}