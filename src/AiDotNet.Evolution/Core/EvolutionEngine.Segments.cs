using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static AiDotNet.Evolution.EvolutionEngineDocuments;

namespace AiDotNet.Evolution;

// Segmented checkpoints (V1-73, #178; archives E-1). With a store that implements IEvolutionCheckpointSegmentStore, the
// deduplication set, the evaluation cache and the island archives leave the checkpoint payload. The segments are an
// append-only journal of the run's state: each save appends one segment holding only what changed since the previous
// save, and every CheckpointCompactionInterval segments a base segment restates everything, so a save costs what
// changed rather than everything the run holds. The payload names the segments, with their SHA-256, that rebuild the
// state on resume: the base, then each delta replayed in order. Segments are streamed through a small buffer, so no save
// holds the whole state at once.
public sealed partial class EvolutionEngine<TGenome>
{
    // Schema 1 segments hold the deduplication set and cache; schema 2 segments hold the island archives as well. Every
    // segment of one checkpoint has the same schema, so a payload says which one to expect.
    private const int SegmentSchemaVersion = 1;
    private const int ArchiveSegmentSchemaVersion = 2;
    private const int SegmentFlushBytes = 64 * 1024;
    // Under Auto, an inline payload at least this long (in UTF-16 characters, about a megabyte of JSON) moves the run to
    // segments: below it a whole-state save is cheap, above it every save rewrites everything the archives hold.
    internal const int AutoSegmentPayloadCharacters = 1024 * 1024;

    // The segments the last saved checkpoint names (a base first) and the identifier the next segment takes.
    private List<SegmentReferenceDocument> _segmentManifest = new();
    private long _nextSegmentId;
    // Set once any cached result has carried a measurement origin. A segmented payload leaves the cache out, so the
    // schema version cannot be read off the document; the version this implies is never too low, only possibly high.
    private bool _cacheHeldMeasurementOrigin;
    // Changes between the last saved boundary and the current one: the next delta segment.
    private readonly List<SafeChange> _unsavedChanges = new();
    // What a save prepared; it becomes current only once the store has accepted the checkpoint that names it.
    private PendingSegments? _pendingSegments;
    private long _savedSegmentBoundary = -1;
    private EvolutionCheckpoint? _savedSegmentedCheckpoint;
    // The archives the saved segments rebuild, which the next delta is taken against; null when they hold none.
    private ArchiveState? _savedArchive;
    // The length of the last inline payload, which Auto compares with AutoSegmentPayloadCharacters.
    private long _lastInlinePayloadLength;

    // The segment store to save through, or null for an inline checkpoint. Auto switches once the run remembers enough
    // genomes, or its payload is large enough, for a full save to cost more than a delta, and stays segmented after that.
    private IEvolutionCheckpointSegmentStore? SegmentStoreForSave()
    {
        var store = _checkpointStore as IEvolutionCheckpointSegmentStore;
        switch (_options.CheckpointFormat)
        {
            case EvolutionCheckpointFormat.Inline:
                return null;
            case EvolutionCheckpointFormat.Segmented:
                return store ?? throw new InvalidOperationException(
                    "EvolutionCheckpointFormat.Segmented needs a checkpoint store that implements IEvolutionCheckpointSegmentStore.");
            default:
                return store is not null &&
                       (_segmentManifest.Count > 0 || (long)_seen.Count + _cache.Count >= _options.CheckpointSegmentThreshold ||
                        _lastInlinePayloadLength >= AutoSegmentPayloadCharacters)
                    ? store
                    : null;
        }
    }

    private void ResetSegments()
    {
        _segmentManifest = new List<SegmentReferenceDocument>();
        _unsavedChanges.Clear();
        _pendingSegments = null;
        _savedSegmentBoundary = -1;
        _savedSegmentedCheckpoint = null;
        _savedArchive = null;
    }

