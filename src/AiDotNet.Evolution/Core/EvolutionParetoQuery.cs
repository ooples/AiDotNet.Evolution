namespace AiDotNet.Evolution;

/// <summary>Front queries, deterministic crowding and exact two-/three-dimensional normalized hypervolume.</summary>
public static class EvolutionParetoQuery
{
    /// <summary>Gets objective semantics, or null for a scalar archive.</summary>
    public static EvolutionParetoDefinition? GetParetoDefinition<TGenome>(this IEvolutionArchiveView<TGenome> archive)
    {
        Guard.NotNull(archive);
        return (archive as IEvolutionObjectiveArchiveView)?.ParetoDefinition;
    }

    /// <summary>Gets the retained feasible front; refuses scalar archives instead of guessing objective semantics.</summary>
    public static IReadOnlyList<EvolutionArchiveEntry<TGenome>> ParetoFront<TGenome>(this IEvolutionArchiveView<TGenome> archive)
    {
        if (archive.GetParetoDefinition() is null) throw new ArgumentException("A Pareto archive is required.", nameof(archive));
        return archive.Entries;
    }

    /// <summary>Merges retained island fronts in stable identity order with the same bounded capacity policy.</summary>
    /// <remarks>This is a bounded union of retained fronts, not a reconstruction of discarded historical candidates.</remarks>
    public static IReadOnlyList<EvolutionArchiveEntry<TGenome>> ParetoFront<TGenome>(this EvolutionRunResult<TGenome> result)
    {
        Guard.NotNull(result);
        if (result.Islands.Count == 0) return Array.Empty<EvolutionArchiveEntry<TGenome>>();
        var first = result.Islands[0];
        var definition = first.GetParetoDefinition() ?? throw new ArgumentException("A Pareto run is required.", nameof(result));
        var merged = new ParetoArchive<TGenome>(definition, first.Direction);
        if (result.Islands.Any(island => island.DefinitionHash != first.DefinitionHash || island.GetParetoDefinition()?.DefinitionHash != definition.DefinitionHash))
            throw new ArgumentException("Island front definitions differ.", nameof(result));
        foreach (var entry in result.Islands.SelectMany(island => island.Entries).OrderBy(entry => entry.Evaluation.GenomeId, StringComparer.Ordinal)
            .ThenBy(entry => entry.Evaluation.EvaluationId)) merged.TryAdd(entry.Candidate, entry.Evaluation);
        return merged.Entries;
    }

    /// <summary>Returns the best retained feasible candidate for a declared objective, with ordinal identity ties.</summary>
    public static EvolutionArchiveEntry<TGenome>? BestObjective<TGenome>(this IEvolutionArchiveView<TGenome> archive, string name)
    {
        var definition = archive.GetParetoDefinition() ?? throw new ArgumentException("A Pareto archive is required.", nameof(archive));
        int index = definition.Objectives.ToList().FindIndex(axis => axis.Name == name);
        if (index < 0) throw new ArgumentException("Unknown objective.", nameof(name));
        return archive.Entries.OrderBy(entry => definition.Objectives[index].Normalize(entry.Evaluation.Objectives[index]))
            .ThenBy(entry => entry.Evaluation.GenomeId, StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>Measures the union of feasible dominated boxes relative to the fixed worst corner (1,...,1).</summary>
    /// <remarks>Result is in [0,1]. Uses raw normalized objectives, not tolerance bins or candidate-derived bounds.
    /// Dominated and duplicate input points do not inflate volume. Infeasible evaluations contribute nothing.</remarks>
    public static double Hypervolume(EvolutionParetoDefinition definition, IEnumerable<EvolutionEvaluation> evaluations)
    {
        Guard.NotNull(definition); Guard.NotNull(evaluations);
        var values = EvolutionCollection.ToBoundedArray(evaluations, 4096, nameof(evaluations));
        if (values.Any(value => value is null)) throw new ArgumentException("Evaluations cannot contain null.", nameof(evaluations));
        var points = values.Where(definition.IsFeasible)
            .Select(value => definition.Objectives.Select((axis, i) => axis.Normalize(value.Objectives[i])).ToArray()).ToArray();
        return Volume(points, definition.Objectives.Count);
    }

    /// <summary>Measures the retained front using its declared fixed normalization.</summary>
    public static double Hypervolume<TGenome>(this IEvolutionArchiveView<TGenome> archive) => Hypervolume(
        archive.GetParetoDefinition() ?? throw new ArgumentException("A Pareto archive is required.", nameof(archive)),
        archive.Entries.Select(entry => entry.Evaluation));

    private static double Volume(double[][] points, int dimensions)
    {
        if (points.Length == 0) return 0;
        if (dimensions == 1) return 1 - points.Min(point => point[0]);
        if (dimensions == 2)
        {
            var sorted = points.OrderBy(point => point[0]).ToArray();
            double area = 0, bestY = 1;
            for (int i = 0; i < sorted.Length; i++)
            {
                bestY = Math.Min(bestY, sorted[i][1]);
                double nextX = i + 1 == sorted.Length ? 1 : sorted[i + 1][0];
                area += (nextX - sorted[i][0]) * (1 - bestY);
            }
            return area;
        }
        int axis = dimensions - 1;
        var levels = points.Select(point => point[axis]).Append(1.0).Distinct().OrderBy(value => value).ToArray();
        double result = 0;
        for (int i = 0; i < levels.Length - 1; i++)
            result += (levels[i + 1] - levels[i]) * Volume(points.Where(point => point[axis] <= levels[i]).ToArray(), dimensions - 1);
        return Math.Max(0, Math.Min(1, result));
    }

    internal static IReadOnlyList<EvolutionArchiveEntry<TGenome>> CrowdingOrder<TGenome>(
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries, EvolutionParetoDefinition definition)
    {
        var distance = entries.ToDictionary(entry => entry.Evaluation.GenomeId, _ => 0.0, StringComparer.Ordinal);
        for (int axis = 0; axis < definition.Objectives.Count && entries.Count > 0; axis++)
        {
            int index = axis;
            var sorted = entries.OrderBy(entry => definition.Objectives[index].Normalize(entry.Evaluation.Objectives[index]))
                .ThenBy(entry => entry.Evaluation.GenomeId, StringComparer.Ordinal).ToArray();
            double low = definition.Objectives[index].Normalize(sorted[0].Evaluation.Objectives[index]);
            double high = definition.Objectives[index].Normalize(sorted[sorted.Length - 1].Evaluation.Objectives[index]);
            if (low == high) continue;
            distance[sorted[0].Evaluation.GenomeId] = double.PositiveInfinity;
            distance[sorted[sorted.Length - 1].Evaluation.GenomeId] = double.PositiveInfinity;
            for (int i = 1; i < sorted.Length - 1; i++)
                distance[sorted[i].Evaluation.GenomeId] += definition.Objectives[index].Normalize(sorted[i + 1].Evaluation.Objectives[index]) -
                    definition.Objectives[index].Normalize(sorted[i - 1].Evaluation.Objectives[index]);
        }
        return entries.OrderByDescending(entry => distance[entry.Evaluation.GenomeId])
            .ThenBy(entry => entry.Evaluation.GenomeId, StringComparer.Ordinal).ToArray();
    }
}
