using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Immutable bounded-front semantics, including objective order, capacity and representative policy.</summary>
public sealed class EvolutionParetoDefinition
{
    /// <summary>Creates a two-to-eight-objective front with capacity between two and 256.</summary>
    public EvolutionParetoDefinition(IEnumerable<EvolutionObjectiveDefinition> objectives, int capacity = 64,
        EvolutionParetoRepresentative representative = EvolutionParetoRepresentative.ClosestToIdeal, int infeasibleCapacity = 0)
    {
        Guard.NotNull(objectives);
        var copy = EvolutionCollection.CopyBounded(objectives.Take(9).ToArray(), 8, nameof(objectives));
        if (copy.Length < 2 || copy.Any(axis => axis is null) ||
            copy.Select(axis => axis.Name).Distinct(StringComparer.Ordinal).Count() != copy.Length)
            throw new ArgumentException("Provide two to eight uniquely named objective definitions.", nameof(objectives));
        if (capacity < 2 || capacity > 256) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (infeasibleCapacity < 0 || infeasibleCapacity > 256) throw new ArgumentOutOfRangeException(nameof(infeasibleCapacity));
        if (!Enum.IsDefined(typeof(EvolutionParetoRepresentative), representative)) throw new ArgumentOutOfRangeException(nameof(representative));
        Objectives = Array.AsReadOnly(copy); Capacity = capacity; Representative = representative; InfeasibleCapacity = infeasibleCapacity;
        DefinitionHash = EvolutionHash.Combine(new[] { "pareto-epsilon-box-crowding-v1",
            capacity.ToString(CultureInfo.InvariantCulture), representative.ToString() }.Concat(copy.Select(axis => axis.Canonical)));
        if (infeasibleCapacity > 0) DefinitionHash = EvolutionHash.Combine(new[] { DefinitionHash, "infeasible-max-violation-v1",
            infeasibleCapacity.ToString(CultureInfo.InvariantCulture) });
    }

    /// <summary>Gets definitions in exactly the order of EvolutionEvaluation.Objectives.</summary>
    public IReadOnlyList<EvolutionObjectiveDefinition> Objectives { get; }
    /// <summary>Gets the maximum number of retained feasible front members per island.</summary>
    public int Capacity { get; }
    /// <summary>Gets the separate infeasible exploration capacity; zero disables it without changing feasible-only semantics.</summary>
    public int InfeasibleCapacity { get; }
    /// <summary>Gets the explicit policy for Best; the complete answer is the front, not Best.</summary>
    public EvolutionParetoRepresentative Representative { get; }
    /// <summary>Gets the versioned identity of every admission, retention and reporting choice.</summary>
    public string DefinitionHash { get; }

    /// <summary>Reports whether a completed feasible evaluation has the exact objective shape and valid bounds.</summary>
    public bool Accepts(EvolutionEvaluation evaluation)
    {
        Guard.NotNull(evaluation);
        return HasValidObjectives(evaluation) && !evaluation.ConstraintViolations.Any(value => value > 0);
    }

    internal bool HasValidObjectives(EvolutionEvaluation evaluation) => DescribeObjectiveProblem(evaluation, out _) is null;

    /// <summary>Names the first reason an evaluation cannot enter the front, or null when the vector is admissible.</summary>
    /// <remarks>
    /// Feasibility is deliberately not a problem: a positive constraint violation is a reported outcome, while an
    /// unusable objective vector is a contract mistake the engine must surface instead of dropping silently.
    /// </remarks>
    internal string? DescribeObjectiveProblem(EvolutionEvaluation evaluation, out int index)
    {
        index = -1;
        if (evaluation.Status != EvolutionEvaluationStatus.Completed) return "not_completed";
        if (!evaluation.Quality.HasValue) return "missing_quality";
        if (evaluation.Objectives.Count != Objectives.Count) return "objective_count";
        for (int i = 0; i < Objectives.Count; i++)
        {
            if (!EvolutionDescriptorDefinition.IsFinite(evaluation.Objectives[i])) { index = i; return "not_finite"; }
            if (evaluation.Objectives[i] < Objectives[i].Minimum || evaluation.Objectives[i] > Objectives[i].Maximum)
            { index = i; return "out_of_bounds"; }
        }
        return null;
    }

    /// <summary>Tests strict Pareto dominance under the fixed exact or epsilon-box objective order.</summary>
    public bool Dominates(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        Validate(left); Validate(right);
        bool better = false;
        for (int i = 0; i < Objectives.Count; i++)
        {
            double a = Objectives[i].CompareValue(left[i]), b = Objectives[i].CompareValue(right[i]);
            if (a > b) return false;
            if (a < b) better = true;
        }
        return better;
    }

    internal void Validate(IReadOnlyList<double> values)
    {
        Guard.NotNull(values);
        if (values.Count != Objectives.Count) throw new ArgumentException("Objective vector has the wrong dimension.", nameof(values));
        for (int i = 0; i < values.Count; i++) Objectives[i].Normalize(values[i]);
    }

    internal bool SameBox(EvolutionEvaluation a, EvolutionEvaluation b) => Objectives.Select((axis, i) =>
        axis.CompareValue(a.Objectives[i]) == axis.CompareValue(b.Objectives[i])).All(same => same);

    internal int CompareRepresentative<T>(EvolutionArchiveEntry<T> a, EvolutionArchiveEntry<T> b)
    {
        int comparison = 0;
        if (Representative == EvolutionParetoRepresentative.ScalarQuality)
            comparison = a.Evaluation.Direction == EvolutionOptimizationDirection.Maximize
                ? Nullable.Compare(b.Evaluation.Quality, a.Evaluation.Quality) : Nullable.Compare(a.Evaluation.Quality, b.Evaluation.Quality);
        else if (Representative == EvolutionParetoRepresentative.ClosestToIdeal)
            comparison = IdealDistance(a.Evaluation).CompareTo(IdealDistance(b.Evaluation));
        return comparison != 0 ? comparison : CompareObjectiveTie(a, b);
    }

    internal int CompareObjectiveTie<T>(EvolutionArchiveEntry<T> a, EvolutionArchiveEntry<T> b)
    {
        for (int i = 0; i < Objectives.Count; i++)
        {
            int comparison = Objectives[i].Normalize(a.Evaluation.Objectives[i]).CompareTo(Objectives[i].Normalize(b.Evaluation.Objectives[i]));
            if (comparison != 0) return comparison;
        }
        int identity = StringComparer.Ordinal.Compare(a.Evaluation.GenomeId, b.Evaluation.GenomeId);
        return identity != 0 ? identity : a.Evaluation.EvaluationId.CompareTo(b.Evaluation.EvaluationId);
    }

    private double IdealDistance(EvolutionEvaluation evaluation) => Objectives.Select((axis, i) =>
    {
        double value = axis.Normalize(evaluation.Objectives[i]); return value * value;
    }).Sum();
}