    private async Task<EvolutionCheckpoint?> PrepareSegmentedCheckpointAsync(IEvolutionCheckpointSegmentStore store,
        CancellationToken cancellationToken)
    {
        // A boundary that was already saved is saved again as the same checkpoint, which a store treats as a no-op.
        if (_savedSegmentBoundary == _safeSequence && _savedSegmentedCheckpoint is not null) return _savedSegmentedCheckpoint;
        if (_safeDocument is not { } document) return null;
        List<ArchiveDocument> islands = document.Islands
            ?? throw new InvalidOperationException("The checkpoint boundary has no island archives.");
        // The archives go into the segments whenever their cells identify their entries; a Pareto front stays inline.
        ArchiveState? archive = _safeArchive is { } boundary && CanSegmentArchives(islands, boundary) ? boundary : null;

        // Everything a segment needs is taken before the first await: evaluations may add to the live set meanwhile. A
        // change of whether the archives are in the segments starts a new chain, so one chain never mixes the two.
        bool writeBase = _segmentManifest.Count == 0 || _segmentManifest.Count >= _options.CheckpointCompactionInterval ||
                         (archive is null) != (_savedArchive is null);
        var manifest = writeBase ? new List<SegmentReferenceDocument>() : new List<SegmentReferenceDocument>(_segmentManifest);
        long nextId = _nextSegmentId;
        Func<SegmentWriter, Task>? write = null;
        if (writeBase)
        {
            BaseSnapshot snapshot = BoundarySnapshot();
            write = segment => WriteBase(segment, snapshot, archive);
        }
        else
        {
            StateDelta delta = StateDelta.From(_unsavedChanges);
            List<ArchiveSegmentDocument>? archiveDelta = archive is not null && _savedArchive is { } saved
                ? ArchiveDelta(saved, archive)
                : null;
            if (!delta.IsEmpty || archiveDelta is { Count: > 0 })
                write = segment => WriteDelta(segment, delta, archive is not null, archiveDelta);
        }

        if (write is not null)
        {
            long id = nextId++;
            string digest = await WriteSegmentAsync(store, id, write, cancellationToken).ConfigureAwait(false);
            manifest.Add(new SegmentReferenceDocument { Id = id, IsBase = writeBase, Sha256 = digest });
        }

        document.SeenGenomeIds = null;
        document.Cache = null;
        document.DeduplicationOrder = _options.DeduplicationCapacity > 0 ? BoundaryDeduplicationOrder() : null;
        document.Segments = manifest;
        document.NextSegmentId = nextId;
        int featureVersion = FeatureSchemaVersion(document);
        // The cache was just taken out of the document, so its measurement origins no longer show in it.
        if (_cacheHeldMeasurementOrigin && featureVersion < EngineMeasurementOriginSchemaVersion)
            featureVersion = EngineMeasurementOriginSchemaVersion;
        document.SchemaVersion = featureVersion + (archive is null ? SegmentedSchemaOffset : ArchiveSegmentedSchemaOffset);
        string payload;
        if (archive is null)
        {
            payload = JsonSerializer.Serialize(document, EvolutionStateJsonContext.Default.EngineStateDocument);
        }
        else
        {
            // The payload keeps each island's version and grid; its entries are in the segments. The boundary document
            // itself keeps them, because a later inline save (after a failed store write, say) still serializes it.
            document.Islands = islands.Select(island => new ArchiveDocument
            {
                Version = island.Version,
                Descriptors = island.Descriptors,
                EntriesInSegments = true
            }).ToList();
            try
            {
                payload = JsonSerializer.Serialize(document, EvolutionStateJsonContext.Default.EngineStateDocument);
            }
            finally
            {
                document.Islands = islands;
            }
        }
        if (payload.Length > EvolutionCollectionLimits.MaximumCheckpointBytes ||
            Encoding.UTF8.GetByteCount(payload) > EvolutionCollectionLimits.MaximumCheckpointBytes)
            throw new InvalidDataException(
                $"The evolution engine state exceeds the {EvolutionCollectionLimits.MaximumCheckpointBytes}-byte package limit.");

        EvolutionCheckpoint checkpoint = new EvolutionCheckpoint(_options.RunId, _safeSequence, _compatibilityHash, payload,
                EvolutionCheckpoint.CurrentSchemaVersion, BestQualityAcrossIslands(), _islands[0].Direction)
            .WithSegmentIds(manifest.Select(segment => segment.Id).ToList());
        _pendingSegments = new PendingSegments(checkpoint, manifest, nextId, _safeSequence, archive);
        return checkpoint;
    }

    // Called after the store accepted a checkpoint: the segments it names are what the next save builds on.
    private void CommitSegmentedCheckpoint(EvolutionCheckpoint saved)
    {
        if (_pendingSegments is not { } pending || !ReferenceEquals(pending.Checkpoint, saved)) return;
        _segmentManifest = pending.Manifest;
        _nextSegmentId = pending.NextId;
        _unsavedChanges.Clear();
        _savedSegmentBoundary = pending.Boundary;
        _savedSegmentedCheckpoint = saved;
        _savedArchive = pending.Archive;
        _pendingSegments = null;
        // The payload no longer needs the boundary document; changes keep being recorded for the next delta.
        _safeDocument = null;
        _safeArchive = null;
    }

