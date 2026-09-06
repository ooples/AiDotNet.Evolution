using System.Collections.ObjectModel;

namespace AiDotNet.Evolution;

/// <summary>
/// A deterministic scalar-best-per-cell MAP-Elites archive with explicit descriptor and capacity policies.
/// </summary>
/// <typeparam name="TGenome">The task-specific genome type.</typeparam>
/// <remarks>
/// <para>
/// Each <see cref="EvolutionDescriptorDefinition"/> contributes one axis of a grid. A completed evaluation is
/// mapped to a cell by binning its descriptor values, and the cell keeps only the entry with the best scalar
/// quality for the configured <see cref="Direction"/>; ties are broken deterministically by genome identifier,
/// cell key, and evaluation identifier, so two archives fed the same evaluations in the same order hold
/// identical elites. When <see cref="Capacity"/> is smaller than the grid, a candidate that would open a new
/// cell must beat the archive-wide worst elite, which is then evicted. Cells live in a sorted dictionary keyed
/// by the ordinal cell key, so <see cref="Get"/> and the lookup half of <see cref="TryAdd"/> cost O(log n) in
/// the number of occupied cells, <see cref="Entries"/> materializes an O(n) copy in stable key order once per archive
/// version and then serves it from a cache,
/// <see cref="Best"/> is maintained incrementally and reads in O(1), and <see cref="Sample"/> and eviction scan the
/// occupied cells (at most O(n log n)).
/// Instances are not thread-safe; the engine performs all archive mutation from its sequential commit step.
/// </para>
/// <para><b>For Beginners:</b> Picture a wall of pigeonholes where each hole stands for one style of
/// solution, for example "small and fast" or "large and accurate", and each hole may hold only the single best
/// solution of that style found so far. That wall is a MAP-Elites archive, and this class is the standard
/// implementation the evolution engine uses. Instead of the whole population collapsing onto one lucky
/// design, the archive keeps a spread of strong, distinct designs, which gives you more choices at the end and
/// supplies diverse parents for the next round of mutations. You describe the axes of the wall with
/// descriptor definitions (name, range, number of bins, and what to do with out-of-range values), choose
/// whether higher or lower quality is better, and optionally cap how many pigeonholes may be filled at once.
/// Most users never construct this class directly because the engine and the AutoML facade build it for them;
/// reach for it when writing a custom evolution task or inspecting a finished run's elites.</para>
/// <para>
/// The algorithm follows Mouret and Clune, "Illuminating search spaces by mapping elites" (2015), restricted
/// to one scalar quality per cell. <see cref="DefinitionHash"/> captures every policy that changes insertion or
/// restoration, so a checkpoint is only restored into an archive with identical semantics.
/// </para>
/// </remarks>
public sealed class MapElitesArchive<TGenome> :
    IGrowableEvolutionArchive<TGenome>,
    IEvolutionArchiveMutationSource<TGenome>
{
    private readonly EvolutionDescriptorDefinition[] _descriptors;
    private readonly EvolutionDescriptorDefinition[] _configuredDescriptors;
    private readonly ReadOnlyCollection<EvolutionDescriptorDefinition> _descriptorView;
    private readonly SortedDictionary<string, EvolutionArchiveEntry<TGenome>> _cells = new(StringComparer.Ordinal);
    private readonly int _capacity;
    private readonly long _maximumGridCells;
    private readonly bool _hasGrowAxis;
    private readonly bool _capacityFollowsGrid;
    private IComparer<EvolutionArchiveEntry<TGenome>>? _comparer;
    private EvolutionArchiveEntry<TGenome>? _best;
    private ReadOnlyCollection<EvolutionArchiveEntry<TGenome>>? _entries;
    private long _entriesVersion = -1;

    /// <summary>Initializes an archive.</summary>
    /// <param name="descriptors">One or more uniquely named descriptor definitions.</param>
    /// <param name="direction">The scalar quality direction for every entry.</param>
    /// <param name="capacity">Maximum occupied cells, or zero to use the full descriptor grid.</param>
    /// <param name="maximumGridCells">Safety limit for the descriptor-grid product.</param>
    /// <exception cref="ArgumentException">
    /// No descriptors were supplied, a descriptor is <c>null</c>, descriptor names collide, or the grid exceeds
    /// <paramref name="maximumGridCells"/> or overflows a 64-bit integer.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="direction"/> is undefined, <paramref name="capacity"/> is negative or exceeds the grid, or
    /// <paramref name="maximumGridCells"/> is not positive.
    /// </exception>
    public MapElitesArchive(
        IEnumerable<EvolutionDescriptorDefinition> descriptors,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize,
        int capacity = 0,
        long maximumGridCells = 10_000_000)
    {
        Guard.NotNull(descriptors);
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        if (capacity < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (maximumGridCells <= 0) throw new ArgumentOutOfRangeException(nameof(maximumGridCells));

        _descriptors = EvolutionCollection.ToBoundedArray(
            descriptors,
            EvolutionCollectionLimits.MaximumArchiveDimensions,
            nameof(descriptors));
        if (_descriptors.Length == 0) throw new ArgumentException("At least one descriptor is required.", nameof(descriptors));
        if (_descriptors.Any(item => item is null)) throw new ArgumentException("Descriptors cannot contain null entries.", nameof(descriptors));
        if (_descriptors.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != _descriptors.Length)
            throw new ArgumentException("Descriptor names must be unique using ordinal comparison.", nameof(descriptors));

        long gridCells = 1;
        try
        {
            foreach (EvolutionDescriptorDefinition descriptor in _descriptors)
            {
                gridCells = checked(gridCells * descriptor.EffectiveBinCount);
                if (gridCells > maximumGridCells)
                    throw new ArgumentException($"The descriptor grid exceeds the {maximumGridCells} cell safety limit.", nameof(descriptors));
            }
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException("The descriptor-grid size overflowed a 64-bit integer.", nameof(descriptors), exception);
        }

        Direction = direction;
        TotalGridCells = gridCells;
        _maximumGridCells = maximumGridCells;
        _hasGrowAxis = _descriptors.Any(item => item.OutOfRangePolicy == EvolutionOutOfRangePolicy.Grow);
        // The configured bounds are kept verbatim: they anchor the definition hash and are the reference a restored
        // checkpoint's grown bounds are validated against.
        _configuredDescriptors = _descriptors.ToArray();
        _capacityFollowsGrid = capacity == 0;
        _capacity = capacity == 0 ? (int)Math.Min(gridCells, int.MaxValue) : capacity;
        if (_capacity > gridCells) throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity cannot exceed the descriptor grid.");
        _descriptorView = Array.AsReadOnly(_descriptors);
        DefinitionHash = EvolutionHash.Combine(new[] { "map-elites-scalar-v1" }
            .Concat(_descriptors.Select(item => item.ToCanonicalString())).Concat(new[]
        {
            ((int)direction).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _capacity.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));
    }

    /// <inheritdoc/>
    public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors => _descriptorView;

    /// <inheritdoc/>
    public EvolutionOptimizationDirection Direction { get; }

    /// <summary>Gets the full number of physical cells implied by descriptor policies.</summary>
    /// <remarks>Grows with the grid when a descriptor uses <see cref="EvolutionOutOfRangePolicy.Grow"/>.</remarks>
    public long TotalGridCells { get; private set; }

    /// <summary>Gets the maximum number of occupied cells.</summary>
    /// <remarks>
    /// An archive constructed with a capacity of zero tracks the whole grid, so it widens along with
    /// <see cref="TotalGridCells"/> under <see cref="EvolutionOutOfRangePolicy.Grow"/>. An explicitly requested
    /// capacity is a fixed budget and never changes.
    /// </remarks>
    public int Capacity => _capacityFollowsGrid ? (int)Math.Min(TotalGridCells, int.MaxValue) : _capacity;

    /// <inheritdoc/>
    /// <remarks>
    /// Combines the archive algorithm identifier, every descriptor's canonical string, the optimization
    /// direction, and the effective capacity, so any change to those settings yields a different hash.
    /// </remarks>
    public string DefinitionHash { get; }

    /// <inheritdoc/>
    public int Count => _cells.Count;

    /// <inheritdoc/>
    public long Version { get; private set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Materialized once per archive version and cached, so repeated reads between insertions - which the engine
    /// performs once per proposal for selection, novelty screening, and history bookkeeping - cost O(1) instead of
    /// O(n) copies. The cached instance is read-only, so handing the same one to several callers is safe.
    /// </remarks>
    public IReadOnlyList<EvolutionArchiveEntry<TGenome>> Entries
    {
        get
        {
            if (_entries is null || _entriesVersion != Version)
            {
                _entries = Array.AsReadOnly(_cells.Values.ToArray());
                _entriesVersion = Version;
            }
            return _entries;
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Maintained incrementally on every accepted insertion, so reading it costs O(1). The ordering is a total
    /// order, so a candidate that beats the incumbent best necessarily beats every other entry and becomes the new
    /// best without a rescan.
    /// </remarks>
    public EvolutionArchiveEntry<TGenome>? Best => _best;

    /// <inheritdoc/>
    public EvolutionArchiveInsertionResult TryAdd(EvolutionCandidate<TGenome> candidate, EvolutionEvaluation evaluation)
        => TryAddWithMutation(candidate, evaluation).Result;

    /// <summary>Attempts insertion and reports the exact aggregate delta to the owning engine.</summary>
    EvolutionArchiveMutation<TGenome> IEvolutionArchiveMutationSource<TGenome>.TryAddWithMutation(
        EvolutionCandidate<TGenome> candidate,
        EvolutionEvaluation evaluation) => TryAddWithMutation(candidate, evaluation);

    private EvolutionArchiveMutation<TGenome> TryAddWithMutation(
        EvolutionCandidate<TGenome> candidate,
        EvolutionEvaluation evaluation)
    {
        Guard.NotNull(candidate);
        Guard.NotNull(evaluation);
        if (evaluation.Status != EvolutionEvaluationStatus.Completed || !evaluation.Quality.HasValue ||
            evaluation.Direction != Direction || candidate.EvaluationId != evaluation.EvaluationId ||
            candidate.CanonicalGenome.Id != evaluation.GenomeId)
        {
            return Mutation(EvolutionArchiveInsertionResult.Rejected);
        }

        // A Grow axis reports a value outside its range as unbinnable, which is the archive's cue to widen rather
        // than discard. Growth happens before keying so the candidate and every incumbent share one grid.
        List<EvolutionArchiveEntry<TGenome>>? removed = GrowToFit(evaluation.Descriptors);

        EvolutionCellKey? key = TryCreateKey(evaluation.Descriptors);
        if (key is null) return Mutation(EvolutionArchiveInsertionResult.Rejected, removed: removed);

        var candidateEntry = new EvolutionArchiveEntry<TGenome>(key, candidate, evaluation);
        if (_cells.TryGetValue(key.StableKey, out EvolutionArchiveEntry<TGenome>? incumbent))
        {
            if (Comparer.Compare(candidateEntry, incumbent) >= 0)
                return Mutation(EvolutionArchiveInsertionResult.NotImproved, removed: removed);
            _cells[key.StableKey] = candidateEntry;
            AddRemoved(ref removed, incumbent);
            PromoteIfBest(candidateEntry);
            Version++;
            return Mutation(EvolutionArchiveInsertionResult.Replaced, candidateEntry, removed);
        }

        if (_cells.Count < Capacity)
        {
            _cells.Add(key.StableKey, candidateEntry);
            PromoteIfBest(candidateEntry);
            Version++;
            return Mutation(EvolutionArchiveInsertionResult.Inserted, candidateEntry, removed);
        }

        EvolutionArchiveEntry<TGenome> worst = _cells.Values.First();
        foreach (EvolutionArchiveEntry<TGenome> entry in _cells.Values)
            if (Comparer.Compare(entry, worst) > 0) worst = entry;
        if (Comparer.Compare(candidateEntry, worst) >= 0)
            return Mutation(EvolutionArchiveInsertionResult.NotImproved, removed: removed);
        _cells.Remove(worst.Cell.StableKey);
        _cells.Add(key.StableKey, candidateEntry);
        AddRemoved(ref removed, worst);
        if (ReferenceEquals(_best, worst)) _best = null;
        PromoteIfBest(candidateEntry);
        Version++;
        return Mutation(EvolutionArchiveInsertionResult.InsertedWithEviction, candidateEntry, removed);
    }

    private static EvolutionArchiveMutation<TGenome> Mutation(
        EvolutionArchiveInsertionResult result,
        EvolutionArchiveEntry<TGenome>? added = null,
        IReadOnlyList<EvolutionArchiveEntry<TGenome>>? removed = null) =>
        new(result, added, removed ?? Array.Empty<EvolutionArchiveEntry<TGenome>>());

    private static void AddRemoved(
        ref List<EvolutionArchiveEntry<TGenome>>? removed,
        EvolutionArchiveEntry<TGenome> entry)
    {
        removed ??= new List<EvolutionArchiveEntry<TGenome>>();
        removed.Add(entry);
    }

    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Get(EvolutionCellKey cell)
    {
        Guard.NotNull(cell);
        return _cells.TryGetValue(cell.StableKey, out EvolutionArchiveEntry<TGenome>? entry) ? entry : null;
    }

    /// <inheritdoc/>
    public EvolutionArchiveEntry<TGenome>? Sample(StableRandom random)
    {
        Guard.NotNull(random);
        if (_cells.Count == 0) return null;
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries = Entries;
        return entries[random.NextInt(entries.Count)];
    }

    /// <summary>Widens any Grow axis that cannot bin the supplied values, then rebins existing entries.</summary>
    /// <param name="descriptors">The named descriptor values about to be archived.</param>
    /// <remarks>
    /// Bin width is held constant, so a cell covers the same span of descriptor values before and after growth and no
    /// entry has to be re-evaluated. Its cell index can still move: a wider range shifts every index when bins are
    /// added below the minimum, and an entry sitting exactly on the old maximum moves off the last bin when bins are
    /// added above it. Both are handled by rebinning from the stored descriptor values rather than by adjusting
    /// indices. Growth that would breach the grid safety limit is declined, in which case the candidate is simply not
    /// archived, exactly as Reject would have behaved.
    /// </remarks>
    private List<EvolutionArchiveEntry<TGenome>>? GrowToFit(IReadOnlyDictionary<string, double> descriptors)
    {
        if (!_hasGrowAxis) return null;

        // Nothing is widened for a candidate that cannot be archived anyway. Growing first and discovering a second
        // axis rejects it would leave the grid permanently larger for a candidate that never entered it, which
        // quietly lowers coverage and, with a capacity that follows the grid, changes what the archive holds.
        foreach (EvolutionDescriptorDefinition definition in _descriptors)
        {
            if (definition.OutOfRangePolicy == EvolutionOutOfRangePolicy.Grow) continue;
            if (!descriptors.TryGetValue(definition.Name, out double other) || !definition.TryGetBin(other, out _))
                return null;
        }

        var proposed = (EvolutionDescriptorDefinition[])_descriptors.Clone();
        int growthCount = 0;

        for (int axis = 0; axis < _descriptors.Length; axis++)
        {
            EvolutionDescriptorDefinition definition = _descriptors[axis];
            if (definition.OutOfRangePolicy != EvolutionOutOfRangePolicy.Grow) continue;
            if (!descriptors.TryGetValue(definition.Name, out double value)) continue;
            if (!EvolutionDescriptorDefinition.IsFinite(value)) continue;
            if (value >= definition.Minimum && value <= definition.Maximum) continue;

            EvolutionDescriptorDefinition widened = definition.Widen(value);
            if (ReferenceEquals(widened, definition)) continue;

            // A bound computed as minimum minus a multiple of the bin width can land a fraction of a unit on the
            // wrong side of the value it was widened for. Widening the archive and then still rejecting the
            // candidate that caused it would leave an empty extra bin and no record of why, so the new definition
            // has to be able to bin the value before it is adopted.
            if (!widened.TryGetBinIgnoringPolicy(value, out _)) continue;

            proposed[axis] = widened;
            growthCount++;
        }

        if (growthCount == 0) return null;

        long projected = 1;
        foreach (EvolutionDescriptorDefinition definition in proposed)
        {
            long bins = definition.EffectiveBinCount;
            if (bins != 0 && projected > _maximumGridCells / bins)
            {
                // Growth is one transaction. A candidate that cannot fit all widened axes must not permanently
                // change a subset of them or re-key incumbents it never competes with.
                return null;
            }
            projected *= bins;
        }

        long grownVersion = checked(Version + growthCount);
        Array.Copy(proposed, _descriptors, proposed.Length);
        TotalGridCells = projected;
        Version = grownVersion;
        return RebinEntries();
    }

    /// <summary>Recomputes every entry's cell against the current descriptor definitions.</summary>
    /// <remarks>
    /// Rebinning from each entry's own descriptor values is what keeps the archive honest after growth: an index
    /// carried over unchanged would silently mean a different range of values. Two entries can in principle land in
    /// one cell, which is resolved the same way an ordinary insertion would resolve it, by keeping the better of the
    /// two under the archive's total ordering, so the outcome does not depend on iteration order.
    /// </remarks>
    private List<EvolutionArchiveEntry<TGenome>>? RebinEntries()
    {
        if (_cells.Count == 0) return null;

        var rebinned = new List<EvolutionArchiveEntry<TGenome>>(_cells.Count);
        foreach (EvolutionArchiveEntry<TGenome> entry in _cells.Values)
        {
            EvolutionCellKey? key = TryCreateKey(entry.Evaluation.Descriptors);
            rebinned.Add(key is null
                ? entry
                : new EvolutionArchiveEntry<TGenome>(key, entry.Candidate, entry.Evaluation));
        }

        _cells.Clear();
        List<EvolutionArchiveEntry<TGenome>>? removed = null;
        foreach (EvolutionArchiveEntry<TGenome> entry in rebinned)
        {
            if (_cells.TryGetValue(entry.Cell.StableKey, out EvolutionArchiveEntry<TGenome>? incumbent) &&
                Comparer.Compare(entry, incumbent) >= 0)
            {
                AddRemoved(ref removed, entry);
                continue;
            }
            if (incumbent is not null) AddRemoved(ref removed, incumbent);
            _cells[entry.Cell.StableKey] = entry;
        }

        // The retained entries are new objects, so the cached best reference has to be rebuilt rather than kept.
        _best = null;
        foreach (EvolutionArchiveEntry<TGenome> entry in _cells.Values) PromoteIfBest(entry);
        return removed;
    }

    /// <summary>Re-measures every elite against a new descriptor reading and re-files the whole archive.</summary>
    /// <param name="measure">
    /// Produces replacement descriptor values for one elite; returning <see langword="null"/> keeps its existing
    /// values. The callback must not mutate this archive or the genome it receives. All archive genomes satisfy the
    /// immutable-genome contract, so the callback may safely inspect but never alter their reachable state.
    /// </param>
    /// <returns>How many elites remain after deterministic collision resolution.</returns>
    /// <remarks>
    /// The operation is transactional. All callbacks and replacement evaluations are completed first, a separate
    /// archive is grown and populated, and the live grid is changed only after staging succeeds. An exception therefore
    /// leaves bounds, cells, best entry, cached entries, and version unchanged.
    /// </remarks>
    public int Remeasure(Func<TGenome, IReadOnlyDictionary<string, double>?> measure)
    {
        Guard.NotNull(measure);
        if (_cells.Count == 0) return 0;

        EvolutionArchiveEntry<TGenome>[] ordered = _cells.Values
            .OrderBy(item => item.Cell.StableKey, StringComparer.Ordinal)
            .ToArray();
        var replacements = new EvolutionEvaluation?[ordered.Length];

        // Invoke arbitrary caller code before touching even a staging grid. This validates every replacement through
        // EvolutionEvaluation's constructor and guarantees callback failure has no observable archive side effect.
        for (int index = 0; index < ordered.Length; index++)
        {
            IReadOnlyDictionary<string, double>? values =
                measure(ordered[index].Candidate.CanonicalGenome.Genome);
            if (values is not null) replacements[index] = ordered[index].Evaluation.WithDescriptors(values);
        }

        var staged = new MapElitesArchive<TGenome>(
            _descriptors,
            Direction,
            _capacityFollowsGrid ? 0 : _capacity,
            _maximumGridCells);

        // Settle the final grid before filing anything. An unplaceable replacement keeps the old descriptors, but all
        // entries are keyed against the same final bounds after every successful growth has been considered.
        for (int index = 0; index < ordered.Length; index++)
        {
            EvolutionEvaluation? replacement = replacements[index];
            if (replacement is null) continue;
            staged.GrowToFit(replacement.Descriptors);
            if (staged.TryCreateKey(replacement.Descriptors) is null) replacements[index] = null;
        }

        for (int index = 0; index < ordered.Length; index++)
        {
            EvolutionArchiveEntry<TGenome> prior = ordered[index];
            EvolutionEvaluation evaluation = replacements[index] ?? prior.Evaluation;
            EvolutionArchiveInsertionResult outcome = staged.TryAdd(prior.Candidate, evaluation);
            if (outcome == EvolutionArchiveInsertionResult.Rejected)
                throw new InvalidOperationException("A previously archived elite could not be staged after remeasurement.");
        }

        for (int axis = 0; axis < _descriptors.Length; axis++)
            _descriptors[axis] = staged._descriptors[axis];
        TotalGridCells = staged.TotalGridCells;
        _cells.Clear();
        foreach (KeyValuePair<string, EvolutionArchiveEntry<TGenome>> cell in staged._cells)
            _cells.Add(cell.Key, cell.Value);
        _best = staged._best;
        Version++;
        return _cells.Count;
    }

    /// <summary>Computes the cell for descriptor values without modifying the archive.</summary>
    /// <param name="descriptors">The named descriptor values.</param>
    /// <returns>The cell, or <c>null</c> when a value is missing or rejected.</returns>
    public EvolutionCellKey? TryCreateKey(IReadOnlyDictionary<string, double> descriptors)
    {
        Guard.NotNull(descriptors);
        var bins = new int[_descriptors.Length];
        for (int i = 0; i < _descriptors.Length; i++)
        {
            EvolutionDescriptorDefinition definition = _descriptors[i];
            if (!descriptors.TryGetValue(definition.Name, out double value) || !definition.TryGetBin(value, out bins[i]))
                return null;
        }
        return new EvolutionCellKey(bins);
    }

    /// <inheritdoc/>
    public void Restore(
        IReadOnlyList<EvolutionArchiveEntry<TGenome>> entries,
        IReadOnlyList<EvolutionDescriptorDefinition> descriptors,
        long version)
    {
        Guard.NotNull(entries);
        Guard.NotNull(descriptors);
        if (_cells.Count != 0 || Version != 0) throw new InvalidOperationException("Only an empty archive can be restored.");
        EvolutionArchiveEntry<TGenome>[] entrySnapshot = EvolutionCollection.CopyBounded(
            entries,
            EvolutionCollectionLimits.MaximumResultEntries,
            nameof(entries));
        EvolutionDescriptorDefinition[] descriptorSnapshot = EvolutionCollection.CopyBounded(
            descriptors,
            EvolutionCollectionLimits.MaximumArchiveDimensions,
            nameof(descriptors));
        if (version < entrySnapshot.Length) throw new ArgumentOutOfRangeException(nameof(version));

        // Restore into an isolated archive first. A corrupt checkpoint must not leave this instance partly populated
        // or with adopted bounds after throwing, because callers may safely fall back to an older snapshot.
        var staged = new MapElitesArchive<TGenome>(
            _configuredDescriptors,
            Direction,
            _capacityFollowsGrid ? 0 : _capacity,
            _maximumGridCells);
        staged.RestoreDescriptorBounds(descriptorSnapshot);
        foreach (EvolutionArchiveEntry<TGenome> entry in entrySnapshot.OrderBy(item => item.Cell.StableKey, StringComparer.Ordinal))
        {
            // Only a plain insertion is legal during a restore. An eviction would mean the checkpoint holds more
            // elites than this archive's capacity allows, and silently dropping one of them would resume a run that
            // is quietly missing part of what it had found.
            EvolutionArchiveInsertionResult result = staged.TryAdd(entry.Candidate, entry.Evaluation);
            if (result != EvolutionArchiveInsertionResult.Inserted)
                throw new InvalidDataException(
                    "The archive checkpoint contains an invalid or conflicting elite, or more elites than the " +
                    "configured capacity holds.");
        }
        if (version < staged.Version) throw new ArgumentOutOfRangeException(nameof(version));

        Array.Copy(staged._descriptors, _descriptors, _descriptors.Length);
        TotalGridCells = staged.TotalGridCells;
        foreach (KeyValuePair<string, EvolutionArchiveEntry<TGenome>> cell in staged._cells)
            _cells.Add(cell.Key, cell.Value);
        _best = staged._best;
        Version = version;
        _entries = null;
        _entriesVersion = -1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Adopting the checkpointed ranges before any entry is replayed is what makes a resumed run identical to the run
    /// that wrote the checkpoint: every restored elite bins against the grid it was archived on, and restoring in cell
    /// order rather than commit order cannot change the outcome because no growth happens during the replay.
    /// </remarks>
    public void RestoreDescriptorBounds(IReadOnlyList<EvolutionDescriptorDefinition> descriptors)
    {
        Guard.NotNull(descriptors);
        if (_cells.Count != 0 || Version != 0)
            throw new InvalidOperationException("Only an empty archive can adopt checkpointed descriptor bounds.");
        if (descriptors.Count != _configuredDescriptors.Length)
            throw new InvalidDataException("The checkpoint descriptor count does not match this archive.");

        var adopted = new EvolutionDescriptorDefinition[_configuredDescriptors.Length];
        long projected = 1;
        for (int i = 0; i < _configuredDescriptors.Length; i++)
        {
            EvolutionDescriptorDefinition configured = _configuredDescriptors[i];
            EvolutionDescriptorDefinition restored = descriptors[i]
                ?? throw new InvalidDataException("A checkpoint descriptor definition is missing.");
            if (!IsWideningOf(configured, restored))
                throw new InvalidDataException(
                    $"The checkpoint descriptor '{restored.Name}' is not a widening of the configured descriptor " +
                    $"'{configured.Name}'; the checkpoint belongs to a differently configured archive.");

            adopted[i] = restored;
            long bins = restored.EffectiveBinCount;
            if (bins != 0 && projected > _maximumGridCells / bins)
                throw new InvalidDataException("The checkpoint descriptor grid exceeds this archive's cell safety limit.");
            projected *= bins;
        }

        if (projected < _capacity)
            throw new InvalidDataException("The checkpoint descriptor grid is smaller than this archive's capacity.");

        Array.Copy(adopted, _descriptors, adopted.Length);
        TotalGridCells = projected;
    }

    /// <summary>Reports whether a restored definition is the configured one after zero or more growth steps.</summary>
    /// <param name="configured">The definition this archive was constructed with.</param>
    /// <param name="restored">The definition read back from a checkpoint.</param>
    /// <returns><c>true</c> when the restored definition covers the configured one on same-width bins.</returns>
    /// <remarks>
    /// Growth keeps bin width fixed in exact arithmetic, but repeatedly recomputing a range from a bin count lets the
    /// width drift in the last bits, so width and bounds are compared with a relative tolerance rather than for exact
    /// equality. Identity beyond the bounds - name, policy, and the fact that a non-growable axis never widens - is
    /// compared exactly.
    /// </remarks>
    private static bool IsWideningOf(
        EvolutionDescriptorDefinition configured,
        EvolutionDescriptorDefinition restored)
    {
        if (!string.Equals(restored.Name, configured.Name, StringComparison.Ordinal)) return false;
        if (restored.OutOfRangePolicy != configured.OutOfRangePolicy) return false;
        if (restored.BinCount == configured.BinCount &&
            restored.Minimum == configured.Minimum && restored.Maximum == configured.Maximum)
        {
            return true;
        }

        if (configured.OutOfRangePolicy != EvolutionOutOfRangePolicy.Grow) return false;
        if (restored.BinCount < configured.BinCount) return false;

        double width = configured.BinWidth;
        double tolerance = Math.Abs(width) * 1e-9;
        if (restored.Minimum > configured.Minimum + tolerance) return false;
        if (restored.Maximum < configured.Maximum - tolerance) return false;
        return Math.Abs(restored.BinWidth - width) <= tolerance;
    }

    private IComparer<EvolutionArchiveEntry<TGenome>> Comparer =>
        _comparer ??= EvolutionEntryOrdering.BestFirst<TGenome>(Direction);

    private void PromoteIfBest(EvolutionArchiveEntry<TGenome> entry)
    {
        if (_best is null || Comparer.Compare(entry, _best) < 0) _best = entry;
    }
}
