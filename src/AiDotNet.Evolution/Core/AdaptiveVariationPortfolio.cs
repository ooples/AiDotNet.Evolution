using System.Text.Json;

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
/// </remarks>
public sealed class AdaptiveVariationPortfolio<TGenome> : IOutcomeAwareVariationOperator<TGenome>
{
    private const int MaximumOperators = 256;
    private const int MaximumPending = 65_536;
    private const int MaximumStateCharacters = 16 * 1024 * 1024;
    private readonly IVariationOperator<TGenome>[] _operators;
    private readonly double _explorationProbability;
    private ArmState[] _arms;
    private SortedDictionary<long, int> _pending = new();

    /// <summary>Creates an ordered portfolio with a nonzero exploration probability in (0, 1].</summary>
    /// <param name="operators">Between one and 256 operators with unique, stable identities.</param>
    /// <param name="explorationProbability">Probability of a uniformly random choice after initial trials.</param>
    public AdaptiveVariationPortfolio(IEnumerable<IVariationOperator<TGenome>> operators,
        double explorationProbability = 0.1)
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
        _arms = _operators.Select(_ => new ArmState()).ToArray();
        VersionHash = EvolutionHash.Combine(new[] { "adaptive-variation-v1", EvolutionHash.EncodeDouble(explorationProbability) }
            .Concat(_operators.SelectMany(op => new[] { op.Id, op.VersionHash })));
    }

    /// <inheritdoc/>
    public string Id => "adaptive-variation";

    /// <inheritdoc/>
    public string VersionHash { get; }

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
        if (_operators[index] is IOutcomeAwareVariationOperator<TGenome> adaptive)
            adaptive.Observe(evaluation, insertionResult);
        _pending.Remove(evaluation.Lineage.Generation);
        _arms[index].Outcomes = checked(_arms[index].Outcomes + 1);
        _arms[index].RewardSum += reward;
    }

    /// <inheritdoc/>
    public string CaptureState()
    {
        string state = JsonSerializer.Serialize(new PortfolioState
        {
            VersionHash = VersionHash,
            Arms = _arms,
            Pending = _pending,
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
        public string?[]? Children { get; set; }
    }
}
