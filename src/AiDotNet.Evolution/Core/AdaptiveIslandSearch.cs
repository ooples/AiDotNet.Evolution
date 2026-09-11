namespace AiDotNet.Evolution;

/// <summary>Allocates a fixed heterogeneous island pool by recent measured progress and schedules bounded exploration.</summary>
/// <typeparam name="TGenome">The owned task genome.</typeparam>
/// <remarks>
/// Opt-in, deterministic and serialized by the engine. Each complete allocation epoch guarantees every island its
/// declared proposal floor; remaining slots use recent parent-relative gain and new-cell diversity. Failed, infeasible
/// and reused outcomes earn zero. Proposal slots are not a substitute for an evaluator/model resource ledger.
/// Restarts temporarily route proposals to an independent exploration operator: archives and ordinary child learning
/// are preserved, not reset. In particular this is not a covariance reset while earlier child outcomes are pending.
/// Use fresh, independently owned children per run. Stateful children must implement the checkpoint contract.
/// Membership is fixed; changing order, identities, child versions or policy settings rejects old checkpoints.
/// </remarks>
public sealed partial class AdaptiveIslandSearch<TGenome> : IOutcomeAwareVariationOperator<TGenome>, IEvolutionIslandProposalScheduler
{
    private const int MaximumIslands = 64;
    private const int MaximumPending = 65_536;
    private const int MaximumDecisions = 256;
    private const int MaximumStateCharacters = 16 * 1024 * 1024;
    private readonly EvolutionIslandStrategy<TGenome>[] _members;
    private readonly string[] _identities;
    private readonly EvolutionIslandPolicyOptions _options;
    private readonly int _epochLength;
    private IslandState[] _islands;
    private SortedDictionary<long, Attribution> _pending = new();
    private List<DecisionState> _decisions = new();
    private long _epoch;
    private long _lastGeneration;

    /// <summary>Creates one to 64 fixed, uniquely named islands with independently owned child instances.</summary>
    public AdaptiveIslandSearch(IEnumerable<EvolutionIslandStrategy<TGenome>> islands, EvolutionIslandPolicyOptions? options = null)
    {
        Guard.NotNull(islands);
        _members = islands.Take(MaximumIslands + 1).ToArray();
        if (_members.Length == 0 || _members.Length > MaximumIslands || _members.Any(member => member is null) ||
            _members.Select(member => member.Id).Distinct(StringComparer.Ordinal).Count() != _members.Length)
            throw new ArgumentException("The policy requires 1 to 64 uniquely named islands.", nameof(islands));
        IVariationOperator<TGenome>[] children = _members.SelectMany(member => new[] { member.Variation, member.Restart }).ToArray();
        for (int i = 0; i < children.Length; i++)
            for (int j = 0; j < i; j++)
                if (ReferenceEquals(children[i], children[j]))
                    throw new ArgumentException("Every island child must be independently owned.", nameof(islands));
        _options = options ?? new EvolutionIslandPolicyOptions();
        _epochLength = _members.Length * _options.ProposalsPerIslandPerEpoch;
        _identities = _members.Select(Identity).ToArray();
        _islands = _members.Select(_ => new IslandState()).ToArray();
        VersionHash = EvolutionHash.Combine(new[] { "adaptive-islands-v1", _options.VersionHash }.Concat(_identities));
    }

    /// <inheritdoc/>
    public string Id => "adaptive-islands";
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public int IslandCount => _members.Length;
    /// <summary>Gets the immutable allocation and restart settings.</summary>
    public EvolutionIslandPolicyOptions Options => _options;
    /// <summary>Gets detached per-island accounting in fixed membership order.</summary>
    public IReadOnlyList<EvolutionIslandStatistics> Statistics => Array.AsReadOnly(_islands.Select((state, i) =>
        new EvolutionIslandStatistics(_members[i].Id, state.Proposals, state.Outcomes, state.Fresh,
            state.RestartProposals, state.RestartOutcomes, state.Phases, state.Remaining, state.EpochProposals,
            Mean(state, false), Mean(state, true))).ToArray());
    /// <summary>Gets the last 256 allocation decisions in generation order; this history survives checkpoints.</summary>
    public IReadOnlyList<EvolutionIslandDecision> RecentDecisions => Array.AsReadOnly(_decisions.Select(decision =>
        new EvolutionIslandDecision(decision.Generation, decision.Island, _members[decision.Island].Id,
            Child(decision.Island, decision.Restart).Id, decision.Restart)).ToArray());

    /// <inheritdoc/>
    public int SelectIsland(long evaluationId, StableRandom random)
    {
        Guard.NotNull(random);
        if (evaluationId < 0) throw new ArgumentOutOfRangeException(nameof(evaluationId));
        EnsureIdentities();
        bool newEpoch = _islands.Sum(state => state.EpochProposals) == _epochLength;
        int least = Enumerable.Range(0, IslandCount).OrderBy(i => newEpoch ? 0 : _islands[i].EpochProposals).First();
        if (newEpoch || _islands[least].EpochProposals < _options.MinimumPerIslandPerEpoch) return least;
        if (!_options.AdaptiveAllocation) return random.NextInt(IslandCount);
        double[] weights = _islands.Select(state => 0.01 + (1 - _options.DiversityWeight) * Mean(state, false) +
            _options.DiversityWeight * Mean(state, true)).ToArray();
        double target = random.NextDouble() * weights.Sum();
        for (int i = 0; i < weights.Length - 1; i++)
        {
            target -= weights[i];
            if (target < 0) return i;
        }
        return weights.Length - 1;
    }

