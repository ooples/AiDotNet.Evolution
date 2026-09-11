using System.Text.Json;

namespace AiDotNet.Evolution;

public sealed partial class AdaptiveIslandSearch<TGenome>
{
    /// <inheritdoc/>
    public string CaptureState()
    {
        EnsureIdentities();
        string state = JsonSerializer.Serialize(new PolicyState
        {
            VersionHash = VersionHash,
            Islands = _islands,
            Pending = _pending,
            Decisions = _decisions,
            Epoch = _epoch,
            LastGeneration = _lastGeneration,
            Children = _members.SelectMany(member => new[] { member.Variation, member.Restart })
                .Select(child => (child as ICheckpointableVariationOperator<TGenome>)?.CaptureState()).ToArray()
        }, EvolutionJson.Compact);
        if (state.Length > MaximumStateCharacters) throw new InvalidOperationException("The island state exceeds its safety limit.");
        return state;
    }

    /// <inheritdoc/>
    /// <remarks>Validates the policy graph before restoring any child. If a child rejects state, discard this instance.</remarks>
    public void RestoreState(string state)
    {
        Guard.NotNull(state);
        EnsureIdentities();
        if (state.Length > MaximumStateCharacters) throw new InvalidDataException("The island state exceeds its safety limit.");
        PolicyState? deserialized;
        try { deserialized = JsonSerializer.Deserialize<PolicyState>(state, EvolutionJson.Compact); }
        catch (JsonException exception) { throw new InvalidDataException("The island state is invalid.", exception); }
        ValidatedPolicyState restored = Validate(deserialized);
        IVariationOperator<TGenome>[] children = _members.SelectMany(member => new[] { member.Variation, member.Restart }).ToArray();
        for (int i = 0; i < children.Length; i++)
            if ((children[i] is ICheckpointableVariationOperator<TGenome>) != (restored.Children[i] is not null))
                throw new InvalidDataException("The island child state is missing or unexpected.");
        for (int i = 0; i < children.Length; i++)
            if (children[i] is ICheckpointableVariationOperator<TGenome> child && restored.Children[i] is string childState)
                child.RestoreState(childState);
        _islands = restored.Islands; _pending = restored.Pending; _decisions = restored.Decisions;
        _epoch = restored.Epoch; _lastGeneration = restored.LastGeneration;
    }

    private ValidatedPolicyState Validate(PolicyState? state)
    {
        if (state is null || state.VersionHash != VersionHash || state.Islands is null || state.Islands.Length != IslandCount ||
            state.Pending is null || state.Pending.Count > MaximumPending || state.Decisions is null || state.Decisions.Count > MaximumDecisions ||
            state.Children is null || state.Children.Length != IslandCount * 2 || state.Epoch < 0 || state.LastGeneration < 0)
            throw new InvalidDataException("The island state is incompatible or incomplete.");
        var counts = new int[IslandCount]; var restarts = new int[IslandCount];
        foreach (KeyValuePair<long, Attribution> item in state.Pending)
        {
            Attribution attribution = item.Value;
            if (item.Key <= 0 || item.Key > state.LastGeneration || attribution is null || attribution.Island < 0 || attribution.Island >= IslandCount ||
                (attribution.ParentQuality.HasValue && !EvolutionDescriptorDefinition.IsFinite(attribution.ParentQuality.Value)))
                throw new InvalidDataException("The island pending attribution is invalid.");
            counts[attribution.Island]++;
            if (attribution.Restart) restarts[attribution.Island]++;
        }
        decimal total = 0; int epochTotal = 0;
        for (int i = 0; i < IslandCount; i++)
        {
            IslandState island = state.Islands[i];
            if (island is null || island.Outcomes < 0 || island.Proposals < island.Outcomes || island.Proposals - island.Outcomes != counts[i] ||
                island.Fresh < 0 || island.Fresh > island.Outcomes || island.RestartOutcomes < 0 || island.RestartOutcomes > island.Outcomes ||
                island.RestartProposals < island.RestartOutcomes || island.RestartProposals > island.Proposals ||
                island.RestartProposals - island.RestartOutcomes != restarts[i] || island.Phases < 0 || island.Phases > island.Outcomes ||
                island.Remaining < 0 || island.Remaining > _options.RestartProposals ||
                (decimal)island.Phases * _options.RestartProposals != (decimal)island.RestartProposals + island.Remaining ||
                island.Stagnation < 0 || island.Stagnation > _options.StagnationOutcomes || island.Stagnation > island.Outcomes ||
                island.EpochProposals < 0 || island.EpochProposals > _epochLength || island.EpochProposals > island.Proposals ||
                island.Proposals < (decimal)state.Epoch * _options.MinimumPerIslandPerEpoch + island.EpochProposals ||
                island.Recent is null || island.Recent.Count != Math.Min((long)_options.RewardWindow, island.Outcomes) ||
                island.Recent.Any(reward => reward is null || !Unit(reward.Gain) || (reward.Diversity != 0 && reward.Diversity != 1)) ||
                island.Recent.Count(reward => reward.Gain > 0 || reward.Diversity > 0) > island.Fresh ||
                (!_options.EnableRestarts && (island.Phases != 0 || island.RestartProposals != 0 || island.Remaining != 0)))
                throw new InvalidDataException("The island statistics are invalid.");
            total += island.Proposals; epochTotal += island.EpochProposals;
        }
        var validated = new ValidatedPolicyState(state.Islands, state.Pending, state.Decisions,
            state.Children, state.Epoch, state.LastGeneration);
        ValidateAllocationAccounting(validated, total, epochTotal);
        ValidateRecordedAllocationHistory(validated, total);
        ValidateDecisionHistory(validated);
        return validated;
    }

