namespace AiDotNet.Evolution;

/// <summary>Optional scalar-best-per-centroid repertoire, retaining at most K measured feasible elites.</summary>
/// <typeparam name="TGenome">The immutable task-specific genome.</typeparam>
/// <remarks>
/// Single-writer like MapElitesArchive. Cell keys contain one centroid index; the evaluation retains all original
/// named descriptors. Definition identity includes the fixed partition and direction. Checkpoints require the
/// same definition supplied by the archive factory; changing geometry requires explicit offline projection.
/// </remarks>
public sealed class CentroidArchive<TGenome> : ICheckpointableEvolutionArchive<TGenome>, IEvolutionArchiveCellCount,
    IEvolutionArchiveMutationSource<TGenome>
{
    private readonly SortedDictionary<string, EvolutionArchiveEntry<TGenome>> _cells = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _genomeCells = new(StringComparer.Ordinal);
    private IReadOnlyList<EvolutionArchiveEntry<TGenome>>? _entries;

    /// <summary>Initializes an empty archive with a frozen partition and scalar quality direction.</summary>
    public CentroidArchive(CentroidArchiveDefinition definition,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        Guard.NotNull(definition);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        Definition = definition;
        Direction = direction;
        DefinitionHash = EvolutionHash.Combine(new[] { "centroid-scalar-feasible-v1", definition.DefinitionHash, direction.ToString() });
    }

    /// <summary>Gets the frozen geometry, which callers must retain alongside checkpoint configuration.</summary>
    public CentroidArchiveDefinition Definition { get; }
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors => Definition.Descriptors;
    /// <inheritdoc/>
    public string DefinitionHash { get; }
    /// <inheritdoc/>
    public EvolutionOptimizationDirection Direction { get; }
    /// <inheritdoc/>
    public long TotalCells => Definition.Centroids.Count;
    /// <inheritdoc/>
    public int Count => _cells.Count;
    /// <inheritdoc/>
    public long Version { get; private set; }
    /// <inheritdoc/>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>> Entries => _entries ??= Array.AsReadOnly(_cells.Values.ToArray());
    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Best { get; private set; }

    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Get(EvolutionCellKey cell)
    {
        Guard.NotNull(cell);
        return _cells.TryGetValue(cell.StableKey, out var entry) ? entry : null;
    }

    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Sample(StableRandom random)
    {
        Guard.NotNull(random);
        return Count == 0 ? null : Entries[random.NextInt(Count)];
    }

    /// <inheritdoc/>
    public EvolutionArchiveInsertionResult TryAdd(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation) =>
        Add(candidate, evaluation).Result;

    EvolutionArchiveMutation<TGenome> IEvolutionArchiveMutationSource<TGenome>.TryAddWithMutation(
        EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation) => Add(candidate, evaluation);

    private EvolutionArchiveMutation<TGenome> Add(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation)
    {
        Guard.NotNull(candidate); Guard.NotNull(evaluation);
        if (evaluation.Status != EvolutionEvaluationStatus.Completed || !evaluation.Quality.HasValue ||
            evaluation.Direction != Direction || evaluation.ConstraintViolations.Any(value => value > 0) ||
            candidate.EvaluationId != evaluation.EvaluationId || candidate.CanonicalGenome.Id != evaluation.GenomeId)
            return Mutation(EvolutionArchiveInsertionResult.Rejected);
        int? index = Definition.FindCell(evaluation.Descriptors);
        if (!index.HasValue) return Mutation(EvolutionArchiveInsertionResult.Rejected);
        var cell = new EvolutionCellKey(new[] { index.Value });
        if (_genomeCells.TryGetValue(evaluation.GenomeId, out string? priorCell) && priorCell != cell.StableKey)
            return Mutation(EvolutionArchiveInsertionResult.Rejected);
        var entry = new EvolutionArchiveEntry<TGenome>(cell, candidate, evaluation);
        _cells.TryGetValue(cell.StableKey, out var incumbent);
        if (incumbent is not null && EvolutionEntryOrdering.Compare(Direction, entry, incumbent) >= 0)
            return Mutation(EvolutionArchiveInsertionResult.NotImproved);
        long nextVersion = checked(Version + 1);
        _cells[cell.StableKey] = entry;
        if (incumbent is not null) _genomeCells.Remove(incumbent.Evaluation.GenomeId);
        _genomeCells[evaluation.GenomeId] = cell.StableKey;
        if (Best is null || EvolutionEntryOrdering.Compare(Direction, entry, Best) < 0) Best = entry;
        _entries = null;
        Version = nextVersion;
        return new EvolutionArchiveMutation<TGenome>(incumbent is null ? EvolutionArchiveInsertionResult.Inserted : EvolutionArchiveInsertionResult.Replaced,
            entry, incumbent is null ? Array.Empty<EvolutionArchiveEntry<TGenome>>() : new[] { incumbent });
    }

    private static EvolutionArchiveMutation<TGenome> Mutation(EvolutionArchiveInsertionResult result) =>
        new(result, null, Array.Empty<EvolutionArchiveEntry<TGenome>>());

    /// <inheritdoc/>
    public void Restore(IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries,
        IReadOnlyList<EvolutionDescriptorDefinition> descriptors, long version)
    {
        Guard.NotNull(entries); Guard.NotNull(descriptors);
        if (Count != 0 || Version != 0) throw new InvalidOperationException("Restore requires a pristine empty archive.");
        if (version < entries.Count || entries.Count > TotalCells || descriptors.Count != Descriptors.Count ||
            !descriptors.Select(axis => axis?.ToCanonicalString()).SequenceEqual(Descriptors.Select(axis => axis.ToCanonicalString())))
            throw new ArgumentException("Incompatible archive snapshot.", nameof(entries));
        var staged = new CentroidArchive<TGenome>(Definition, Direction);
        var genomes = new HashSet<string>(StringComparer.Ordinal);
        var evaluationIds = new HashSet<long>();
        foreach (var entry in entries)
        {
            if (entry is null || !genomes.Add(entry.Evaluation.GenomeId) || !evaluationIds.Add(entry.Evaluation.EvaluationId) ||
                staged.TryAdd(entry.Candidate, entry.Evaluation) != EvolutionArchiveInsertionResult.Inserted ||
                staged.Get(entry.Cell)?.Evaluation.GenomeId != entry.Evaluation.GenomeId)
                throw new ArgumentException("Snapshot has invalid, duplicate or incorrectly routed entries.", nameof(entries));
        }
        foreach (var pair in staged._cells) _cells.Add(pair.Key, pair.Value);
        foreach (var pair in staged._genomeCells) _genomeCells.Add(pair.Key, pair.Value);
        Best = staged.Best;
        Version = version;
        _entries = null;
    }

    /// <summary>Projects retained elites into a new frozen partition without changing the source archive.</summary>
    /// <remarks>
    /// This is an offline transaction, not a mutation of an engine-owned archive. Collisions retain the deterministic
    /// best; unplaceable entries reject the entire operation. A changed definition gets a different compatibility
    /// hash and a new version. It cannot recover candidates discarded by the source archive or add evaluations.
    /// Use the same reference partition for both methods when comparing retained quality and coverage.
    /// </remarks>
    public static CentroidArchive<TGenome> Project(IEvolutionArchiveView<TGenome> source, CentroidArchiveDefinition definition)
        => ProjectWithReport(source, definition).Archive;

    /// <summary>Projects transactionally and captures immutable source/target definition, version and collision metadata.</summary>
    /// <remarks>No report or target is published if validation, routing or version advancement fails.
    /// The metadata is for experiment provenance, not a cryptographic proof of evaluation validity.</remarks>
    public static CentroidArchiveProjection<TGenome> ProjectWithReport(IEvolutionArchiveView<TGenome> source, CentroidArchiveDefinition definition)
    {
        Guard.NotNull(source); Guard.NotNull(definition);
        var staged = new CentroidArchive<TGenome>(definition, source.Direction);
        var snapshot = new EvolutionArchiveSnapshot<TGenome>(source);
        foreach (var entry in snapshot.Entries)
            if (staged.TryAdd(entry.Candidate, entry.Evaluation) == EvolutionArchiveInsertionResult.Rejected)
                throw new ArgumentException("Projection cannot place every retained feasible elite.", nameof(definition));
        staged.Version = checked(Math.Max(snapshot.Version, snapshot.Count) + 1);
        return new CentroidArchiveProjection<TGenome>(staged, new EvolutionArchiveProjectionReport(
            snapshot.DefinitionHash, snapshot.Version, snapshot.Count, staged.DefinitionHash, staged.Version, staged.Count));
    }
}
