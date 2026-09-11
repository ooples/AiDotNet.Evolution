namespace AiDotNet.Evolution;

/// <summary>Opt-in feasible nondominated archive with deterministic crowded-front capacity control.</summary>
/// <remarks>
/// Epsilon-box dominance follows the immutable definition; equal boxes retain the lexicographically best raw
/// objective vector. Capacity overflow removes the least crowded member, with ordinal identity tie-breaking.
/// Pruned historical candidates cannot be recovered: nondominance is guaranteed for the retained set, not the
/// entire evaluation history. Infeasible candidates are rejected; this archive has no infeasible exploration pool.
/// Cells are stable storage slots, not behavior-grid bins. Best is only the configured front representative.
/// </remarks>
public sealed class ParetoArchive<TGenome> : ICheckpointableEvolutionArchive<TGenome>, IEvolutionArchiveCellCount,
    IEvolutionParetoArchiveView<TGenome>, IEvolutionArchiveMutationSource<TGenome>
{
    private readonly SortedDictionary<int, EvolutionArchiveEntry<TGenome>> _slots = new();
    private IReadOnlyList<EvolutionArchiveEntry<TGenome>>? _entries;

    /// <summary>Creates an empty front; scalar direction affects only reporting and an explicitly chosen scalar representative.</summary>
    public ParetoArchive(EvolutionParetoDefinition definition,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(definition);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        ParetoDefinition = definition; Direction = direction;
        DefinitionHash = EvolutionHash.Combine(new[] { definition.DefinitionHash, direction.ToString() });
        Descriptors = Array.AsReadOnly(new[] { new EvolutionDescriptorDefinition("__pareto_slot", 0, definition.Capacity, definition.Capacity) });
    }

    /// <inheritdoc/>
    public EvolutionParetoDefinition ParetoDefinition { get; }
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
    public EvolutionParetoFront<TGenome> Front => new(ParetoDefinition, Entries);

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
        if (!ParetoDefinition.Accepts(evaluation) || evaluation.Direction != Direction ||
            candidate.EvaluationId != evaluation.EvaluationId || candidate.CanonicalGenome.Id != evaluation.GenomeId)
            return Unchanged(EvolutionArchiveInsertionResult.Rejected);
        // A different measurement of one identity must be aggregated by the evaluator, not counted twice here.
        if (Entries.Any(entry => entry.Evaluation.GenomeId == evaluation.GenomeId)) return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
        var proposed = new EvolutionArchiveEntry<TGenome>(new EvolutionCellKey(new[] { 0 }), candidate, evaluation);
        var front = new EvolutionParetoFront<TGenome>(ParetoDefinition, Entries.Concat(new[] { proposed }));
        var retained = front.Entries.ToArray();
        if (retained.Length > ParetoDefinition.Capacity)
            retained = EvolutionParetoFront<TGenome>.DiverseOrder(ParetoDefinition, retained).Take(ParetoDefinition.Capacity).ToArray();
        if (!retained.Contains(proposed)) return Unchanged(EvolutionArchiveInsertionResult.NotImproved);
        var removed = Entries.Where(entry => !retained.Contains(entry)).ToArray();
        int slot = Enumerable.Range(0, ParetoDefinition.Capacity).First(index => !_slots.ContainsKey(index) || removed.Any(entry => entry.Cell.Bins[0] == index));
        var added = new EvolutionArchiveEntry<TGenome>(new EvolutionCellKey(new[] { slot }), candidate, evaluation);
        long nextVersion = checked(Version + 1);
        foreach (var entry in removed) _slots.Remove(entry.Cell.Bins[0]);
        _slots.Add(slot, added); _entries = null; Version = nextVersion;
        Best = Entries.OrderBy(entry => entry, Comparer<EvolutionArchiveEntry<TGenome>>.Create(ParetoDefinition.CompareRepresentative)).First();
        return new EvolutionArchiveMutation<TGenome>(removed.Length == 0 ? EvolutionArchiveInsertionResult.Inserted : EvolutionArchiveInsertionResult.InsertedWithEviction, added, removed);
    }

    private static EvolutionArchiveMutation<TGenome> Unchanged(EvolutionArchiveInsertionResult result) => new(result, null, Array.Empty<EvolutionArchiveEntry<TGenome>>());

    /// <inheritdoc/>
    public void Restore(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries, IReadOnlyList<EvolutionDescriptorDefinition> descriptors, long version)
    {
        Guard.NotNull(entries); Guard.NotNull(descriptors);
        if (Version != 0 || Count != 0) throw new InvalidOperationException("Restore requires a pristine archive.");
        var copy = EvolutionCollection.CopyBounded(entries, ParetoDefinition.Capacity, nameof(entries));
        if (version < copy.Length || !descriptors.Select(axis => axis?.ToCanonicalString()).SequenceEqual(Descriptors.Select(axis => axis.ToCanonicalString())) ||
            copy.Any(entry => entry is null || entry.Cell.Bins.Count != 1 || entry.Cell.Bins[0] >= ParetoDefinition.Capacity ||
                entry.Evaluation.Direction != Direction || !ParetoDefinition.Accepts(entry.Evaluation) ||
                entry.Candidate.EvaluationId != entry.Evaluation.EvaluationId || entry.Candidate.CanonicalGenome.Id != entry.Evaluation.GenomeId) ||
            copy.Select(entry => entry.Cell.StableKey).Distinct().Count() != copy.Length ||
            copy.Select(entry => entry.Evaluation.GenomeId).Distinct(StringComparer.Ordinal).Count() != copy.Length ||
            copy.Select(entry => entry.Evaluation.EvaluationId).Distinct().Count() != copy.Length)
            throw new ArgumentException("Incompatible Pareto snapshot.", nameof(entries));
        var front = new EvolutionParetoFront<TGenome>(ParetoDefinition, copy);
        if (front.Entries.Count != copy.Length) throw new ArgumentException("Snapshot contains dominated or equivalent members.", nameof(entries));
        foreach (var entry in copy) _slots.Add(entry.Cell.Bins[0], entry);
        Version = version; Best = front.Representative; _entries = null;
    }
}
