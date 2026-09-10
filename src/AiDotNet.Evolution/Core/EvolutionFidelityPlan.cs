using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>A versioned cumulative evaluator-resource level, distinct from a fixed pass/fail cascade stage.</summary>
public sealed class EvolutionFidelityLevel
{
    /// <summary>Declares a named increasing resource quantity and the maximum actual cost of one replicate at that level.</summary>
    /// <remarks>ResourceLevel describes epochs, dataset size or another consumer-defined cumulative quantity.
    /// MaximumCostPerReplicate is the same-unit ledger upper bound, not a derived quantity or assumed resume discount.</remarks>
    public EvolutionFidelityLevel(string id, long resourceLevel, decimal maximumCostPerReplicate)
    {
        Guard.NotNullOrWhiteSpace(id);
        if (id.Length > 64 || id.Any(char.IsControl)) throw new ArgumentException("Bounded printable fidelity identity required.", nameof(id));
        if (resourceLevel is < 1 or > 1_000_000_000) throw new ArgumentOutOfRangeException(nameof(resourceLevel));
        if (maximumCostPerReplicate < 0 || maximumCostPerReplicate > EvolutionResources.MaximumAmount)
            throw new ArgumentOutOfRangeException(nameof(maximumCostPerReplicate));
        Id = id; ResourceLevel = resourceLevel; MaximumCostPerReplicate = maximumCostPerReplicate;
        VersionHash = EvolutionHash.Combine(new[] { "fidelity-level-v1", id, resourceLevel.ToString(CultureInfo.InvariantCulture), maximumCostPerReplicate.ToString(CultureInfo.InvariantCulture) });
    }
    /// <summary>Gets the unique level identity within the plan.</summary>
    public string Id { get; }
    /// <summary>Gets the cumulative consumer-defined evaluator resource level.</summary>
    public long ResourceLevel { get; }
    /// <summary>Gets the externally enforced maximum cost reserved for each measurement.</summary>
    public decimal MaximumCostPerReplicate { get; }
    /// <summary>Gets the identity binding resource and cost semantics.</summary>
    public string VersionHash { get; }
}

/// <summary>A bounded synchronous successive-halving plan with explicit exploration and fresh final confirmation.</summary>
/// <remarks>This is one promotion bracket, not the complete Hyperband bracket-allocation algorithm.</remarks>
public sealed class EvolutionFidelityPlan
{
    /// <summary>Creates 2..8 increasing levels, 2..16 replicates per batch and a bounded survivor policy.</summary>
    public EvolutionFidelityPlan(IEnumerable<EvolutionFidelityLevel> levels, double minimumQuality, double maximumQuality,
        int replicates = 2, int reductionFactor = 2, int minimumSurvivors = 2, double explorationFraction = 0.25,
        double confidence = 0.95, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(levels);
        var copy = levels.Take(9).ToArray();
        if (copy.Length is < 2 or > 8 || copy.Any(level => level is null) || copy.Select(level => level.Id).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Require 2..8 uniquely named fidelity levels.", nameof(levels));
        for (int i = 1; i < copy.Length; i++)
            if (copy[i].ResourceLevel <= copy[i - 1].ResourceLevel) throw new ArgumentException("Fidelity resource levels must strictly increase.", nameof(levels));
        if (replicates is < 2 or > 16 || reductionFactor is < 2 or > 16 || minimumSurvivors is < 2 or > 64)
            throw new ArgumentOutOfRangeException(nameof(replicates));
        if (!EvolutionDescriptorDefinition.IsFinite(explorationFraction) || explorationFraction < 0 || explorationFraction > 0.5)
            throw new ArgumentOutOfRangeException(nameof(explorationFraction));
        if (!EvolutionDescriptorDefinition.IsFinite(confidence) || confidence < 0.8 || confidence > 0.99)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        // Reuse the bounded replication contract's validation, not a second conflicting score policy.
        _ = new EvolutionReplicationPlan(replicates, replicates, minimumQuality, maximumQuality, 0, confidence, direction: direction);
        Levels = Array.AsReadOnly(copy); MinimumQuality = minimumQuality; MaximumQuality = maximumQuality; Replicates = replicates;
        ReductionFactor = reductionFactor; MinimumSurvivors = minimumSurvivors; ExplorationFraction = explorationFraction; Confidence = confidence; Direction = direction;
        VersionHash = EvolutionHash.Combine(new[] { "fidelity-plan-v1", Bits(minimumQuality), Bits(maximumQuality), replicates.ToString(CultureInfo.InvariantCulture),
            reductionFactor.ToString(CultureInfo.InvariantCulture), minimumSurvivors.ToString(CultureInfo.InvariantCulture), Bits(explorationFraction), Bits(confidence), direction.ToString() }
            .Concat(copy.Select(level => level.VersionHash)));
    }
    /// <summary>Gets the immutable low-to-high resource ladder.</summary>
    public IReadOnlyList<EvolutionFidelityLevel> Levels { get; }
    /// <summary>Gets the known lower quality support bound, declared before observing scores.</summary>
    public double MinimumQuality { get; }
    /// <summary>Gets the known upper quality support bound.</summary>
    public double MaximumQuality { get; }
    /// <summary>Gets the fresh replicate count at every level and during final confirmation.</summary>
    public int Replicates { get; }
    /// <summary>Gets the survivor-count divisor.</summary>
    public int ReductionFactor { get; }
    /// <summary>Gets the minimum survivors when enough valid candidates remain.</summary>
    public int MinimumSurvivors { get; }
    /// <summary>Gets the exploration share, rounded up to at least one non-greedy survivor when possible and enabled.</summary>
    public double ExplorationFraction { get; }
    /// <summary>Gets nominal confidence; fresh final batches divide alpha across the maximum 64 candidates.</summary>
    public double Confidence { get; }
    /// <summary>Gets scalar optimization direction.</summary>
    public EvolutionOptimizationDirection Direction { get; }
    /// <summary>Gets the immutable plan identity.</summary>
    public string VersionHash { get; }
    internal EvolutionReplicationPlan Replication(EvolutionFidelityLevel level, bool confirmation) =>
        new(Replicates, Replicates, MinimumQuality, MaximumQuality, level.MaximumCostPerReplicate,
            confirmation ? 1 - (1 - Confidence) / 64 : Confidence, direction: Direction);
    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value).ToString("x16", CultureInfo.InvariantCulture);
}