    // An island's archive can be journaled by cell only when each cell holds one entry, which is the archive contract
    // (IEvolutionArchiveView.Get returns one entry per cell). A Pareto front, with its exploration pool, is bounded at a
    // few thousand entries and stays in the payload, and so does any archive that breaks the contract.
    private static bool CanSegmentArchives(List<ArchiveDocument> islands, ArchiveState boundary)
    {
        if (boundary.Keys.Length != islands.Count) return false;
        for (int island = 0; island < islands.Count; island++)
        {
            ArchiveDocument document = islands[island];
            if (document.Pareto is not null || document.InfeasibleEntries is not null ||
                !ReferenceEquals(document.Entries, boundary.Entries[island]))
                return false;
            string[] keys = boundary.Keys[island];
            for (int index = 1; index < keys.Length; index++)
                if (string.CompareOrdinal(keys[index - 1], keys[index]) >= 0) return false;
        }
        return true;
    }

    // What each island's archive gained, replaced and lost since the saved archives: a merge of the two ordinal-ordered
    // key lists, so nothing is serialized for an entry that did not change.
    private static List<ArchiveSegmentDocument> ArchiveDelta(ArchiveState saved, ArchiveState current)
    {
        var changes = new List<ArchiveSegmentDocument>();
        for (int island = 0; island < current.Keys.Length; island++)
        {
            string[] beforeKeys = saved.Keys[island], afterKeys = current.Keys[island];
            List<ArchiveEntryDocument> before = saved.Entries[island], after = current.Entries[island];
            var written = new List<ArchiveEntryDocument>();
            var removed = new List<string>();
            int left = 0, right = 0;
            while (left < beforeKeys.Length || right < afterKeys.Length)
            {
                int order = left == beforeKeys.Length ? 1
                    : right == afterKeys.Length ? -1
                    : string.CompareOrdinal(beforeKeys[left], afterKeys[right]);
                if (order < 0)
                {
                    removed.Add(beforeKeys[left++]);
                }
                else if (order > 0)
                {
                    written.Add(after[right++]);
                }
                else
                {
                    if (!ReferenceEquals(before[left], after[right])) written.Add(after[right]);
                    left++;
                    right++;
                }
            }
            if (written.Count > 0 || removed.Count > 0)
                changes.Add(new ArchiveSegmentDocument
                {
                    Island = island,
                    Entries = written,
                    RemovedCells = removed.Count == 0 ? null : removed
                });
        }
        return changes;
    }

