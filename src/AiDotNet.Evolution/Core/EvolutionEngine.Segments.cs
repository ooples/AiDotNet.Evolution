using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using static AiDotNet.Evolution.EvolutionEngineDocuments;

namespace AiDotNet.Evolution;

// Segmented checkpoints (V1-73, #178). With a store that implements IEvolutionCheckpointSegmentStore, the deduplication
// set and the evaluation cache leave the checkpoint payload. Each save writes one segment holding only what changed
// since the previous save, and every SegmentCompactionInterval segments a base segment restates both, so a save costs
// what changed rather than everything the run has seen. The payload names the segments, with their SHA-256, that rebuild
// the two structures on resume. Segments are streamed through a small buffer, so no save holds the whole state at once.
public sealed partial class EvolutionEngine<TGenome>
{
    private const int SegmentCompactionInterval = 32;
    private const int SegmentSchemaVersion = 1;
    private const int SegmentFlushBytes = 64 * 1024;

    // The segments the last saved checkpoint names (a base first) and the identifier the next segment takes.
    private List<SegmentReferenceDocument> _segmentManifest = new();
    private long _nextSegmentId;
    // Changes between the last saved boundary and the current one: the next delta segment.
    private readonly List<SafeChange> _unsavedChanges = new();
    // What a save prepared; it becomes current only once the store has accepted the checkpoint that names it.
    private PendingSegments? _pendingSegments;
    private long _savedSegmentBoundary = -1;
    private EvolutionCheckpoint? _savedSegmentedCheckpoint;

