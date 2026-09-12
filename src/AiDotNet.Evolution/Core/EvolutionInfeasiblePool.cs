namespace AiDotNet.Evolution;

// A separate storage pool, never an IEvolutionArchiveView: its entries cannot masquerade as deployable elites.
internal sealed class EvolutionInfeasiblePool<TGenome>
{
    private readonly EvolutionParetoDefinition _definition;
    private readonly EvolutionOptimizationDirection _direction;
    private readonly SortedDictionary<int, EvolutionArchiveEntry<TGenome>> _slots = new();
    private IReadOnlyList<EvolutionArchiveEntry<TGenome>>? _entries;

    internal EvolutionInfeasiblePool(EvolutionParetoDefinition definition, EvolutionOptimizationDirection direction)
    { _definition = definition; _direction = direction; }

    internal IReadOnlyList<EvolutionArchiveEntry<TGenome>> Entries => _entries ??= Array.AsReadOnly(_slots.Values.ToArray());

    internal bool TryAdd(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation, Action beforeCommit)
    {
        var proposed = new EvolutionArchiveEntry<TGenome>(new EvolutionCellKey(new[] { _definition.Capacity }), candidate, evaluation);
        var retained = Entries.Concat(new[] { proposed }).OrderBy(entry => entry,
            Comparer<EvolutionArchiveEntry<TGenome>>.Create(Compare)).Take(_definition.InfeasibleCapacity).ToArray();
        if (!retained.Contains(proposed)) return false;
        var removed = Entries.Where(entry => !retained.Contains(entry)).ToArray();
        int slot = Enumerable.Range(_definition.Capacity, _definition.InfeasibleCapacity)
            .First(index => !_slots.ContainsKey(index) || removed.Any(entry => entry.Cell.Bins[0] == index));
        beforeCommit(); // version overflow must be detected before either pool or archive changes
        foreach (var entry in removed) _slots.Remove(entry.Cell.Bins[0]);
        _slots.Add(slot, new EvolutionArchiveEntry<TGenome>(new EvolutionCellKey(new[] { slot }), candidate, evaluation));
        _entries = null;
        return true;
    }

    // The task must express violation magnitudes on a comparable scale. No scalar quality rewards infeasibility.
    private static int Compare(EvolutionArchiveEntry<TGenome> a, EvolutionArchiveEntry<TGenome> b)
    {
        var left = a.Evaluation.ConstraintViolations; var right = b.Evaluation.ConstraintViolations;
        int comparison = left.Max().CompareTo(right.Max());
        if (comparison != 0) return comparison;
        comparison = left.Count(value => value > 0).CompareTo(right.Count(value => value > 0));
        if (comparison != 0) return comparison;
        for (int i = 0; i < Math.Min(left.Count, right.Count); i++)
        {
            comparison = left[i].CompareTo(right[i]);
            if (comparison != 0) return comparison;
        }
        comparison = left.Count.CompareTo(right.Count);
        return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Evaluation.GenomeId, b.Evaluation.GenomeId);
    }

    internal void Restore(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries)
    {
        var copy = EvolutionCollection.CopyBounded(entries, _definition.InfeasibleCapacity, nameof(entries));
        if (copy.Any(entry => entry is null || !_definition.HasValidObjectives(entry.Evaluation) ||
            !entry.Evaluation.ConstraintViolations.Any(value => value > 0) || entry.Evaluation.Direction != _direction ||
            entry.Candidate.EvaluationId != entry.Evaluation.EvaluationId || entry.Candidate.CanonicalGenome.Id != entry.Evaluation.GenomeId ||
            entry.Cell.Bins.Count != 1 || entry.Cell.Bins[0] < _definition.Capacity ||
            entry.Cell.Bins[0] >= _definition.Capacity + _definition.InfeasibleCapacity) ||
            copy.Select(entry => entry.Cell.StableKey).Distinct().Count() != copy.Length ||
            copy.Select(entry => entry.Evaluation.GenomeId).Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            copy.Select(entry => entry.Evaluation.EvaluationId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Invalid infeasible exploration pool.", nameof(entries));
        if (_slots.Count != 0) throw new InvalidOperationException("Exploration restore requires an empty pool.");
        foreach (var entry in copy) _slots.Add(entry.Cell.Bins[0], entry);
        _entries = null;
    }
}