    private async Task<string> WriteSegmentAsync(IEvolutionCheckpointSegmentStore store, long id,
        Func<SegmentWriter, Task> write, CancellationToken cancellationToken)
    {
        string? digest = null;
        await store.WriteSegmentAsync(_options.RunId, id, async (stream, token) =>
        {
            using var segment = new SegmentWriter(stream, token);
            await write(segment).ConfigureAwait(false);
            digest = await segment.CompleteAsync().ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return digest ?? throw new InvalidOperationException("The checkpoint store did not write the segment.");
    }

    // The deduplication set and cache as they were at the last boundary: the live ones with later changes undone.
    private BaseSnapshot BoundarySnapshot()
    {
        StateDelta sinceBoundary = StateDelta.From(_safeChanges);
        var seen = new List<string>(_seen.Count);
        foreach (string id in _seen)
            if (!sinceBoundary.Seen.TryGetValue(id, out SeenChange change) || change.Prior) seen.Add(id);
        foreach (KeyValuePair<string, SeenChange> change in sinceBoundary.Seen)
            if (change.Value.Prior && !change.Value.Final) seen.Add(change.Key);

        var cache = new List<KeyValuePair<string, EvolutionTaskResult>>(_cache.Count);
        foreach (KeyValuePair<string, EvolutionTaskResult> entry in _cache)
        {
            if (!sinceBoundary.Cache.TryGetValue(entry.Key, out CacheChange change)) cache.Add(entry);
            else if (change.Prior is { } prior) cache.Add(new KeyValuePair<string, EvolutionTaskResult>(entry.Key, prior));
        }
        foreach (KeyValuePair<string, CacheChange> change in sinceBoundary.Cache)
            if (change.Value.Final is null && change.Value.Prior is { } prior && !_cache.ContainsKey(change.Key))
                cache.Add(new KeyValuePair<string, EvolutionTaskResult>(change.Key, prior));
        // Ordinal order, as the inline payload writes them, so a segment's bytes depend only on the state and not on the
        // history of adds and removes that shaped the hash tables (a resumed run rebuilds them in another order).
        seen.Sort(StringComparer.Ordinal);
        cache.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
        return new BaseSnapshot(seen, cache);
    }

    // The deduplication order at the last boundary. It is bounded by the capacity, so it stays in the payload.
    private List<string> BoundaryDeduplicationOrder()
    {
        var order = new List<string>(_deduplicationOrder);
        for (int i = _safeChanges.Count - 1; i >= 0; i--)
        {
            SafeChange change = _safeChanges[i];
            if (change.Kind == SafeChangeKind.OrderEnqueued) order.RemoveAt(order.Count - 1);
            else if (change.Kind == SafeChangeKind.OrderDequeued) order.Insert(0, change.Key);
        }
        return order;
    }

    private static async Task WriteBase(SegmentWriter segment, BaseSnapshot snapshot, ArchiveState? archive)
    {
        Utf8JsonWriter json = segment.Json;
        json.WriteStartObject();
        json.WriteNumber(nameof(StateSegmentDocument.SchemaVersion), archive is null ? SegmentSchemaVersion : ArchiveSegmentSchemaVersion);
        json.WriteBoolean(nameof(StateSegmentDocument.IsBase), true);
        json.WriteStartArray(nameof(StateSegmentDocument.SeenGenomeIds));
        foreach (string id in snapshot.Seen)
        {
            json.WriteStringValue(id);
            await segment.FlushIfFullAsync().ConfigureAwait(false);
        }
        json.WriteEndArray();
        json.WriteStartArray(nameof(StateSegmentDocument.Cache));
        foreach (KeyValuePair<string, EvolutionTaskResult> entry in snapshot.Cache)
        {
            WriteCacheEntry(json, entry.Key, entry.Value);
            await segment.FlushIfFullAsync().ConfigureAwait(false);
        }
        json.WriteEndArray();
        if (archive is not null)
        {
            json.WriteStartArray(nameof(StateSegmentDocument.Islands));
            for (int island = 0; island < archive.Entries.Length; island++)
                await WriteIsland(segment, island, archive.Entries[island], null).ConfigureAwait(false);
            json.WriteEndArray();
        }
        json.WriteEndObject();
    }

    private static async Task WriteDelta(SegmentWriter segment, StateDelta delta, bool archiveSchema,
        List<ArchiveSegmentDocument>? archive)
    {
        Utf8JsonWriter json = segment.Json;
        json.WriteStartObject();
        json.WriteNumber(nameof(StateSegmentDocument.SchemaVersion), archiveSchema ? ArchiveSegmentSchemaVersion : SegmentSchemaVersion);
        json.WriteBoolean(nameof(StateSegmentDocument.IsBase), false);
        await WriteIds(nameof(StateSegmentDocument.SeenGenomeIds), delta.Seen.Where(pair => !pair.Value.Prior && pair.Value.Final)).ConfigureAwait(false);
        await WriteIds(nameof(StateSegmentDocument.RemovedGenomeIds), delta.Seen.Where(pair => pair.Value.Prior && !pair.Value.Final)).ConfigureAwait(false);
        json.WriteStartArray(nameof(StateSegmentDocument.Cache));
        foreach (KeyValuePair<string, CacheChange> change in delta.Cache.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (change.Value.Final is not { } final || ReferenceEquals(final, change.Value.Prior)) continue;
            WriteCacheEntry(json, change.Key, final);
            await segment.FlushIfFullAsync().ConfigureAwait(false);
        }
        json.WriteEndArray();
        await WriteIds(nameof(StateSegmentDocument.RemovedCacheIds),
            delta.Cache.Where(pair => pair.Value.Final is null && pair.Value.Prior is not null)).ConfigureAwait(false);
        if (archive is { Count: > 0 })
        {
            json.WriteStartArray(nameof(StateSegmentDocument.Islands));
            foreach (ArchiveSegmentDocument island in archive)
                await WriteIsland(segment, island.Island, island.Entries ?? new List<ArchiveEntryDocument>(), island.RemovedCells)
                    .ConfigureAwait(false);
            json.WriteEndArray();
        }
        json.WriteEndObject();

        async Task WriteIds<TValue>(string name, IEnumerable<KeyValuePair<string, TValue>> ids)
        {
            json.WriteStartArray(name);
            foreach (KeyValuePair<string, TValue> id in ids.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                json.WriteStringValue(id.Key);
                await segment.FlushIfFullAsync().ConfigureAwait(false);
            }
            json.WriteEndArray();
        }
    }

    private static async Task WriteIsland(SegmentWriter segment, int island, List<ArchiveEntryDocument> entries,
        List<string>? removedCells)
    {
        Utf8JsonWriter json = segment.Json;
        json.WriteStartObject();
        json.WriteNumber(nameof(ArchiveSegmentDocument.Island), island);
        json.WriteStartArray(nameof(ArchiveSegmentDocument.Entries));
        foreach (ArchiveEntryDocument entry in entries)
        {
            JsonSerializer.Serialize(json, entry, EvolutionStateJsonContext.Default.ArchiveEntryDocument);
            await segment.FlushIfFullAsync().ConfigureAwait(false);
        }
        json.WriteEndArray();
        if (removedCells is not null)
        {
            json.WriteStartArray(nameof(ArchiveSegmentDocument.RemovedCells));
            foreach (string cell in removedCells) json.WriteStringValue(cell);
            json.WriteEndArray();
        }
        json.WriteEndObject();
    }

    private static void WriteCacheEntry(Utf8JsonWriter json, string id, EvolutionTaskResult result) =>
        JsonSerializer.Serialize(json, new CacheDocument { GenomeId = id, Result = TaskResultDocument.From(result) },
            EvolutionStateJsonContext.Default.CacheDocument);

    /// <summary>Rebuilds the state a segmented checkpoint keeps in segments into <paramref name="state"/>.</summary>
    /// <remarks>
    /// The segments are read in the order the payload lists them, each checked against its recorded SHA-256 and applied
    /// strictly: a delta that adds a genome already present, removes one that is absent or empties an unknown cell is
    /// refused. The result goes through the same validation as an inline checkpoint.
    /// </remarks>
    private async Task LoadSegmentsAsync(EvolutionCheckpoint checkpoint, EngineStateDocument state,
        CancellationToken cancellationToken)
    {
        ResetSegments();
        _nextSegmentId = 0;
        if (state.Segments is null)
        {
            if (checkpoint.SegmentIds.Count > 0 || state.NextSegmentId is not null)
                throw new InvalidDataException("The checkpoint names segments its engine state does not list.");
            return;
        }

        if (_checkpointStore is not IEvolutionCheckpointSegmentStore store)
            throw new InvalidDataException(
                "This checkpoint keeps its deduplication set and cache in segments of the store that wrote it, and the configured " +
                "store cannot read segments. Resume from that store (copy its whole directory to move it), or write portable " +
                "checkpoints with EvolutionCheckpointFormat.Inline.");
        LoadedSegments loaded = await ReadSegmentStateAsync(store, _options.RunId, checkpoint, state, cancellationToken)
            .ConfigureAwait(false);
        // The inline checks ran before the segments were read, when the collections were still absent.
        ValidatePackageCheckpointBounds(state);
        if (HasMeasurementOrigins(state) && FeatureVersionOf(state) < EngineMeasurementOriginSchemaVersion)
            throw new InvalidDataException("Measurement-origin metadata requires the versioned checkpoint schema.");
        _segmentManifest = loaded.Manifest;
        _nextSegmentId = loaded.NextId;
        _savedArchive = loaded.Archive;
    }

    // Replays a checkpoint's segments into its state document: the base, then each delta in order.
    private static async Task<LoadedSegments> ReadSegmentStateAsync(IEvolutionCheckpointSegmentStore store, string runId,
        EvolutionCheckpoint checkpoint, EngineStateDocument state, CancellationToken cancellationToken)
    {
        List<SegmentReferenceDocument> manifest = state.Segments
            ?? throw new InvalidDataException("The checkpoint segment list is invalid.");
        if (state.SeenGenomeIds is not null || state.Cache is not null)
            throw new InvalidDataException("A segmented checkpoint also carries an inline deduplication set or cache.");
        if (checkpoint.SegmentIds.Count == 0)
            throw new InvalidDataException(
                "This checkpoint keeps its deduplication set and cache in store segments, but it was copied without the list " +
                "of segments it needs. Resume from the store that wrote it, or write portable checkpoints with " +
                "EvolutionCheckpointFormat.Inline.");
        if (manifest.Count == 0 ||
            manifest.Any(segment => segment is null || segment.Id < 0 || string.IsNullOrWhiteSpace(segment.Sha256)) ||
            !manifest[0].IsBase || manifest.Skip(1).Any(segment => segment.IsBase) ||
            !manifest.Select(segment => segment.Id).SequenceEqual(checkpoint.SegmentIds) ||
            state.NextSegmentId is not { } nextId || nextId <= manifest[manifest.Count - 1].Id)
            throw new InvalidDataException("The checkpoint segment list is invalid.");

        List<ArchiveDocument> islands = state.Islands ?? throw new InvalidDataException("Checkpoint islands are missing.");
        bool archiveInSegments = islands.Any(island => island is not null && island.EntriesInSegments);
        if (archiveInSegments && islands.Any(island => island is null || !island.EntriesInSegments))
            throw new InvalidDataException("Only some of the checkpoint's island archives are in its segments.");
        int expectedSchema = archiveInSegments ? ArchiveSegmentSchemaVersion : SegmentSchemaVersion;
        Dictionary<string, ArchiveEntryDocument>[] cells = archiveInSegments
            ? islands.Select(_ => new Dictionary<string, ArchiveEntryDocument>(StringComparer.Ordinal)).ToArray()
            : Array.Empty<Dictionary<string, ArchiveEntryDocument>>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cache = new Dictionary<string, CacheDocument>(StringComparer.Ordinal);
        foreach (SegmentReferenceDocument reference in manifest)
        {
            StateSegmentDocument segment = await ReadSegmentAsync(store, runId, reference, cancellationToken).ConfigureAwait(false);
            if (segment.SchemaVersion != expectedSchema || segment.IsBase != reference.IsBase)
                throw new InvalidDataException("A checkpoint segment has an unexpected schema or kind.");
            foreach (string id in segment.RemovedGenomeIds ?? new List<string>())
                if (id is null || !seen.Remove(id)) throw new InvalidDataException("A checkpoint segment removes an unknown genome.");
            foreach (string id in segment.SeenGenomeIds ?? new List<string>())
                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id)) throw new InvalidDataException("A checkpoint segment adds a genome twice.");
            foreach (string id in segment.RemovedCacheIds ?? new List<string>())
                if (id is null || !cache.Remove(id)) throw new InvalidDataException("A checkpoint segment removes an unknown cache entry.");
            foreach (CacheDocument entry in segment.Cache ?? new List<CacheDocument>())
            {
                if (entry is null || string.IsNullOrWhiteSpace(entry.GenomeId) || (reference.IsBase && cache.ContainsKey(entry.GenomeId)))
                    throw new InvalidDataException("A checkpoint segment has an invalid cache entry.");
                cache[entry.GenomeId] = entry;
            }
            ApplyArchiveSegment(segment, reference.IsBase, cells);
        }

        state.SeenGenomeIds = seen.ToList();
        state.Cache = cache.Values.ToList();
        ArchiveState? archive = null;
        if (archiveInSegments)
        {
            // Ordinal cell order is the order an inline payload lists entries in, so the rebuilt state is the same document.
            var keys = new string[cells.Length][];
            var entries = new List<ArchiveEntryDocument>[cells.Length];
            for (int island = 0; island < cells.Length; island++)
            {
                keys[island] = cells[island].Keys.ToArray();
                Array.Sort(keys[island], StringComparer.Ordinal);
                entries[island] = new List<ArchiveEntryDocument>(keys[island].Length);
                foreach (string key in keys[island]) entries[island].Add(cells[island][key]);
                islands[island].Entries = entries[island];
                islands[island].EntriesInSegments = false;
            }
            archive = new ArchiveState(keys, entries);
        }
        return new LoadedSegments(new List<SegmentReferenceDocument>(manifest), nextId, archive);
    }