    /// <inheritdoc/>
    public ValueTask<TGenome> ProposeAsync(EvolutionVariationContext<TGenome> context, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureIdentities();
        if (context.Generation <= _lastGeneration || context.Island >= IslandCount)
            throw new ArgumentException("A proposal requires an increasing positive generation and a configured island.", nameof(context));
        if (_pending.Count >= MaximumPending) throw new InvalidOperationException("The island pending-outcome limit was reached.");
        if (_islands.Sum(state => state.EpochProposals) == _epochLength)
        {
            _epoch = checked(_epoch + 1);
            foreach (IslandState state in _islands) state.EpochProposals = 0;
        }
        IslandState island = _islands[context.Island];
        if (island.EpochProposals >= _options.MinimumPerIslandPerEpoch &&
            _islands.Any(member => member.EpochProposals < _options.MinimumPerIslandPerEpoch))
            throw new ArgumentException("The destination would violate the current epoch's exploration floor.", nameof(context));
        bool restart = island.Remaining > 0;
        EvolutionEvaluation parent = context.Parent.Evaluation;
        _pending.Add(context.Generation, new Attribution
        {
            Island = context.Island,
            Restart = restart,
            ParentQuality = parent.Status == EvolutionEvaluationStatus.Completed && parent.Direction == _options.Direction &&
                !parent.ConstraintViolations.Any(value => value > 0) ? parent.Quality : null
        });
        island.Proposals = checked(island.Proposals + 1);
        island.EpochProposals++;
        if (restart) { island.Remaining--; island.RestartProposals = checked(island.RestartProposals + 1); }
        _lastGeneration = context.Generation;
        _decisions.Add(new DecisionState { Generation = context.Generation, Island = context.Island, Restart = restart });
        if (_decisions.Count > MaximumDecisions) _decisions.RemoveAt(0);
        // Attribution must survive child failure: the engine still commits one terminal failed proposal.
        return Child(context.Island, restart).ProposeAsync(context, cancellationToken);
    }

    /// <inheritdoc/>
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        Guard.NotNull(evaluation);
        EnsureIdentities();
        if (!_pending.TryGetValue(evaluation.Lineage.Generation, out Attribution? pending) || pending.Island != evaluation.Lineage.Island)
            throw new InvalidOperationException("The island policy received an unknown, repeated or misattributed outcome.");
        bool fresh = evaluation.Status == EvolutionEvaluationStatus.Completed && !evaluation.IsMeasurementReuse &&
            evaluation.Direction == _options.Direction && !evaluation.ConstraintViolations.Any(value => value > 0);
        double gain = 0;
        if (fresh && pending.ParentQuality.HasValue)
        {
            double difference = _options.Direction == EvolutionOptimizationDirection.Maximize
                ? evaluation.Quality!.Value - pending.ParentQuality.Value : pending.ParentQuality.Value - evaluation.Quality!.Value;
            gain = Math.Max(0, Math.Min(1, difference / _options.GainScale));
        }
        double diversity = fresh && insertionResult is EvolutionArchiveInsertionResult.Inserted or EvolutionArchiveInsertionResult.InsertedWithEviction ? 1 : 0;
        if (Child(pending.Island, pending.Restart) is IOutcomeAwareVariationOperator<TGenome> child) child.Observe(evaluation, insertionResult);
        _pending.Remove(evaluation.Lineage.Generation);
        IslandState island = _islands[pending.Island];
        island.Outcomes = checked(island.Outcomes + 1);
        if (fresh) island.Fresh = checked(island.Fresh + 1);
        if (pending.Restart) island.RestartOutcomes = checked(island.RestartOutcomes + 1);
        island.Recent.Add(new Reward { Gain = gain, Diversity = diversity });
        if (island.Recent.Count > _options.RewardWindow) island.Recent.RemoveAt(0);
        island.Stagnation = gain > 0 || diversity > 0 ? 0 : Math.Min(_options.StagnationOutcomes, island.Stagnation + 1);
        if (_options.EnableRestarts && island.Stagnation >= _options.StagnationOutcomes && island.Remaining == 0 &&
            island.RestartProposals == island.RestartOutcomes)
        {
            island.Remaining = _options.RestartProposals;
            island.Phases = checked(island.Phases + 1);
            island.Stagnation = 0;
        }
    }

    private IVariationOperator<TGenome> Child(int island, bool restart) => restart ? _members[island].Restart : _members[island].Variation;
    private static double Mean(IslandState state, bool diversity) => state.Recent.Count == 0 ? 0 :
        state.Recent.Average(reward => diversity ? reward.Diversity : reward.Gain);
    private static string Identity(EvolutionIslandStrategy<TGenome> member) => EvolutionHash.Combine(new[]
        { member.Id, member.Variation.Id, member.Variation.VersionHash, member.Restart.Id, member.Restart.VersionHash });
    private void EnsureIdentities()
    {
        if (!_members.Select(Identity).SequenceEqual(_identities)) throw new InvalidOperationException("Island child identities changed during a run.");
    }
}
