using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>A stable island identity and its independently owned normal and exploration-restart operators.</summary>
/// <typeparam name="TGenome">The owned task genome.</typeparam>
public sealed class EvolutionIslandStrategy<TGenome>
{
    /// <summary>Creates a fixed island member. Stateful children must implement the checkpoint/outcome contracts.</summary>
    public EvolutionIslandStrategy(string id, IVariationOperator<TGenome> variation, IVariationOperator<TGenome> restart)
    {
        Guard.NotNullOrWhiteSpace(id); Guard.NotNull(variation); Guard.NotNull(restart);
        if (id.Length > 128 || id.Any(char.IsControl)) throw new ArgumentException("Island identity is oversized or nonprintable.", nameof(id));
        if (string.IsNullOrWhiteSpace(variation.Id) || string.IsNullOrWhiteSpace(variation.VersionHash) ||
            string.IsNullOrWhiteSpace(restart.Id) || string.IsNullOrWhiteSpace(restart.VersionHash))
            throw new ArgumentException("Island children require stable component identities.");
        Id = id; Variation = variation; Restart = restart;
    }
    /// <summary>Gets the stable membership identity; reordering or renaming members changes compatibility.</summary>
    public string Id { get; }
    /// <summary>Gets the ordinary proposal operator.</summary>
    public IVariationOperator<TGenome> Variation { get; }
    /// <summary>Gets the independently owned exploration operator, normally a task-specific random restart.</summary>
    public IVariationOperator<TGenome> Restart { get; }
}

/// <summary>Immutable bounded allocation, progress and exploration-phase settings.</summary>
public sealed class EvolutionIslandPolicyOptions
{
    /// <summary>Creates opt-in adaptive allocation and soft exploration restarts with a guaranteed proposal floor.</summary>
    /// <param name="proposalsPerIslandPerEpoch">Epoch length divided by island count, from 1 to 1024.</param>
    /// <param name="minimumPerIslandPerEpoch">Guaranteed proposal slots per island in each complete allocation epoch.</param>
    /// <param name="rewardWindow">Recent terminal outcomes retained per island, from 1 to 4096.</param>
    /// <param name="gainScale">Positive finite scalar gain that maps to unit reward.</param>
    /// <param name="diversityWeight">Mixture weight [0,1] for fresh feasible new-cell insertion.</param>
    /// <param name="stagnationOutcomes">Terminal outcomes without fresh gain/diversity before scheduling exploration.</param>
    /// <param name="restartProposals">Bounded independent proposals in each exploration phase.</param>
    /// <param name="adaptiveAllocation">False retains uniform allocation after the same mandatory exploration floor.</param>
    /// <param name="enableRestarts">False never substitutes the exploration operator.</param>
    /// <param name="direction">Scalar direction used to interpret parent-relative gains.</param>
    public EvolutionIslandPolicyOptions(int proposalsPerIslandPerEpoch = 16, int minimumPerIslandPerEpoch = 1,
        int rewardWindow = 32, double gainScale = 1, double diversityWeight = 0.25, int stagnationOutcomes = 64,
        int restartProposals = 8, bool adaptiveAllocation = true, bool enableRestarts = true,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        if (proposalsPerIslandPerEpoch < 1 || proposalsPerIslandPerEpoch > 1024 || minimumPerIslandPerEpoch < 1 ||
            minimumPerIslandPerEpoch > proposalsPerIslandPerEpoch) throw new ArgumentOutOfRangeException(nameof(proposalsPerIslandPerEpoch));
        if (rewardWindow < 1 || rewardWindow > 4096) throw new ArgumentOutOfRangeException(nameof(rewardWindow));
        if (!EvolutionDescriptorDefinition.IsFinite(gainScale) || gainScale <= 0) throw new ArgumentOutOfRangeException(nameof(gainScale));
        if (!EvolutionDescriptorDefinition.IsFinite(diversityWeight) || diversityWeight < 0 || diversityWeight > 1)
            throw new ArgumentOutOfRangeException(nameof(diversityWeight));
        if (stagnationOutcomes < 1 || stagnationOutcomes > 1_000_000 || restartProposals < 1 || restartProposals > 4096)
            throw new ArgumentOutOfRangeException(nameof(stagnationOutcomes));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ProposalsPerIslandPerEpoch = proposalsPerIslandPerEpoch; MinimumPerIslandPerEpoch = minimumPerIslandPerEpoch;
        RewardWindow = rewardWindow; GainScale = gainScale; DiversityWeight = diversityWeight; StagnationOutcomes = stagnationOutcomes;
        RestartProposals = restartProposals; AdaptiveAllocation = adaptiveAllocation; EnableRestarts = enableRestarts; Direction = direction;
        VersionHash = EvolutionHash.Combine(new[] { "island-policy-options-v1", proposalsPerIslandPerEpoch.ToString(CultureInfo.InvariantCulture),
            minimumPerIslandPerEpoch.ToString(CultureInfo.InvariantCulture), rewardWindow.ToString(CultureInfo.InvariantCulture),
            EvolutionHash.EncodeDouble(gainScale), EvolutionHash.EncodeDouble(diversityWeight), stagnationOutcomes.ToString(CultureInfo.InvariantCulture),
            restartProposals.ToString(CultureInfo.InvariantCulture), adaptiveAllocation.ToString(), enableRestarts.ToString(), direction.ToString() });
    }
    /// <summary>Gets epoch length per configured island.</summary>
    public int ProposalsPerIslandPerEpoch { get; }
    /// <summary>Gets the hard proposal floor per island and complete epoch.</summary>
    public int MinimumPerIslandPerEpoch { get; }
    /// <summary>Gets the bounded recent-outcome window.</summary>
    public int RewardWindow { get; }
    /// <summary>Gets the declared gain normalization.</summary>
    public double GainScale { get; }
    /// <summary>Gets the weight of new-cell diversity.</summary>
    public double DiversityWeight { get; }
    /// <summary>Gets the declared stagnation window.</summary>
    public int StagnationOutcomes { get; }
    /// <summary>Gets the proposal cap of an exploration phase.</summary>
    public int RestartProposals { get; }
    /// <summary>Gets whether recent measured productivity influences allocation.</summary>
    public bool AdaptiveAllocation { get; }
    /// <summary>Gets whether bounded exploration phases may be scheduled.</summary>
    public bool EnableRestarts { get; }
    /// <summary>Gets the scalar progress direction.</summary>
    public EvolutionOptimizationDirection Direction { get; }
    /// <summary>Gets the fingerprint of every policy setting.</summary>
    public string VersionHash { get; }
}