    // Applies one segment's archive changes. A base restates every island; a delta writes cells (an entry replaces what
    // its cell held) and empties cells that must already hold an entry.
    private static void ApplyArchiveSegment(StateSegmentDocument segment, bool isBase, Dictionary<string, ArchiveEntryDocument>[] cells)
    {
        if (segment.Islands is not { } islands)
        {
            if (isBase && cells.Length > 0) throw new InvalidDataException("A checkpoint base segment is missing its island archives.");
            return;
        }
        if (cells.Length == 0)
            throw new InvalidDataException("A checkpoint segment carries island archives its payload does not expect.");
        if (isBase && islands.Count != cells.Length)
            throw new InvalidDataException("A checkpoint base segment does not list every island archive.");
        var touched = new HashSet<int>();
        foreach (ArchiveSegmentDocument island in islands)
        {
            if (island is null || island.Island < 0 || island.Island >= cells.Length || !touched.Add(island.Island))
                throw new InvalidDataException("A checkpoint segment names an invalid island archive.");
            Dictionary<string, ArchiveEntryDocument> map = cells[island.Island];
            if (isBase && island.RemovedCells is not null)
                throw new InvalidDataException("A checkpoint base segment empties archive cells.");
            foreach (string cell in island.RemovedCells ?? new List<string>())
                if (cell is null || !map.Remove(cell)) throw new InvalidDataException("A checkpoint segment empties an unknown archive cell.");
            foreach (ArchiveEntryDocument entry in island.Entries ?? new List<ArchiveEntryDocument>())
            {
                if (entry?.CellBins is null) throw new InvalidDataException("An archive entry is incomplete.");
                string key = CellKeyOf(entry.CellBins);
                if (isBase && map.ContainsKey(key))
                    throw new InvalidDataException("A checkpoint segment fills one archive cell twice.");
                map[key] = entry;
            }
        }
    }

