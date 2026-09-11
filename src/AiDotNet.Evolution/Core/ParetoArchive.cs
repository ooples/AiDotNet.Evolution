namespace AiDotNet.Evolution;

/// <summary>Opt-in feasible nondominated archive with deterministic crowded-front capacity control.</summary>
/// <remarks>
/// Epsilon-box dominance follows the immutable definition; equal boxes retain the lexicographically best raw
/// objective vector. Capacity overflow removes the least crowded member, with ordinal identity tie-breaking.
/// Pruned historical candidates cannot be recovered: nondominance is guaranteed for the retained set, not the
/// entire evaluation history. Infeasible candidates are rejected unless the separate exploration pool is enabled;
/// they never enter Entries, Best, Front, Sample, or Get. The engine samples an enabled nonempty pool with 10%
/// probability, or always while no feasible parent exists. Violation magnitudes should use comparable task-defined scales.
/// Cells are stable storage slots, not behavior-grid bins. Best is only the configured front representative.
/// Insertion tests the proposed vector against the current members alone, using comparison coordinates cached per
/// version, so admission costs O(members) comparisons rather than an O(members squared) rebuild of the whole front.
/// </remarks>
public sealed class ParetoArchive<TGenome> : ICheckpointableParetoArchive<TGenome>, IEvolutionArchiveCellCount,
    IEvolutionParetoArchiveView<TGenome>, IEvolutionArchiveMutationSource<TGenome>
{
    private readonly SortedDictionary<int, EvolutionArchiveEntry<TGenome>> _slots = new();
    private IReadOnlyList<EvolutionArchiveEntry<TGenome>>? _entries;
    private double[][]? _entryBoxes;
    private EvolutionParetoFront<TGenome>? _front;
    private long _frontVersion = -1;
    private readonly EvolutionInfeasiblePool<TGenome>? _exploration;

    /// <summary>Creates an empty front; scalar direction affects only reporting and an explicitly chosen scalar representative.</summary>
    public ParetoArchive(EvolutionParetoDefinition definition,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(definition);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ParetoDefinition = definition; Direction = direction;
        if (definition.InfeasibleCapacity > 0) _exploration = new EvolutionInfeasiblePool<TGenome>(definition, direction);
        DefinitionHash = EvolutionHash.Combine(new[] { definition.DefinitionHash, direction.ToString() });
        Descriptors = Array.AsReadOnly(new[] { new EvolutionDescriptorDefinition("__pareto_slot", 0, definition.Capacity, definition.Capacity) });
    }

    /// <inheritdoc/>
    public EvolutionParetoDefinition ParetoDefinition { get; }
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>>? InfeasibleEntries => _exploration?.Entries;
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors { get; }
    /// <inheritdoc/>
    public string DefinitionHash { get; }
    /// <inheritdoc/>
    public EvolutionOptimizationDirection Direction { get; }
    /// <inheritdoc/>
    public long TotalCells => ParetoDefinition.Capacity;
    /// <inheritdoc/>
    public int Count => _slots.Count;
    /// <inheritdoc/>
    public long Version { get; private set; }
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>> Entries => _entries ??= Array.AsReadOnly(_slots.Values.ToArray());
    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Best { get; private set; }

    /// <summary>Gets a stable front query, including raw-objective hypervolume and deployment filters.</summary>
    /// <remarks>The front is rebuilt only when <see cref="Version"/> changes, so repeated reporting reuses one view.</remarks>
    public EvolutionParetoFront<TGenome> Front
    {
        get
        {
            if (_front is null || _frontVersion != Version)
            {
                _front = new EvolutionParetoFront<TGenome>(ParetoDefinition, Entries);
                _frontVersion = Version;
            }
            return _front;
        }
    }

    /// <summary>Gets the comparison coordinates of every stored member, in <see cref="Entries"/> order.</summary>
    private double[][] EntryBoxes
    {
        get
        {
            if (_entryBoxes is not null) return _entryBoxes;
            IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries = Entries;
            var boxes = new double[entries.Count][];
            for (int i = 0; i < entries.Count; i++) boxes[i] = ParetoDefinition.CompareVector(entries[i].Evaluation.Objectives);
            return _entryBoxes = boxes;
        }
    }

    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Get(EvolutionCellKey cell)
    {
        Guard.NotNull(cell);
        return cell.Bins.Count == 1 && _slots.TryGetValue(cell.Bins[0], out var entry) ? entry : null;
    }

    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Sample(StableRandom random)
    {
        Guard.NotNull(random); return Count == 0 ? null : Entries[random.NextInt(Count)];
    }

    /// <inheritdoc/>
    public EvolutionArchiveInsertionResult TryAdd(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation) => Add(candidate, evaluation).Result;

    EvolutionArchiveMutation<TGenome> IEvolutionArchiveMutationSource<TGenome>.TryAddWithMutation(
        EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation) => Add(candidate, evaluation);

    private EvolutionArchiveMutation<TGenome> Add(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation)
    {
        Guard.NotNull(candidate); Guard.NotNull(evaluation);
        if (!ParetoDefinition.HasValidObjectives(evaluation) || evaluation.Direction != Direction ||
            candidate.EvaluationId != evaluation.EvaluationId || candidate.CanonicalGenome.Id != evaluation.GenomeId)
            return Unchanged(EvolutionArchiveInsertionResult.Rejected);
        // A different measurement of one identity must be aggregated by the evaluator, not counted twice here.
        IReadOnlyList<EvolutionArchiveEntry<TGenome>>? pool = InfeasibleEntries;
        if (HoldsGenome(Entries, evaluation.GenomeId) || (pool is not null && HoldsGenome(pool, evaluation.GenomeId)))
            return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
        if (HoldsEvaluation(Entries, evaluation.EvaluationId) || (pool is not null && HoldsEvaluation(pool, evaluation.EvaluationId)))
            return Unchanged(EvolutionArchiveInsertionResult.Rejected);
        if (evaluation.ConstraintViolations.Any(value => value > 0))
        {
            if (_exploration is null) return Unchanged(EvolutionArchiveInsertionResult.Rejected);
            long next = Version;
            if (!_exploration.TryAdd(candidate, evaluation, () => next = checked(Version + 1)))
                return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
            Version = next;
            return Unchanged(EvolutionArchiveInsertionResult.RetainedForExploration);
        }
        var proposed = new EvolutionArchiveEntry<TGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
        double[] proposedBox = ParetoDefinition.CompareVector(evaluation.Objectives);
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> members = Entries;
        double[][] boxes = EntryBoxes;

        // Stored members are already mutually nondominated and occupy distinct boxes, so the proposed vector only has
        // to be compared with each of them once: at most one member can dominate it or share its box.
        var kept = new List<EvolutionArchiveEntry<TGenome>>(members.Count + 1);
        for (int i = 0; i < members.Count; i++)
        {
            if (EvolutionParetoDefinition.DominatesCompared(boxes[i], proposedBox))
                return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
            if (EvolutionParetoDefinition.SameCompared(boxes[i], proposedBox))
            {
                // One deterministic representative per box: the better raw objective vector keeps the slot.
                if (ParetoDefinition.CompareObjectiveTie(members[i], proposed) <= 0)
                    return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
                continue;
            }
            if (EvolutionParetoDefinition.DominatesCompared(proposedBox, boxes[i])) continue;
            kept.Add(members[i]);
        }
        kept.Add(proposed);
        EvolutionArchiveEntry<TGenome>[] retained = kept.ToArray();
        if (retained.Length > ParetoDefinition.Capacity)
        {
            retained = EvolutionParetoFront<TGenome>.DiverseOrder(ParetoDefinition, retained).Take(ParetoDefinition.Capacity).ToArray();
            if (Array.IndexOf(retained, proposed) < 0) return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
        }
        var retainedSet = new HashSet<EvolutionArchiveEntry<TGenome>>(retained);
        var removed = members.Where(entry => !retainedSet.Contains(entry)).ToArray();
        var vacated = new HashSet<int>();
        foreach (EvolutionArchiveEntry<TGenome> entry in removed) vacated.Add(entry.Cell.Bins[0]);
        int slot = -1;
        for (int index = 0; index < ParetoDefinition.Capacity; index++)
        {
            if (_slots.ContainsKey(index) && !vacated.Contains(index)) continue;
            slot = index; break;
        }
        if (slot < 0) throw new InvalidOperationException("The bounded front has no free storage slot.");
        var added = new EvolutionArchiveEntry<TGenome>(new EvolutionCellKey(new[] { slot }), candidate, evaluation);
        long nextVersion = checked(Version + 1);
        foreach (var entry in removed) _slots.Remove(entry.Cell.Bins[0]);
        _slots.Add(slot, added); Invalidate(); Version = nextVersion;
        EvolutionArchiveEntry<TGenome>? best = null;
        foreach (EvolutionArchiveEntry<TGenome> entry in Entries)
            if (best is null || ParetoDefinition.CompareRepresentative(entry, best) < 0) best = entry;
        Best = best;
        return new EvolutionArchiveMutation<TGenome>(removed.Length == 0 ? EvolutionArchiveInsertionResult.Inserted : EvolutionArchiveInsertionResult.InsertedWithEviction, added, removed);
    }

    private static bool HoldsGenome(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries, string genomeId)
    {
        for (int i = 0; i < entries.Count; i++)
            if (string.Equals(entries[i].Evaluation.GenomeId, genomeId, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool HoldsEvaluation(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries, long evaluationId)
    {
        for (int i = 0; i < entries.Count; i++) if (entries[i].Evaluation.EvaluationId == evaluationId) return true;
        return false;
    }

    private void Invalidate() { _entries = null; _entryBoxes = null; }

    private static EvolutionArchiveMutation<TGenome> Unchanged(EvolutionArchiveInsertionResult result) => new(result, null, Array.Empty<EvolutionArchiveEntry<TGenome>>());

    /// <inheritdoc/>
    public void Restore(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries, IReadOnlyList<EvolutionDescriptorDefinition> descriptors, long version)
        => RestoreWithExploration(entries, Array.Empty<EvolutionArchiveEntry<TGenome>>(), descriptors, version);

    /// <summary>Restores feasible and infeasible storage separately in one validated transaction.</summary>
    public void RestoreWithExploration(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries,
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> infeasibleEntries, IReadOnlyList<EvolutionDescriptorDefinition> descriptors, long version)
    {
        Guard.NotNull(entries); Guard.NotNull(infeasibleEntries); Guard.NotNull(descriptors);
        if (Version != 0 || Count != 0) throw new InvalidOperationException("Restore requires a pristine archive.");
        var copy = EvolutionCollection.CopyBounded(entries, ParetoDefinition.Capacity, nameof(entries));
        var exploration = EvolutionCollection.CopyBounded(infeasibleEntries, ParetoDefinition.InfeasibleCapacity, nameof(infeasibleEntries));
        if (version < copy.Length + exploration.Length || !descriptors.Select(axis => axis?.ToCanonicalString()).SequenceEqual(Descriptors.Select(axis => axis.ToCanonicalString())) ||
            copy.Any(entry => entry is null || entry.Cell.Bins.Count != 1 || entry.Cell.Bins[0] >= ParetoDefinition.Capacity ||
                entry.Evaluation.Direction != Direction || !ParetoDefinition.Accepts(entry.Evaluation) ||
                entry.Candidate.EvaluationId != entry.Evaluation.EvaluationId || entry.Candidate.CanonicalGenome.Id != entry.Evaluation.GenomeId) ||
            copy.Select(entry => entry.Cell.StableKey).Distinct().Count() != copy.Length ||
            copy.Select(entry => entry.Evaluation.GenomeId).Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            copy.Select(entry => entry.Evaluation.EvaluationId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Incompatible Pareto snapshot.", nameof(entries));
        var front = new EvolutionParetoFront<TGenome>(ParetoDefinition, copy);
        if (front.Entries.Count != copy.Length) throw new ArgumentException("Snapshot contains dominated or equivalent members.", nameof(entries));
        if (exploration.Any(entry => entry is null) || copy.Concat(exploration).Select(entry => entry.Evaluation.GenomeId).Distinct(StringComparer.Ordinal).Count() != copy.Length + exploration.Length ||
            copy.Concat(exploration).Select(entry => entry.Evaluation.EvaluationId).Distinct().Count() != copy.Length + exploration.Length)
            throw new ArgumentException("Feasible and infeasible pools cannot share identities.", nameof(infeasibleEntries));
        _exploration?.Restore(exploration);
        foreach (var entry in copy) _slots.Add(entry.Cell.Bins[0], entry);
        Version = version; Best = front.Representative; Invalidate();
    }
}