/// <summary>A detached per-island accounting and progress view.</summary>
public sealed class EvolutionIslandStatistics
{
    internal EvolutionIslandStatistics(string id, long proposals, long outcomes, long fresh, long restartProposals,
        long restartOutcomes, long phases, int remaining, int epochProposals, double recentGain, double recentDiversity)
    {
        Id = id; Proposals = proposals; Outcomes = outcomes; FreshMeasurements = fresh; RestartProposals = restartProposals;
        RestartOutcomes = restartOutcomes; RestartPhasesScheduled = phases; RemainingRestartProposals = remaining;
        EpochProposals = epochProposals; MeanRecentGain = recentGain; MeanRecentDiversity = recentDiversity;
    }
    /// <summary>Gets the fixed membership identity.</summary>
    public string Id { get; }
    /// <summary>Gets prepared operator proposals, including failures and restarts but not seeds.</summary>
    public long Proposals { get; }
    /// <summary>Gets terminal committed outcomes.</summary>
    public long Outcomes { get; }
    /// <summary>Gets fresh feasible completed outcomes with the declared direction.</summary>
    public long FreshMeasurements { get; }
    /// <summary>Gets proposals actually routed to the restart operator.</summary>
    public long RestartProposals { get; }
    /// <summary>Gets terminal restart outcomes.</summary>
    public long RestartOutcomes { get; }
    /// <summary>Gets scheduled exploration phases, including a phase not yet started when a run stops.</summary>
    public long RestartPhasesScheduled { get; }
    /// <summary>Gets remaining proposal slots in the current exploration phase.</summary>
    public int RemainingRestartProposals { get; }
    /// <summary>Gets proposals allocated in the current epoch.</summary>
    public int EpochProposals { get; }
    /// <summary>Gets bounded mean fresh normalized parent improvement; failed/reused outcomes contribute zero.</summary>
    public double MeanRecentGain { get; }
    /// <summary>Gets mean fresh feasible new-cell insertion over recent terminal outcomes.</summary>
    public double MeanRecentDiversity { get; }
}

/// <summary>A detached recent allocation decision; the same bounded history is checkpointed.</summary>
public sealed class EvolutionIslandDecision
{
    internal EvolutionIslandDecision(long generation, int island, string islandId, string operatorId, bool restart)
    { Generation = generation; Island = island; IslandId = islandId; OperatorId = operatorId; Restart = restart; }
    /// <summary>Gets the stable proposal generation.</summary>
    public long Generation { get; }
    /// <summary>Gets the fixed destination index.</summary>
    public int Island { get; }
    /// <summary>Gets the fixed destination identity.</summary>
    public string IslandId { get; }
    /// <summary>Gets the actual child operator identity.</summary>
    public string OperatorId { get; }
    /// <summary>Gets whether the proposal used bounded exploration rather than its normal operator.</summary>
    public bool Restart { get; }
}
