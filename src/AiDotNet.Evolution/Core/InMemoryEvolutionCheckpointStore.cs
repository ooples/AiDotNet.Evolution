
namespace AiDotNet.Evolution;

/// <summary>A thread-safe in-memory checkpoint store intended for tests and short-lived applications.</summary>
/// <remarks>
/// <para>
/// The store keeps the latest <see cref="EvolutionCheckpoint"/> per run id in a dictionary guarded by a single lock.
/// <see cref="SaveAsync"/> validates the checkpoint's schema and checksum, then enforces the same monotonic contract
/// a durable store must honor: a run cannot change its compatibility hash, its sequence cannot move backwards, and one
/// sequence cannot be published with two different checksums. Saving a checkpoint whose sequence and checksum equal
/// the stored ones is an idempotent no-op, which is what makes retries after a transient failure safe. Checkpoints
/// are cloned on the way in and on the way out, so callers never share an instance with the store.
/// </para>
/// <para>
/// Nothing survives the process: use <see cref="JsonEvolutionCheckpointStore"/> when a run must be resumable after a
/// restart. Both operations complete synchronously and cost O(1) apart from the clone, so this store adds no
/// measurable overhead to a test run.
/// </para>
/// <para><b>For Beginners:</b> A checkpoint store is where the evolution engine saves its progress so that a long
/// search can be resumed later instead of restarted from scratch. This implementation saves that progress in memory
/// only, so it disappears when your program ends, which is exactly what you want in a unit test or a quick experiment
/// where the checkpointing code path needs to run but durability does not matter. For example, a test can run an
/// engine for a few generations with this store, build a second engine that points at the same store and run id, and
/// assert that the second engine resumes from the saved state. For anything that must survive a crash or reboot,
/// choose the JSON file-backed store instead.</para>
/// </remarks>
public sealed class InMemoryEvolutionCheckpointStore : IEvolutionCheckpointSegmentStore
{
    // Segments written but not yet named by a saved checkpoint are bounded, so repeated failed saves cannot grow a run.
    private const int MaximumSegmentsPerRun = 2 * EvolutionCheckpoint.MaximumSegmentCount;
    private readonly object _gate = new();
    private readonly Dictionary<string, EvolutionCheckpoint> _checkpoints = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SortedDictionary<long, byte[]>> _segments = new(StringComparer.Ordinal);
    private readonly int _capacity;

    /// <summary>Initializes a bounded in-memory store.</summary>
    /// <param name="capacity">Maximum number of distinct run identifiers retained at once.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public InMemoryEvolutionCheckpointStore(int capacity = 1024)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    /// <summary>Gets the maximum number of distinct runs this store retains.</summary>
    public int Capacity => _capacity;

    /// <summary>Removes one run so its capacity can be reused after the run is complete.</summary>
    public bool Remove(string runId)
    {
        Guard.NotNullOrWhiteSpace(runId);
        lock (_gate)
        {
            _segments.Remove(runId.Trim());
            return _checkpoints.Remove(runId.Trim());
        }
    }

    /// <inheritdoc/>
    public Task SaveAsync(EvolutionCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();
        checkpoint.Validate();
        lock (_gate)
        {
            if (_checkpoints.TryGetValue(checkpoint.RunId, out EvolutionCheckpoint? existing))
            {
                ValidateSuccessor(existing, checkpoint);
                if (checkpoint.Sequence == existing.Sequence) return Task.CompletedTask;
            }
            else if (_checkpoints.Count >= _capacity)
            {
                throw new InvalidOperationException(
                    "The in-memory checkpoint store is at capacity. Remove a completed run before adding another.");
            }
            _checkpoints[checkpoint.RunId] = checkpoint.Clone();
            // Only the latest checkpoint is kept, so only the segments it names are still needed.
            if (_segments.TryGetValue(checkpoint.RunId, out SortedDictionary<long, byte[]>? segments))
            {
                var named = new HashSet<long>(checkpoint.SegmentIds);
                foreach (long id in segments.Keys.Where(id => !named.Contains(id)).ToList()) segments.Remove(id);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async Task WriteSegmentAsync(string runId, long segmentId, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(runId);
        Guard.NotNull(write);
        if (segmentId < 0) throw new ArgumentOutOfRangeException(nameof(segmentId));
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        await write(buffer, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = buffer.ToArray();
        string run = runId.Trim();
        lock (_gate)
        {
            if (!_segments.TryGetValue(run, out SortedDictionary<long, byte[]>? segments))
            {
                segments = new SortedDictionary<long, byte[]>();
                _segments[run] = segments;
            }
            if (!segments.ContainsKey(segmentId) && segments.Count >= MaximumSegmentsPerRun)
                throw new InvalidOperationException("The in-memory store holds too many unreferenced segments for this run.");
            segments[segmentId] = bytes;
        }
    }

    /// <inheritdoc/>
    public Task<Stream?> OpenSegmentAsync(string runId, long segmentId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Stream? stream = _segments.TryGetValue(runId.Trim(), out SortedDictionary<long, byte[]>? segments) &&
                segments.TryGetValue(segmentId, out byte[]? bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null;
            return Task.FromResult(stream);
        }
    }

    /// <inheritdoc/>
    public Task<EvolutionCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default)
    {
        Guard.NotNullOrWhiteSpace(runId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            EvolutionCheckpoint? result = _checkpoints.TryGetValue(runId, out EvolutionCheckpoint? checkpoint)
                ? checkpoint.Clone()
                : null;
            return Task.FromResult(result);
        }
    }

    private static void ValidateSuccessor(EvolutionCheckpoint existing, EvolutionCheckpoint checkpoint)
    {
        if (!string.Equals(existing.CompatibilityHash, checkpoint.CompatibilityHash, StringComparison.Ordinal))
            throw new InvalidOperationException("A checkpoint run cannot change compatibility identity.");
        if (checkpoint.Sequence < existing.Sequence)
            throw new InvalidOperationException("A checkpoint store cannot move a run backwards.");
        if (checkpoint.Sequence == existing.Sequence &&
            !string.Equals(existing.Checksum, checkpoint.Checksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A checkpoint sequence cannot identify two different states.");
        }
    }
}
