namespace AiDotNet.Evolution;

/// <summary>Fixed two- or three-objective feasible-front contract, including constraint shape and capacity.</summary>
/// <remarks>Hard constraints require exactly ConstraintCount reported violations, all zero. Infeasible exploration
/// is deliberately not retained in this deployable archive. Best remains a scalar-quality representative only.
/// Capacity pruning uses normalized crowding with stable identity ties; discarded points are not remembered.</remarks>
public sealed class EvolutionParetoDefinition
{
    /// <summary>Creates a front contract. Capacity is 2..1024, constraint count 0..64.</summary>
    public EvolutionParetoDefinition(IEnumerable<EvolutionObjectiveDefinition> objectives, int capacity = 64, int constraintCount = 0)
    {
        Guard.NotNull(objectives);
        var axes = EvolutionCollection.ToBoundedArray(objectives, 3, nameof(objectives));
        if (axes.Length < 2 || axes.Any(axis => axis is null) || axes.Select(axis => axis.Name).Distinct(StringComparer.Ordinal).Count() != axes.Length)
            throw new ArgumentException("Two or three uniquely named objectives are required.", nameof(objectives));
        if (capacity < 2 || capacity > 1024) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (constraintCount < 0 || constraintCount > 64) throw new ArgumentOutOfRangeException(nameof(constraintCount));
        Objectives = Array.AsReadOnly(axes); Capacity = capacity; ConstraintCount = constraintCount;
        DefinitionHash = EvolutionHash.Combine(new[] { "pareto-crowding-v1", capacity.ToString(System.Globalization.CultureInfo.InvariantCulture),
            constraintCount.ToString(System.Globalization.CultureInfo.InvariantCulture), "scalar-quality-representative", "unit-reference" }.Concat(axes.Select(axis => axis.Canonical)));
    }
    /// <summary>Gets the ordered axes matching evaluation objective vectors.</summary>
    public IReadOnlyList<EvolutionObjectiveDefinition> Objectives { get; }
    /// <summary>Gets the retained-front capacity.</summary>
    public int Capacity { get; }
    /// <summary>Gets the exact number of hard constraint violations required.</summary>
    public int ConstraintCount { get; }
    /// <summary>Gets the stable contract hash.</summary>
    public string DefinitionHash { get; }
    /// <summary>Checks completion, objective domains and complete zero-valued hard constraints.</summary>
    public bool IsFeasible(EvolutionEvaluation evaluation)
    {
        Guard.NotNull(evaluation);
        return evaluation.Status == EvolutionEvaluationStatus.Completed && evaluation.Quality.HasValue &&
            evaluation.Objectives.Count == Objectives.Count && evaluation.ConstraintViolations.Count == ConstraintCount &&
            evaluation.ConstraintViolations.All(value => value == 0) &&
            Objectives.Select((axis, i) => evaluation.Objectives[i] >= axis.Minimum && evaluation.Objectives[i] <= axis.Maximum).All(valid => valid);
    }
    /// <summary>Compares feasible vectors: -1 dominates, 1 is dominated, 0 is equivalent or incomparable.</summary>
    public int Compare(EvolutionEvaluation left, EvolutionEvaluation right)
    {
        if (!IsFeasible(left) || !IsFeasible(right)) throw new ArgumentException("Dominance requires feasible objective vectors.");
        bool less = false, greater = false;
        for (int i = 0; i < Objectives.Count; i++)
        {
            int comparison = Objectives[i].ComparisonValue(left.Objectives[i]).CompareTo(Objectives[i].ComparisonValue(right.Objectives[i]));
            less |= comparison < 0; greater |= comparison > 0;
        }
        return less == greater ? 0 : less ? -1 : 1;
    }
    internal bool Equivalent(EvolutionEvaluation left, EvolutionEvaluation right) =>
        Objectives.Select((axis, i) => axis.ComparisonValue(left.Objectives[i]) == axis.ComparisonValue(right.Objectives[i])).All(equal => equal);
}