    private void ValidateAllocationAccounting(ValidatedPolicyState state, decimal total, int epochTotal)
    {
        if (epochTotal > _epochLength)
            throw new InvalidDataException("The island allocation history is inconsistent.");
        if (epochTotal == _epochLength && state.Islands.Any(island => island.EpochProposals < _options.MinimumPerIslandPerEpoch))
            throw new InvalidDataException("The island allocation history is inconsistent.");
        if (total != (decimal)state.Epoch * _epochLength + epochTotal || total > state.LastGeneration)
            throw new InvalidDataException("The island allocation history is inconsistent.");
    }

    private static void ValidateRecordedAllocationHistory(ValidatedPolicyState state, decimal total)
    {
        if (state.Decisions.Count != Math.Min((decimal)MaximumDecisions, total))
            throw new InvalidDataException("The island allocation history is inconsistent.");
        if (total == 0)
        {
            if (state.LastGeneration != 0)
                throw new InvalidDataException("The island allocation history is inconsistent.");
            return;
        }
        if (state.Decisions[state.Decisions.Count - 1]?.Generation != state.LastGeneration)
            throw new InvalidDataException("The island allocation history is inconsistent.");
    }

    private void ValidateDecisionHistory(ValidatedPolicyState state)
    {
        long last = 0;
        foreach (DecisionState decision in state.Decisions)
        {
            if (decision is null)
                throw new InvalidDataException("The island decision history is invalid.");
            ValidateDecisionOrder(decision, last, state.LastGeneration);
            ValidateDecisionDestination(decision);
            ValidateDecisionAttribution(decision, state.Pending);
            last = decision.Generation;
        }
    }

    private static void ValidateDecisionOrder(DecisionState decision, long last, long lastGeneration)
    {
        if (decision.Generation <= last || decision.Generation > lastGeneration)
            throw new InvalidDataException("The island decision history is invalid.");
    }

    private void ValidateDecisionDestination(DecisionState decision)
    {
        if (decision.Island < 0 || decision.Island >= IslandCount)
            throw new InvalidDataException("The island decision history is invalid.");
        if (!_options.EnableRestarts && decision.Restart)
            throw new InvalidDataException("The island decision history is invalid.");
    }

    private static void ValidateDecisionAttribution(DecisionState decision, SortedDictionary<long, Attribution> pending)
    {
        if (pending.TryGetValue(decision.Generation, out Attribution? attribution) &&
            (attribution.Island != decision.Island || attribution.Restart != decision.Restart))
            throw new InvalidDataException("The island decision history is invalid.");
    }

    // Holds the actual validated, privately deserialized collections. It does not substitute
    // empty collections for missing JSON, copy the policy graph, or expose nullable state fields.
    private readonly struct ValidatedPolicyState
    {
        public ValidatedPolicyState(IslandState[] islands, SortedDictionary<long, Attribution> pending,
            List<DecisionState> decisions, string?[] children, long epoch, long lastGeneration)
        {
            Islands = islands;
            Pending = pending;
            Decisions = decisions;
            Children = children;
            Epoch = epoch;
            LastGeneration = lastGeneration;
        }

        public IslandState[] Islands { get; }
        public SortedDictionary<long, Attribution> Pending { get; }
        public List<DecisionState> Decisions { get; }
        public string?[] Children { get; }
        public long Epoch { get; }
        public long LastGeneration { get; }
    }

    private static bool Unit(double value) => EvolutionDescriptorDefinition.IsFinite(value) && value >= 0 && value <= 1;
    private sealed class PolicyState
    {
        public PolicyState() { }
        public string? VersionHash { get; set; }
        public IslandState[]? Islands { get; set; }
        public SortedDictionary<long, Attribution>? Pending { get; set; }
        public List<DecisionState>? Decisions { get; set; }
        public string?[]? Children { get; set; }
        public long Epoch { get; set; }
        public long LastGeneration { get; set; }
    }
    private sealed class IslandState
    {
        public IslandState() { }
        public long Proposals { get; set; }
        public long Outcomes { get; set; }
        public long Fresh { get; set; }
        public long RestartProposals { get; set; }
        public long RestartOutcomes { get; set; }
        public long Phases { get; set; }
        public int Remaining { get; set; }
        public int Stagnation { get; set; }
        public int EpochProposals { get; set; }
        public List<Reward> Recent { get; set; } = new();
    }
    private sealed class Reward
    {
        public Reward() { }
        public double Gain { get; set; }
        public double Diversity { get; set; }
    }
    private sealed class Attribution
    {
        public Attribution() { }
        public int Island { get; set; }
        public bool Restart { get; set; }
        public double? ParentQuality { get; set; }
    }
    private sealed class DecisionState
    {
        public DecisionState() { }
        public long Generation { get; set; }
        public int Island { get; set; }
        public bool Restart { get; set; }
    }
}
