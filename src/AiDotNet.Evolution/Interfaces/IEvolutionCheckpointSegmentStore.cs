namespace AiDotNet.Evolution;

/// <summary>A checkpoint store that also keeps append-only state segments beside its checkpoints.</summary>
/// <remarks>
/// <para>
/// When its store implements this interface, the engine writes a run's deduplication set and evaluation cache as
/// segments rather than inside every checkpoint payload. Each save appends one segment holding only what changed since
/// the previous save, and a periodic base segment restates the whole set, so a save costs what changed rather than
/// everything the run has seen. The checkpoint names the segments it needs in
/// <see cref="EvolutionCheckpoint.SegmentIds"/>; the engine verifies each segment's SHA-256, recorded in its own payload,
/// when it resumes.
/// </para>
/// <para>
/// A store must write a segment atomically, so a crash leaves either the whole segment or none of it, and must keep
/// every segment a checkpoint it still retains names. It may delete any other segment once a later checkpoint has been
/// saved. Segment identifiers increase within a run; rewriting an identifier replaces its contents.
/// </para>
/// </remarks>
public interface IEvolutionCheckpointSegmentStore : IEvolutionCheckpointStore
{
    /// <summary>Writes one segment atomically.</summary>
    /// <param name="runId">The run the segment belongs to.</param>
    /// <param name="segmentId">The segment's identifier within the run; zero or greater.</param>
    /// <param name="write">Writes the segment's bytes to the stream it is given.</param>
    /// <param name="cancellationToken">Cancels the write before it is committed.</param>
    /// <returns>A task that completes once the segment is durably stored.</returns>
    Task WriteSegmentAsync(string runId, long segmentId, Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default);

    /// <summary>Opens a segment for reading.</summary>
    /// <param name="runId">The run the segment belongs to.</param>
    /// <param name="segmentId">The segment's identifier within the run.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>A readable stream the caller disposes, or <c>null</c> when the segment does not exist.</returns>
    Task<Stream?> OpenSegmentAsync(string runId, long segmentId, CancellationToken cancellationToken = default);
}