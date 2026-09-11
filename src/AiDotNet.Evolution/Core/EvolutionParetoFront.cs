namespace AiDotNet.Evolution;

/// <summary>Immutable feasible nondominated query result, not a scalar ranking.</summary>
public sealed class EvolutionParetoFront<TGenome>
{
    /// <summary>Filters up to 4096 entries into a stable nondominated union without capacity truncation.</summary>
    /// <remarks>One deterministic raw-objective representative is retained per equal objective box.</remarks>
    public EvolutionParetoFront(EvolutionParetoDefinition definition, IEnumerable<EvolutionArchiveEntry<TGenome>> entries)
    {
        Guard.NotNull(definition); Guard.NotNull(entries);
        Definition = definition;
        var copy = EvolutionCollection.CopyBounded(entries.Take(4097).ToArray(), 4096, nameof(entries));
        if (copy.Any(entry => entry is null || !definition.Accepts(entry.Evaluation)))
            throw new ArgumentException("A front may contain only completed feasible entries within the declared objective bounds.", nameof(entries));
        if (copy.Select(entry => entry.Evaluation.Direction).Distinct().Count() > 1)
            throw new ArgumentException("Front entries must share a scalar reporting direction.", nameof(entries));
        var front = new List<EvolutionArchiveEntry<TGenome>>();
        foreach (var entry in copy.OrderBy(entry => entry, Comparer<EvolutionArchiveEntry<TGenome>>.Create(definition.CompareObjectiveTie)))
        {
            if (front.Any(prior => definition.Dominates(prior.Evaluation.Objectives, entry.Evaluation.Objectives) ||
                definition.SameBox(prior.Evaluation, entry.Evaluation))) continue;
            front.RemoveAll(prior => definition.Dominates(entry.Evaluation.Objectives, prior.Evaluation.Objectives));
            front.Add(entry);
        }
        Entries = Array.AsReadOnly(front.ToArray());
        Representative = Entries.OrderBy(entry => entry,
            Comparer<EvolutionArchiveEntry<TGenome>>.Create(definition.CompareRepresentative)).FirstOrDefault();
    }

    /// <summary>Gets the immutable objective and reporting definition.</summary>
    public EvolutionParetoDefinition Definition { get; }
    /// <summary>Gets every retained tradeoff in deterministic objective order.</summary>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>> Entries { get; }
    /// <summary>Gets the explicitly lossy Best representative, or null for an empty front.</summary>
    public EvolutionArchiveEntry<TGenome>? Representative { get; }

    /// <summary>Filters deployable front members by inclusive bounds in original objective units.</summary>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>> Query(string objectiveName, double minimum, double maximum)
    {
        Guard.NotNullOrWhiteSpace(objectiveName);
        int index = -1;
        for (int i = 0; i < Definition.Objectives.Count; i++) if (Definition.Objectives[i].Name == objectiveName) index = i;
        if (index < 0) throw new ArgumentException("Unknown objective name.", nameof(objectiveName));
        if (!EvolutionDescriptorDefinition.IsFinite(minimum) || !EvolutionDescriptorDefinition.IsFinite(maximum) || minimum > maximum)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        return Array.AsReadOnly(Entries.Where(entry => entry.Evaluation.Objectives[index] >= minimum &&
            entry.Evaluation.Objectives[index] <= maximum).ToArray());
    }

    /// <summary>Computes exact two- or three-dimensional dominated volume in normalized loss coordinates.</summary>
    /// <remarks>
    /// The fixed reference point is (1,...,1), the worst bound of each declared objective. Raw measured objectives,
    /// not epsilon-box corners, determine volume. Higher is better. No automatic run-dependent normalization,
    /// extrapolation or approximate many-objective fallback is performed. The result measures retained solutions.
    /// </remarks>
    public double Hypervolume()
    {
        if (Definition.Objectives.Count > 3) throw new NotSupportedException("Exact hypervolume supports two or three objectives.");
        var points = Entries.Select(entry => Definition.Objectives.Select((axis, i) => axis.Normalize(entry.Evaluation.Objectives[i])).ToArray()).ToArray();
        return Volume(points, Definition.Objectives.Count);
    }

    private static double Volume(double[][] points, int dimensions)
    {
        if (points.Length == 0) return 0;
        if (dimensions == 1) return 1 - points.Min(point => point[0]);
        if (dimensions == 2)
        {
            double area = 0, priorX = 0, bestY = 1;
            foreach (var point in points.OrderBy(point => point[0]))
            {
                area += (point[0] - priorX) * (1 - bestY);
                bestY = Math.Min(bestY, point[1]); priorX = point[0];
            }
            return area + (1 - priorX) * (1 - bestY);
        }
        int axis = dimensions - 1;
        double[] cuts = points.Select(point => point[axis]).Append(1d).Distinct().OrderBy(value => value).ToArray();
        double volume = 0;
        for (int i = 0; i + 1 < cuts.Length; i++)
            volume += (cuts[i + 1] - cuts[i]) * Volume(points.Where(point => point[axis] <= cuts[i]).ToArray(), dimensions - 1);
        return volume;
    }

    internal static EvolutionArchiveEntry<TGenome>[] DiverseOrder(EvolutionParetoDefinition definition,
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries)
    {
        var distances = new double[entries.Count];
        for (int axis = 0; axis < definition.Objectives.Count; axis++)
        {
            int dimension = axis;
            int[] order = Enumerable.Range(0, entries.Count).OrderBy(i => entries[i].Evaluation.Objectives[dimension])
                .ThenBy(i => entries[i].Evaluation.GenomeId, StringComparer.Ordinal).ToArray();
            if (order.Length < 2) continue;
            double low = entries[order[0]].Evaluation.Objectives[axis], high = entries[order[order.Length - 1]].Evaluation.Objectives[axis];
            if (low == high) continue;
            distances[order[0]] = distances[order[order.Length - 1]] = double.PositiveInfinity;
            for (int i = 1; i + 1 < order.Length; i++)
                distances[order[i]] += (entries[order[i + 1]].Evaluation.Objectives[axis] - entries[order[i - 1]].Evaluation.Objectives[axis]) /
                    (definition.Objectives[axis].Maximum - definition.Objectives[axis].Minimum);
        }
        return Enumerable.Range(0, entries.Count).OrderByDescending(i => distances[i])
            .ThenBy(i => entries[i].Evaluation.GenomeId, StringComparer.Ordinal).ThenBy(i => entries[i].Evaluation.EvaluationId)
            .Select(i => entries[i]).ToArray();
    }
}
