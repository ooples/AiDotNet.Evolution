namespace AiDotNet.Evolution;

/// <summary>Immutable feasible nondominated query result, not a scalar ranking.</summary>
public sealed class EvolutionParetoFront<TGenome>
{
    private double[][]? _normalized;
    private double? _hypervolume;

    /// <summary>Filters up to 4096 entries into a stable nondominated union without capacity truncation.</summary>
    /// <remarks>
    /// One deterministic raw-objective representative is retained per equal objective box. Each member's comparison
    /// coordinates are computed once, so a pairwise dominance or box test is an array compare rather than a repeated
    /// revalidation and renormalization of both objective vectors.
    /// </remarks>
    public EvolutionParetoFront(EvolutionParetoDefinition definition, IEnumerable<EvolutionArchiveEntry<TGenome>> entries)
    {
        Guard.NotNull(definition); Guard.NotNull(entries);
        Definition = definition;
        var copy = EvolutionCollection.CopyBounded(entries.Take(4097).ToArray(), 4096, nameof(entries));
        if (copy.Any(entry => entry is null || !definition.Accepts(entry.Evaluation)))
            throw new ArgumentException("A front may contain only completed feasible entries within the declared objective bounds.", nameof(entries));
        if (copy.Select(entry => entry.Evaluation.Direction).Distinct().Count() > 1)
            throw new ArgumentException("Front entries must share a scalar reporting direction.", nameof(entries));
        var ordered = copy.OrderBy(entry => entry, Comparer<EvolutionArchiveEntry<TGenome>>.Create(definition.CompareObjectiveTie)).ToArray();
        var boxes = new double[ordered.Length][];
        for (int i = 0; i < ordered.Length; i++) boxes[i] = definition.CompareVector(ordered[i].Evaluation.Objectives);
        var front = new List<EvolutionArchiveEntry<TGenome>>();
        var frontBoxes = new List<double[]>();
        for (int i = 0; i < ordered.Length; i++)
        {
            bool covered = false;
            for (int j = 0; j < frontBoxes.Count; j++)
            {
                if (EvolutionParetoDefinition.DominatesCompared(frontBoxes[j], boxes[i]) ||
                    EvolutionParetoDefinition.SameCompared(frontBoxes[j], boxes[i])) { covered = true; break; }
            }
            if (covered) continue;
            for (int j = frontBoxes.Count - 1; j >= 0; j--)
            {
                if (!EvolutionParetoDefinition.DominatesCompared(boxes[i], frontBoxes[j])) continue;
                front.RemoveAt(j); frontBoxes.RemoveAt(j);
            }
            front.Add(ordered[i]); frontBoxes.Add(boxes[i]);
        }
        Entries = Array.AsReadOnly(front.ToArray());
        EvolutionArchiveEntry<TGenome>? representative = null;
        foreach (EvolutionArchiveEntry<TGenome> entry in Entries)
            if (representative is null || definition.CompareRepresentative(entry, representative) < 0) representative = entry;
        Representative = representative;
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
    /// The front is immutable, so the normalized points and the volume are computed once and then reused.
    /// </remarks>
    public double Hypervolume()
    {
        if (Definition.Objectives.Count > 3) throw new NotSupportedException("Exact hypervolume supports two or three objectives.");
        if (_hypervolume.HasValue) return _hypervolume.Value;
        if (_normalized is null)
        {
            var points = new double[Entries.Count][];
            for (int i = 0; i < points.Length; i++)
                points[i] = Definition.NormalizedVector(Entries[i].Evaluation.Objectives);
            _normalized = points;
        }
        double volume = Volume(_normalized, Definition.Objectives.Count);
        _hypervolume = volume;
        return volume;
    }

    private static double Volume(double[][] points, int dimensions)
    {
        if (points.Length == 0) return 0;
        if (dimensions == 2)
        {
            var sorted = (double[][])points.Clone();
            Array.Sort(sorted, (left, right) => left[0].CompareTo(right[0]));
            return Area(sorted, sorted.Length);
        }

        // Three objectives: one ascending sweep of the last axis. Every cut only adds the points that entered the
        // slice to a list kept sorted on the first axis, so no slice refilters and re-sorts the whole front.
        var byDepth = (double[][])points.Clone();
        Array.Sort(byDepth, (left, right) => left[2].CompareTo(right[2]));
        var cuts = new List<double>(byDepth.Length + 1);
        foreach (double[] point in byDepth)
            if (cuts.Count == 0 || cuts[cuts.Count - 1] != point[2]) cuts.Add(point[2]);
        if (cuts.Count == 0 || cuts[cuts.Count - 1] != 1) cuts.Add(1);
        var active = new double[byDepth.Length][];
        int count = 0, next = 0;
        double volume = 0;
        for (int i = 0; i + 1 < cuts.Count; i++)
        {
            while (next < byDepth.Length && byDepth[next][2] <= cuts[i]) InsertByFirstAxis(active, count++, byDepth[next++]);
            volume += (cuts[i + 1] - cuts[i]) * Area(active, count);
        }
        return volume;
    }

    /// <summary>Accumulates the staircase area of points already ascending on the first axis.</summary>
    private static double Area(double[][] points, int count)
    {
        double area = 0, priorX = 0, bestY = 1;
        for (int i = 0; i < count; i++)
        {
            area += (points[i][0] - priorX) * (1 - bestY);
            if (points[i][1] < bestY) bestY = points[i][1];
            priorX = points[i][0];
        }
        return area + (1 - priorX) * (1 - bestY);
    }

    private static void InsertByFirstAxis(double[][] items, int count, double[] point)
    {
        int index = count;
        while (index > 0 && items[index - 1][0] > point[0]) { items[index] = items[index - 1]; index--; }
        items[index] = point;
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