    private static string CellKeyOf(int[] bins)
    {
        try
        {
            return new EvolutionCellKey(bins).StableKey;
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("A checkpoint archive entry has an invalid cell.", exception);
        }
    }

    private static async Task<StateSegmentDocument> ReadSegmentAsync(IEvolutionCheckpointSegmentStore store, string runId,
        SegmentReferenceDocument reference, CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (Stream stream = await store.OpenSegmentAsync(runId, reference.Id, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException($"Checkpoint segment {reference.Id} is missing from the store."))
            bytes = await ReadSegmentBytesAsync(stream, reference.Id, cancellationToken).ConfigureAwait(false);

        using (IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            hash.AppendData(bytes);
            if (!string.Equals(ToHex(hash.GetHashAndReset()), reference.Sha256, StringComparison.Ordinal))
                throw new InvalidDataException($"Checkpoint segment {reference.Id} failed its SHA-256 check.");
        }

        EvolutionCheckpointJsonPreflight.Validate(bytes);
        try
        {
            return JsonSerializer.Deserialize(bytes, EvolutionStateJsonContext.Default.StateSegmentDocument)
                ?? throw new InvalidDataException($"Checkpoint segment {reference.Id} is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Checkpoint segment {reference.Id} is invalid.", exception);
        }
    }

    // A segment's bytes, read straight into an array of the stream's length when it reports one: growing a MemoryStream
    // and copying it out allocated about three times each segment, the largest share of a resume's allocation (E-1). A
    // stream that turns out longer or shorter than it reported still yields exactly the bytes it holds, so the SHA-256
    // check sees what the store returned.
    private static async Task<byte[]> ReadSegmentBytesAsync(Stream stream, long id, CancellationToken cancellationToken)
    {
        long expected = -1;
        if (stream.CanSeek)
        {
            try
            {
                expected = stream.Length - stream.Position;
            }
            catch (NotSupportedException)
            {
                expected = -1;
            }
        }
        if (expected > EvolutionCollectionLimits.MaximumCheckpointBytes)
            throw new InvalidDataException($"Checkpoint segment {id} exceeds the package limit.");

        // Every byte is overwritten before it is read, so on runtimes that allow it the array is not zeroed first.
#if NET5_0_OR_GREATER
        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)(expected > 0 ? expected : SegmentFlushBytes));
#else
        byte[] bytes = new byte[expected > 0 ? expected : SegmentFlushBytes];
#endif
        int length = 0;
        while (true)
        {
            if (length == bytes.Length)
            {
                // Full: either the reported length was exact (the next read returns nothing) or the stream holds more.
                var probe = new byte[1];
                if (await stream.ReadAsync(probe, 0, 1, cancellationToken).ConfigureAwait(false) == 0) break;
                if ((long)length + 1 > EvolutionCollectionLimits.MaximumCheckpointBytes)
                    throw new InvalidDataException($"Checkpoint segment {id} exceeds the package limit.");
                long grown = Math.Min(Math.Max((long)bytes.Length * 2, SegmentFlushBytes),
                    (long)EvolutionCollectionLimits.MaximumCheckpointBytes);
                Array.Resize(ref bytes, (int)Math.Max(grown, (long)length + 1));
                bytes[length++] = probe[0];
                continue;
            }
            int read = await stream.ReadAsync(bytes, length, bytes.Length - length, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length != bytes.Length) Array.Resize(ref bytes, length);
        return bytes;
    }

