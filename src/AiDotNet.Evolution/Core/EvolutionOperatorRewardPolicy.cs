namespace AiDotNet.Evolution;

/// <summary>The valid gain credited to a freshly evaluated archive-changing proposal.</summary>
public enum EvolutionOperatorRewardKind
{
    /// <summary>One bounded unit for an insertion, replacement or insertion with eviction.</summary>
    ArchiveSuccess,
    /// <summary>Direction-aware improvement over the proposal-time parent, normalized by a declared fixed quality scale.</summary>
    ParentImprovement
}

/// <summary>Which non-overlapping charges normalize an operator's valid gain.</summary>
public enum EvolutionOperatorCostBasis
{
    /// <summary>Terminal evaluator costs, including reported retry and screening costs.</summary>
    Evaluation,
    /// <summary>Proposal backend receipt plus terminal evaluator costs; other consumer stages need explicit inclusion.</summary>
    ProposalAndEvaluation
}

/// <summary>Immutable opt-in marginal-gain and cost semantics for an adaptive variation portfolio.</summary>
/// <remarks>Reward is gain / max(1, chargedCost / minimumCostUnits), bounded to [0,1]. Parent gain is
/// clamp(direction-aware child-minus-parent / qualityScale, 0, 1), only for fresh feasible archive-changing outcomes.
/// This is parent-relative scalar gain, not global hypervolume or uncertainty-corrected improvement. Preserve the
/// declared quality/cost scales across operators; do not infer them from observed winners.</remarks>
public sealed class EvolutionOperatorRewardPolicy
{
    /// <summary>Creates a fixed reward policy; costUnitVersionHash must describe the evaluator and proposal units.</summary>
    public EvolutionOperatorRewardPolicy(EvolutionOperatorRewardKind kind, EvolutionOperatorCostBasis costBasis,
        string costUnitVersionHash, double qualityScale = 1, double minimumCostUnits = 1)
    {
        Guard.NotNullOrWhiteSpace(costUnitVersionHash);
        if (costUnitVersionHash.Length > 256 || costUnitVersionHash.Any(char.IsControl)) throw new ArgumentException("Bounded printable cost identity required.", nameof(costUnitVersionHash));
        if (!Enum.IsDefined(typeof(EvolutionOperatorRewardKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Enum.IsDefined(typeof(EvolutionOperatorCostBasis), costBasis)) throw new ArgumentOutOfRangeException(nameof(costBasis));
        if (!EvolutionDescriptorDefinition.IsFinite(qualityScale) || qualityScale <= 0) throw new ArgumentOutOfRangeException(nameof(qualityScale));
        if (!EvolutionDescriptorDefinition.IsFinite(minimumCostUnits) || minimumCostUnits <= 0) throw new ArgumentOutOfRangeException(nameof(minimumCostUnits));
        Kind = kind; CostBasis = costBasis; CostUnitVersionHash = costUnitVersionHash; QualityScale = qualityScale; MinimumCostUnits = minimumCostUnits;
        VersionHash = EvolutionHash.Combine(new[] { "operator-reward-v2-measurement-origin", kind.ToString(), costBasis.ToString(), costUnitVersionHash,
            EvolutionHash.EncodeDouble(qualityScale), EvolutionHash.EncodeDouble(minimumCostUnits) });
    }
    /// <summary>Gets archive-success or parent-relative scalar improvement semantics.</summary>
    public EvolutionOperatorRewardKind Kind { get; }
    /// <summary>Gets the non-overlapping cost scope used for credit.</summary>
    public EvolutionOperatorCostBasis CostBasis { get; }
    /// <summary>Gets the declared deterministic cost conversion/measurement identity.</summary>
    public string CostUnitVersionHash { get; }
    /// <summary>Gets the fixed positive quality gain that earns one unit before cost normalization.</summary>
    public double QualityScale { get; }
    /// <summary>Gets the fixed positive unit cost below which gain is not amplified.</summary>
    public double MinimumCostUnits { get; }
    /// <summary>Gets the immutable reward semantics fingerprint.</summary>
    public string VersionHash { get; }

    internal double Reward(double? parentQuality, EvolutionOptimizationDirection parentDirection, EvolutionEvaluation evaluation,
        EvolutionArchiveInsertionResult? insertion, EvolutionProposalCost? proposalCost)
    {
        bool changed = insertion is EvolutionArchiveInsertionResult.Inserted or EvolutionArchiveInsertionResult.Replaced or EvolutionArchiveInsertionResult.InsertedWithEviction;
        if (!changed || evaluation.Status != EvolutionEvaluationStatus.Completed || evaluation.IsMeasurementReuse ||
            evaluation.Cost.AttemptCount == 0 || !evaluation.Quality.HasValue || evaluation.ConstraintViolations.Any(value => value > 0)) return 0;
        if (proposalCost is not null && (proposalCost.Outcome != EvolutionResourceOutcome.Completed || proposalCost.ExceededMaximum)) return 0;
        if (evaluation.Diagnostics.Any(diagnostic => diagnostic.Code is "resource_cost_unknown" or "resource_cost_unrepresentable" or "resource_maximum_exceeded")) return 0;
        double gain = 1;
        if (Kind == EvolutionOperatorRewardKind.ParentImprovement)
        {
            if (!parentQuality.HasValue || parentDirection != evaluation.Direction) return 0;
            double difference = evaluation.Direction == EvolutionOptimizationDirection.Maximize
                ? evaluation.Quality.Value - parentQuality.Value : parentQuality.Value - evaluation.Quality.Value;
            // A positive overflowing difference exceeds every finite declared scale; negative differences earn nothing.
            gain = Math.Max(0, Math.Min(1, difference / QualityScale));
        }
        double cost = evaluation.Cost.CostUnits;
        if (CostBasis == EvolutionOperatorCostBasis.ProposalAndEvaluation)
            cost += (double)(proposalCost ?? throw new InvalidOperationException("Complete proposal costs are required.")).Charged["cost_units"];
        return gain / Math.Max(1, cost / MinimumCostUnits);
    }
}