    // The segment store to save through, or null for an inline checkpoint. Auto switches once the run remembers enough
    // genomes for a full save to cost more than a delta, and stays segmented for the rest of the run.
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
                       (_segmentManifest.Count > 0 || (long)_seen.Count + _cache.Count >= _options.CheckpointSegmentThreshold)
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
    }

    private async Task<EvolutionCheckpoint?> PrepareSegmentedCheckpointAsync(IEvolutionCheckpointSegmentStore store,
        CancellationToken cancellationToken)
    {
        // A boundary that was already saved is saved again as the same checkpoint, which a store treats as a no-op.
        if (_savedSegmentBoundary == _safeSequence && _savedSegmentedCheckpoint is not null) return _savedSegmentedCheckpoint;
        if (_safeDocument is not { } document) return null;

        // Everything a segment needs is taken before the first await: evaluations may add to the live set meanwhile.
        bool writeBase = _segmentManifest.Count == 0 || _segmentManifest.Count >= SegmentCompactionInterval;
        var manifest = writeBase ? new List<SegmentReferenceDocument>() : new List<SegmentReferenceDocument>(_segmentManifest);
        long nextId = _nextSegmentId;
        Func<SegmentWriter, Task>? write = null;
        if (writeBase)
        {
            BaseSnapshot snapshot = BoundarySnapshot();
            write = segment => WriteBase(segment, snapshot);
        }
        else
        {
            StateDelta delta = StateDelta.From(_unsavedChanges);
            if (!delta.IsEmpty) write = segment => WriteDelta(segment, delta);
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
        document.SchemaVersion = FeatureSchemaVersion(document) + SegmentedSchemaOffset;
        string payload = JsonSerializer.Serialize(document, EvolutionStateJsonContext.Default.EngineStateDocument);
        if (payload.Length > EvolutionCollectionLimits.MaximumCheckpointBytes ||
            Encoding.UTF8.GetByteCount(payload) > EvolutionCollectionLimits.MaximumCheckpointBytes)
            throw new InvalidDataException(
                $"The evolution engine state exceeds the {EvolutionCollectionLimits.MaximumCheckpointBytes}-byte package limit.");

        EvolutionCheckpoint checkpoint = new EvolutionCheckpoint(_options.RunId, _safeSequence, _compatibilityHash, payload,
                EvolutionCheckpoint.CurrentSchemaVersion, BestQualityAcrossIslands(), _islands[0].Direction)
            .WithSegmentIds(manifest.Select(segment => segment.Id).ToList());
        _pendingSegments = new PendingSegments(checkpoint, manifest, nextId, _safeSequence);
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
        _pendingSegments = null;
        // The payload no longer needs the boundary document; changes keep being recorded for the next delta.
        _safeDocument = null;
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

    private static async Task WriteBase(SegmentWriter segment, BaseSnapshot snapshot)
    {
        Utf8JsonWriter json = segment.Json;
        json.WriteStartObject();
        json.WriteNumber(nameof(StateSegmentDocument.SchemaVersion), SegmentSchemaVersion);
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
        json.WriteEndObject();
    }

    private static async Task WriteDelta(SegmentWriter segment, StateDelta delta)
    {
        Utf8JsonWriter json = segment.Json;
        json.WriteStartObject();
        json.WriteNumber(nameof(StateSegmentDocument.SchemaVersion), SegmentSchemaVersion);
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

    private static void WriteCacheEntry(Utf8JsonWriter json, string id, EvolutionTaskResult result) =>
        JsonSerializer.Serialize(json, new CacheDocument { GenomeId = id, Result = TaskResultDocument.From(result) },
            EvolutionStateJsonContext.Default.CacheDocument);

    /// <summary>Rebuilds the deduplication set and cache of a segmented checkpoint into <paramref name="state"/>.</summary>
    /// <remarks>
    /// The segments are read in the order the payload lists them, each checked against its recorded SHA-256 and applied
    /// strictly: a delta that adds a genome already present or removes one that is absent is refused. The result goes
    /// through the same validation as an inline deduplication set and cache.
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
        List<SegmentReferenceDocument> manifest = state.Segments;
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

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cache = new Dictionary<string, CacheDocument>(StringComparer.Ordinal);
        foreach (SegmentReferenceDocument reference in manifest)
        {
            StateSegmentDocument segment = await ReadSegmentAsync(store, reference, cancellationToken).ConfigureAwait(false);
            if (segment.SchemaVersion != SegmentSchemaVersion || segment.IsBase != reference.IsBase)
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
        }

        state.SeenGenomeIds = seen.ToList();
        state.Cache = cache.Values.ToList();
        // The inline checks ran before the segments were read, when both collections were still absent.
        ValidatePackageCheckpointBounds(state);
        if (HasMeasurementOrigins(state) && FeatureVersionOf(state) < EngineMeasurementOriginSchemaVersion)
            throw new InvalidDataException("Measurement-origin metadata requires the versioned checkpoint schema.");
        _segmentManifest = new List<SegmentReferenceDocument>(manifest);
        _nextSegmentId = nextId;
    }

    private async Task<StateSegmentDocument> ReadSegmentAsync(IEvolutionCheckpointSegmentStore store,
        SegmentReferenceDocument reference, CancellationToken cancellationToken)
    {
        byte[] bytes;
        using (Stream stream = await store.OpenSegmentAsync(_options.RunId, reference.Id, cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException($"Checkpoint segment {reference.Id} is missing from the store."))
        using (var buffer = new MemoryStream())
        {
            byte[] chunk = new byte[SegmentFlushBytes];
            int read;
            while ((read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > EvolutionCollectionLimits.MaximumCheckpointBytes)
                    throw new InvalidDataException($"Checkpoint segment {reference.Id} exceeds the package limit.");
                buffer.Write(chunk, 0, read);
            }
            bytes = buffer.ToArray();
        }

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

    // The version an inline payload of this document would carry: measurement origins, then Pareto, then constraints.
    private static int FeatureSchemaVersion(EngineStateDocument document)
    {
        int version = HasMeasurementOrigins(document) ? EngineMeasurementOriginSchemaVersion : EngineStateSchemaVersion;
        IReadOnlyList<ArchiveDocument> islands = document.Islands ?? new List<ArchiveDocument>();
        if (islands.Any(island => island.Pareto is not null)) version = EngineParetoSchemaVersion;
        if (islands.Any(island => island.Pareto?.ConstraintCount is not null)) version = EngineParetoConstraintSchemaVersion;
        return version;
    }

    private static int FeatureVersionOf(EngineStateDocument document) =>
        document.SchemaVersion >= EngineSegmentedSchemaVersion ? document.SchemaVersion - SegmentedSchemaOffset : document.SchemaVersion;

    private static string ToHex(byte[] bytes)
    {
        var text = new StringBuilder(bytes.Length * 2);
        foreach (byte value in bytes) text.Append(value.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return text.ToString();
    }

    private sealed record PendingSegments(EvolutionCheckpoint Checkpoint, List<SegmentReferenceDocument> Manifest, long NextId,
        long Boundary);

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