    // The version an inline payload of this document would carry: measurement origins, then Pareto, then constraints.
    private static int FeatureSchemaVersion(EngineStateDocument document)
    {
        int version = HasMeasurementOrigins(document) ? EngineMeasurementOriginSchemaVersion : EngineStateSchemaVersion;
        IReadOnlyList<ArchiveDocument> islands = document.Islands ?? new List<ArchiveDocument>();
        if (islands.Any(island => island.Pareto is not null)) version = EngineParetoSchemaVersion;
        if (islands.Any(island => island.Pareto?.ConstraintCount is not null)) version = EngineParetoConstraintSchemaVersion;
        return version;
    }

    private static int FeatureVersionOf(EngineStateDocument document) => document.SchemaVersion - SchemaOffsetOf(document.SchemaVersion);

    // The offset a payload version carries: none inline, one for segments, a larger one when the archives are in them too.
    private static int SchemaOffsetOf(int schemaVersion) =>
        schemaVersion >= EngineArchiveSegmentedSchemaVersion ? ArchiveSegmentedSchemaOffset
        : schemaVersion >= EngineSegmentedSchemaVersion ? SegmentedSchemaOffset
        : 0;

    private static string ToHex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) text.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private sealed record PendingSegments(EvolutionCheckpoint Checkpoint, List<SegmentReferenceDocument> Manifest, long NextId,
        long Boundary, ArchiveState? Archive);

    private sealed record LoadedSegments(List<SegmentReferenceDocument> Manifest, long NextId, ArchiveState? Archive);

    // Each island's archive at one boundary: its cell keys in ordinal order, and the entry documents in the same order.
    private sealed record ArchiveState(string[][] Keys, List<ArchiveEntryDocument>[] Entries);

    private sealed record BaseSnapshot(List<string> Seen, List<KeyValuePair<string, EvolutionTaskResult>> Cache);

    private readonly record struct SeenChange(bool Prior, bool Final);

    private readonly record struct CacheChange(EvolutionTaskResult? Prior, EvolutionTaskResult? Final);

    // The net effect of a run of changes on each key: its state before the first change and after the last.
    private readonly struct StateDelta
    {
        private StateDelta(Dictionary<string, SeenChange> seen, Dictionary<string, CacheChange> cache)
        {
            Seen = seen;
            Cache = cache;
        }

        public Dictionary<string, SeenChange> Seen { get; }
        public Dictionary<string, CacheChange> Cache { get; }

        public bool IsEmpty => Seen.Values.All(change => change.Prior == change.Final) &&
                               Cache.Values.All(change => ReferenceEquals(change.Prior, change.Final));

        public static StateDelta From(IReadOnlyList<SafeChange> changes)
        {
            var seen = new Dictionary<string, SeenChange>(StringComparer.Ordinal);
            var cache = new Dictionary<string, CacheChange>(StringComparer.Ordinal);
            foreach (SafeChange change in changes)
            {
                switch (change.Kind)
                {
                    case SafeChangeKind.SeenAdded:
                        seen[change.Key] = new SeenChange(seen.TryGetValue(change.Key, out SeenChange added) && added.Prior, true);
                        break;
                    case SafeChangeKind.SeenRemoved:
                        seen[change.Key] = new SeenChange(!seen.TryGetValue(change.Key, out SeenChange removed) || removed.Prior, false);
                        break;
                    case SafeChangeKind.CacheSet:
                        cache[change.Key] = new CacheChange(cache.TryGetValue(change.Key, out CacheChange set) ? set.Prior : change.Previous, change.Value);
                        break;
                    case SafeChangeKind.CacheRemoved:
                        cache[change.Key] = new CacheChange(cache.TryGetValue(change.Key, out CacheChange gone) ? gone.Prior : change.Previous, null);
                        break;
                }
            }
            return new StateDelta(seen, cache);
        }
    }

    // A JSON writer that drains to the store's stream in small chunks and hashes exactly the bytes it writes.
    private sealed class SegmentWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly CancellationToken _cancellationToken;
        private readonly MemoryStream _chunk = new();
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _written;

        public SegmentWriter(Stream stream, CancellationToken cancellationToken)
        {
            _stream = stream;
            _cancellationToken = cancellationToken;
            Json = new Utf8JsonWriter(_chunk);
        }

        public Utf8JsonWriter Json { get; }

        public async Task FlushIfFullAsync()
        {
            if (Json.BytesPending + _chunk.Length >= SegmentFlushBytes) await DrainAsync().ConfigureAwait(false);
        }

        public async Task<string> CompleteAsync()
        {
            await DrainAsync().ConfigureAwait(false);
            return ToHex(_hash.GetHashAndReset());
        }

        private async Task DrainAsync()
        {
            Json.Flush();
            int length = (int)_chunk.Length;
            if (length == 0) return;
            // Resume refuses a segment above the package limit, so a save must refuse to write one.
            _written += length;
            if (_written > EvolutionCollectionLimits.MaximumCheckpointBytes)
                throw new InvalidDataException(
                    $"A checkpoint segment exceeds the {EvolutionCollectionLimits.MaximumCheckpointBytes}-byte package limit.");
            byte[] buffer = _chunk.GetBuffer();
            _hash.AppendData(buffer, 0, length);
            await _stream.WriteAsync(buffer, 0, length, _cancellationToken).ConfigureAwait(false);
            _chunk.SetLength(0);
        }

        public void Dispose()
        {
            Json.Dispose();
            _chunk.Dispose();
            _hash.Dispose();
        }
    }
}
