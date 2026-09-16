using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

/// <summary>Selects variation operators using checkpointed, cost-normalized archive-success feedback.</summary>
/// <typeparam name="TGenome">The immutable task-specific genome.</typeparam>
/// <remarks>
/// Each operator is tried once, then epsilon-greedy selection balances exploration and the highest mean reward.
/// A fresh completed archive insertion or replacement earns 1 / max(1, evaluator cost units); everything else
/// earns zero. This bounded reward measures archive success, not scalar gain or end-to-end model spending.
/// Costs must use the same deterministic units across operators. Elapsed time never influences selection.
/// Generation numbers attribute terminal outcomes even when proposals fail before canonicalization. Snapshots
/// retain pending attribution and child state. Use a fresh instance for each run; methods are serialized by the
/// engine and are not intended for concurrent external callers. Child identities and configuration must stay fixed.
/// Supplying an explicit reward policy opts into versioned parent-relative gains and/or proposal-plus-evaluator costs.
/// The default constructor retains the original archive-success semantics and checkpoint representation.
/// </remarks>
public sealed class AdaptiveVariationPortfolio<TGenome> : IOutcomeAwareVariationOperator<TGenome>
{
    private const int MaximumOperators = 256;
    private const int MaximumPending = 65_536;
    private const int MaximumStateCharacters = 16 * 1024 * 1024;
    private readonly IVariationOperator<TGenome>[] _operators;
    private readonly double _explorationProbability;
    private readonly EvolutionOperatorRewardPolicy? _rewardPolicy;
    private ArmState[] _arms;
    private SortedDictionary<long, int> _pending = new();
    private SortedDictionary<long, ParentCredit>? _credit;

    /// <summary>Creates the original archive-success/evaluator-cost portfolio without changing its binary or checkpoint contract.</summary>
    public AdaptiveVariationPortfolio(IEnumerable<IVariationOperator<TGenome>> operators, double explorationProbability = 0.1)
        : this(operators, explorationProbability, null) { }

    /// <summary>Creates an opt-in portfolio with explicit gain/cost semantics and nonzero exploration.</summary>
    public AdaptiveVariationPortfolio(IEnumerable<IVariationOperator<TGenome>> operators, EvolutionOperatorRewardPolicy rewardPolicy, double explorationProbability = 0.1)
        : this(operators, explorationProbability, rewardPolicy) => Guard.NotNull(rewardPolicy);

    /// <summary>Creates an ordered portfolio with a nonzero exploration probability in (0, 1].</summary>
    /// <param name="operators">Between one and 256 operators with unique, stable identities.</param>
    /// <param name="explorationProbability">Probability of a uniformly random choice after initial trials.</param>
    /// <param name="rewardPolicy">Optional fixed gain/cost semantics. Proposal-inclusive credit requires checkpointed cost providers with matching unit identities.</param>
    private AdaptiveVariationPortfolio(IEnumerable<IVariationOperator<TGenome>> operators,
        double explorationProbability, EvolutionOperatorRewardPolicy? rewardPolicy)
    {
        Guard.NotNull(operators);
        if (!EvolutionDescriptorDefinition.IsFinite(explorationProbability) ||
            explorationProbability <= 0 || explorationProbability > 1)
            throw new ArgumentOutOfRangeException(nameof(explorationProbability));
        _operators = operators.Take(MaximumOperators + 1).ToArray();
        if (_operators.Length == 0 || _operators.Length > MaximumOperators ||
            _operators.Any(op => op is null || string.IsNullOrWhiteSpace(op.Id) ||
                string.IsNullOrWhiteSpace(op.VersionHash)) ||
            _operators.Select(op => op.Id).Distinct(StringComparer.Ordinal).Count() != _operators.Length)
            throw new ArgumentException("The portfolio requires 1 to 256 operators with unique identities.", nameof(operators));
        _explorationProbability = explorationProbability;
        _rewardPolicy = rewardPolicy;
        if (rewardPolicy?.CostBasis == EvolutionOperatorCostBasis.ProposalAndEvaluation && _operators.Any(op =>
                op is not ICheckpointableVariationOperator<TGenome> || op is not IEvolutionProposalCostProvider provider || provider.CostUnitVersionHash != rewardPolicy.CostUnitVersionHash))
            throw new ArgumentException("Every operator must supply checkpointed proposal costs using the policy's declared units.", nameof(operators));
        if (rewardPolicy is not null) _credit = new();
        _arms = _operators.Select(_ => new ArmState()).ToArray();
        VersionHash = EvolutionHash.Combine(new[] { "adaptive-variation-v1", EvolutionHash.EncodeDouble(explorationProbability) }
            .Concat(_operators.SelectMany(op => new[] { op.Id, op.VersionHash })));
        if (rewardPolicy is not null) VersionHash = EvolutionHash.Combine(new[] { "adaptive-variation-credit-v2", VersionHash, rewardPolicy.VersionHash });
    }

    /// <inheritdoc/>
    public string Id => "adaptive-variation";

    /// <inheritdoc/>
    public string VersionHash { get; }

    /// <summary>Gets detached attribution for the last committed outcome, or null before feedback and immediately after restore.</summary>
    /// <remarks>This bounded diagnostic view is not checkpointed and never influences selection. Persist it externally
    /// when a full audit trace is needed; learned totals and pending attribution remain in CaptureState.</remarks>
    public EvolutionOperatorCredit? LastCredit { get; private set; }

    /// <summary>Returns detached per-operator statistics, in constructor order.</summary>
    public IReadOnlyList<EvolutionOperatorStatistics> Statistics => Array.AsReadOnly(_arms
        .Select((arm, index) => new EvolutionOperatorStatistics(_operators[index].Id,
            arm.Proposals, arm.Outcomes, arm.RewardSum)).ToArray());

    /// <inheritdoc/>
    public ValueTask<TGenome> ProposeAsync(EvolutionVariationContext<TGenome> context,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Generation <= 0 || _pending.ContainsKey(context.Generation))
            throw new ArgumentException("A proposal requires a unique positive generation.", nameof(context));
        if (_pending.Count >= MaximumPending)
            throw new InvalidOperationException("The portfolio pending-outcome limit was reached.");
        int index = Array.FindIndex(_arms, arm => arm.Proposals == 0);
        if (index < 0)
        {
            index = context.Random.NextDouble() < _explorationProbability
                ? context.Random.NextInt(_arms.Length)
                : Enumerable.Range(0, _arms.Length).OrderByDescending(i => MeanReward(_arms[i])).First();
        }
        _arms[index].Proposals = checked(_arms[index].Proposals + 1);
        _pending.Add(context.Generation, index);
        if (_credit is not null)
            _credit.Add(context.Generation, new ParentCredit
            {
                Quality = context.Parent.Evaluation.ConstraintViolations.Any(value => value > 0) ? null : context.Parent.Evaluation.Quality,
                Direction = context.Parent.Evaluation.Direction
            });
        // Keep attribution if a child throws: the engine still commits its failed proposal.
        return _operators[index].ProposeAsync(context, cancellationToken);
    }

    /// <inheritdoc/>
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult)
    {
        Guard.NotNull(evaluation);
        if (!_pending.TryGetValue(evaluation.Lineage.Generation, out int index))
            throw new InvalidOperationException("The portfolio received an unknown or repeated outcome.");
        bool improved = insertionResult is EvolutionArchiveInsertionResult.Inserted or
            EvolutionArchiveInsertionResult.Replaced or EvolutionArchiveInsertionResult.InsertedWithEviction;
        double reward = evaluation.Status == EvolutionEvaluationStatus.Completed &&
            evaluation.CacheStatus != EvolutionCacheStatus.Hit && improved
            ? 1 / Math.Max(1, evaluation.Cost.CostUnits) : 0;
        ParentCredit? baseline = null;
        EvolutionProposalCost? cost = null;
        if (_rewardPolicy is not null)
        {
            baseline = _credit![evaluation.Lineage.Generation];
            cost = _rewardPolicy.CostBasis == EvolutionOperatorCostBasis.ProposalAndEvaluation
                ? ((IEvolutionProposalCostProvider)_operators[index]).GetProposalCost(evaluation.Lineage.Generation) : null;
            reward = _rewardPolicy.Reward(baseline.Quality, baseline.Direction, evaluation, insertionResult, cost);
        }
        if (_operators[index] is IOutcomeAwareVariationOperator<TGenome> adaptive)
            adaptive.Observe(evaluation, insertionResult);
        _pending.Remove(evaluation.Lineage.Generation);
        _credit?.Remove(evaluation.Lineage.Generation);
        _arms[index].Outcomes = checked(_arms[index].Outcomes + 1);
        _arms[index].RewardSum += reward;
        LastCredit = new(evaluation.Lineage.Generation, _operators[index].Id, _operators[index].VersionHash,
            _rewardPolicy?.VersionHash ?? "legacy-archive-success-evaluator-cost-v1", baseline?.Quality, evaluation, insertionResult, cost, reward);
    }

    /// <inheritdoc/>
    public string CaptureState()
    {
        string state = JsonSerializer.Serialize(new PortfolioState
        {
            VersionHash = VersionHash,
            Arms = _arms,
            Pending = _pending,
            Credit = _credit,
            Children = _operators.Select(op =>
                (op as ICheckpointableVariationOperator<TGenome>)?.CaptureState()).ToArray()
        }, EvolutionJson.Compact);
        if (state.Length > MaximumStateCharacters)
            throw new InvalidOperationException("The portfolio state exceeds its safety limit.");
        return state;
    }

    /// <inheritdoc/>
    /// <remarks>If a child rejects its state, discard this instance before retrying a restore.</remarks>
    public void RestoreState(string state)
    {
        Guard.NotNull(state);
        if (state.Length > MaximumStateCharacters)
            throw new InvalidDataException("The portfolio state exceeds its safety limit.");
        PortfolioState? restored;
        try { restored = JsonSerializer.Deserialize<PortfolioState>(state, EvolutionJson.Compact); }
        catch (JsonException exception) { throw new InvalidDataException("The portfolio state is invalid.", exception); }
        if (restored is null || restored.VersionHash != VersionHash || restored.Arms is null ||
            restored.Arms.Length != _arms.Length || restored.Children is null ||
            restored.Children.Length != _arms.Length || restored.Pending is null || restored.Pending.Count > MaximumPending)
            throw new InvalidDataException("The portfolio state is incompatible or incomplete.");
        var pendingCounts = new int[_arms.Length];
        foreach (KeyValuePair<long, int> pending in restored.Pending)
        {
            if (pending.Key <= 0 || pending.Value < 0 || pending.Value >= _arms.Length)
                throw new InvalidDataException("The portfolio attribution is invalid.");
            pendingCounts[pending.Value]++;
        }
        if ((_rewardPolicy is null) != (restored.Credit is null)) throw new InvalidDataException("Missing or unexpected parent-credit state.");
        if (restored.Credit is not null)
        {
            if (!restored.Credit.Keys.SequenceEqual(restored.Pending.Keys)) throw new InvalidDataException("Parent-credit identities do not match pending attribution.");
            foreach (ParentCredit credit in restored.Credit.Values)
                if (credit is null || (credit.Quality.HasValue && !EvolutionDescriptorDefinition.IsFinite(credit.Quality.Value)) ||
                    !Enum.IsDefined(typeof(EvolutionOptimizationDirection), credit.Direction)) throw new InvalidDataException("Invalid parent-credit baseline.");
        }
        for (int i = 0; i < _arms.Length; i++)
        {
            ArmState arm = restored.Arms[i];
            if (arm is null || arm.Outcomes < 0 || arm.Proposals < arm.Outcomes ||
                arm.Proposals - arm.Outcomes != pendingCounts[i] ||
                !EvolutionDescriptorDefinition.IsFinite(arm.RewardSum) || arm.RewardSum < 0 || arm.RewardSum > arm.Outcomes ||
                (_operators[i] is ICheckpointableVariationOperator<TGenome>) != (restored.Children[i] is not null))
                throw new InvalidDataException("The portfolio statistics or child state are invalid.");
        }
        for (int i = 0; i < _arms.Length; i++)
            if (_operators[i] is ICheckpointableVariationOperator<TGenome> child)
                child.RestoreState(restored.Children[i]!);
        _arms = restored.Arms;
        _pending = restored.Pending;
        _credit = restored.Credit;
        LastCredit = null;
    }

    private static double MeanReward(ArmState arm) => arm.Outcomes == 0 ? 0 : arm.RewardSum / arm.Outcomes;

    private sealed class ArmState
    {
        public ArmState() { }
        public long Proposals { get; set; }
        public long Outcomes { get; set; }
        public double RewardSum { get; set; }
    }

    private sealed class PortfolioState
    {
        public PortfolioState() { }
        public string? VersionHash { get; set; }
        public ArmState[]? Arms { get; set; }
        public SortedDictionary<long, int>? Pending { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SortedDictionary<long, ParentCredit>? Credit { get; set; }
        public string?[]? Children { get; set; }
    }

    private sealed class ParentCredit
    {
        public ParentCredit() { }
        public double? Quality { get; set; }
        public EvolutionOptimizationDirection Direction { get; set; }
    }
}
