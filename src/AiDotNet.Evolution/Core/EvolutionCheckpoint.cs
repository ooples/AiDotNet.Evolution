
namespace AiDotNet.Evolution;

/// <summary>A versioned, checksummed, opaque engine snapshot.</summary>
/// <remarks>
/// <para>
/// A checkpoint couples an engine-owned serialized <see cref="Payload"/> with the metadata a store needs to
/// order and validate it: the <see cref="RunId"/> it belongs to, a monotonically increasing
/// <see cref="Sequence"/>, the <see cref="CompatibilityHash"/> of every component that affects resume
/// semantics (such as the task and evaluator versions and the engine configuration), a
/// <see cref="SchemaVersion"/>, and a lowercase hexadecimal SHA-256 <see cref="Checksum"/> of the payload that
/// the public constructor computes. <see cref="Validate"/> recomputes the checksum and rejects unsupported
/// schema versions, which lets a store detect truncated or tampered data before the engine attempts to
/// deserialize the payload. Instances are immutable; identifier and hash strings are trimmed, while the payload
/// is preserved exactly because its checksum depends on every character.
/// </para>
/// <para><b>For Beginners:</b> Think of a checkpoint as a sealed envelope containing a save file. The engine
/// writes the save data inside (the payload) and stamps the outside with the run name, a sequence number so
/// the newest envelope is unambiguous, a fingerprint of the configuration that produced it, and a checksum that
/// proves the contents have not been altered or cut short. You normally never open the envelope yourself: the
/// engine creates checkpoints, an <see cref="AiDotNet.Evolution.IEvolutionCheckpointStore"/> files them away,
/// and on resume the engine checks that the fingerprint matches its current configuration before trusting the
/// contents. If the fingerprint differs, for example because you changed the evaluator or the seed, the old save
/// is refused so you never silently continue a run under different rules.</para>
/// </remarks>
public sealed class EvolutionCheckpoint
{
    /// <summary>The current public checkpoint schema.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The most segments one checkpoint may name.</summary>
    public const int MaximumSegmentCount = 4096;

    private static readonly IReadOnlyList<long> NoSegments = Array.AsReadOnly(Array.Empty<long>());

    /// <summary>Initializes a checkpoint and computes its payload checksum.</summary>
    /// <param name="runId">The stable run identifier.</param>
    /// <param name="sequence">The monotonically increasing committed-state sequence.</param>
    /// <param name="compatibilityHash">Hash of every component that affects resume semantics.</param>
    /// <param name="payload">The engine-owned serialized state.</param>
    /// <param name="schemaVersion">The checkpoint schema version.</param>
    /// <param name="quality">
    /// The best elite quality this snapshot holds, or <see langword="null"/> when the run had produced no elite yet.
    /// </param>
    /// <param name="qualityDirection">The direction <paramref name="quality"/> is better in.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sequence"/> or <paramref name="schemaVersion"/> is out of range, <paramref name="quality"/> is
    /// not finite, or <paramref name="qualityDirection"/> is undefined.
    /// </exception>
    public EvolutionCheckpoint(string runId, long sequence, string compatibilityHash, string payload,
        int schemaVersion = CurrentSchemaVersion, double? quality = null,
        EvolutionOptimizationDirection qualityDirection = EvolutionOptimizationDirection.Maximize)
        : this(runId, sequence, compatibilityHash, payload, EvolutionHash.Compute(payload), schemaVersion, quality,
            qualityDirection, NoSegments)
    {
    }

    internal EvolutionCheckpoint(string runId, long sequence, string compatibilityHash, string payload,
        string checksum, int schemaVersion, double? quality = null,
        EvolutionOptimizationDirection qualityDirection = EvolutionOptimizationDirection.Maximize,
        IReadOnlyList<long>? segmentIds = null)
    {
        Guard.NotNullOrWhiteSpace(runId);
        Guard.NotNullOrWhiteSpace(compatibilityHash);
        Guard.NotNull(payload);
        Guard.NotNullOrWhiteSpace(checksum);
        if (sequence < 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (schemaVersion <= 0) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        if (quality.HasValue && (double.IsNaN(quality.Value) || double.IsInfinity(quality.Value)))
            throw new ArgumentOutOfRangeException(nameof(quality), "A checkpoint quality must be finite.");
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), qualityDirection))
            throw new ArgumentOutOfRangeException(nameof(qualityDirection));
        RunId = runId.Trim();
        Sequence = sequence;
        CompatibilityHash = compatibilityHash.Trim();
        Payload = payload;
        Checksum = checksum.Trim();
        SchemaVersion = schemaVersion;
        Quality = quality;
        QualityDirection = qualityDirection;
        SegmentIds = segmentIds is null ? NoSegments : CopySegmentIds(segmentIds);
    }

    private static IReadOnlyList<long> CopySegmentIds(IReadOnlyList<long> segmentIds)
    {
        if (segmentIds.Count == 0) return NoSegments;
        if (segmentIds.Count > MaximumSegmentCount)
            throw new ArgumentOutOfRangeException(nameof(segmentIds), $"A checkpoint names at most {MaximumSegmentCount} segments.");
        var copy = new long[segmentIds.Count];
        for (int i = 0; i < copy.Length; i++)
        {
            copy[i] = segmentIds[i];
            if (copy[i] < 0 || (i > 0 && copy[i] <= copy[i - 1]))
                throw new ArgumentOutOfRangeException(nameof(segmentIds), "Segment identifiers must be non-negative and strictly increasing.");
        }
        return Array.AsReadOnly(copy);
    }

    /// <summary>Gets the schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the run identifier.</summary>
    public string RunId { get; }

    /// <summary>Gets the committed-state sequence.</summary>
    public long Sequence { get; }

    /// <summary>Gets the resume compatibility hash.</summary>
    public string CompatibilityHash { get; }

    /// <summary>Gets the engine-owned serialized payload.</summary>
    public string Payload { get; }

    /// <summary>Gets the lowercase SHA-256 checksum of <see cref="Payload"/>.</summary>
    public string Checksum { get; }

    /// <summary>Gets the best elite quality this snapshot holds, or <c>null</c> when it holds none.</summary>
    /// <remarks>
    /// This is provenance the engine stamps on the envelope so a store can rank snapshots without opening the opaque
    /// payload. <c>DirectoryEvolutionCheckpointStore</c> uses it for its keep-best retention, and it is what lets a
    /// listing show how good each retained snapshot was.
    /// </remarks>
    public double? Quality { get; }

    /// <summary>Gets the direction in which a larger <see cref="Quality"/> is better.</summary>
    public EvolutionOptimizationDirection QualityDirection { get; }

    /// <summary>Gets the store segments this checkpoint needs, in increasing order; empty when it needs none.</summary>
    public IReadOnlyList<long> SegmentIds { get; }

    /// <summary>Verifies the schema and payload checksum.</summary>
    /// <exception cref="InvalidDataException">The schema or checksum is invalid.</exception>
    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Unsupported evolution checkpoint schema {SchemaVersion}.");
        string actual = EvolutionHash.Compute(Payload);
        if (!string.Equals(actual, Checksum, StringComparison.Ordinal))
            throw new InvalidDataException("Evolution checkpoint checksum validation failed.");
    }

    /// <summary>Returns a copy of this checkpoint that names the given store segments.</summary>
    /// <param name="segmentIds">
    /// The segments of an <see cref="IEvolutionCheckpointSegmentStore"/> the checkpoint needs, in increasing order. A
    /// store keeps every segment a retained checkpoint names, and one that reloads a checkpoint restores them with this.
    /// </param>
    /// <returns>A checkpoint equal to this one apart from <see cref="SegmentIds"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="segmentIds"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="segmentIds"/> is not strictly increasing, has a negative identifier, or names more than
    /// <see cref="MaximumSegmentCount"/> segments.
    /// </exception>
    public EvolutionCheckpoint WithSegmentIds(IReadOnlyList<long> segmentIds) =>
        new(RunId, Sequence, CompatibilityHash, Payload, Checksum, SchemaVersion, Quality, QualityDirection,
            segmentIds ?? throw new ArgumentNullException(nameof(segmentIds)));

    internal EvolutionCheckpoint Clone() =>
        new(RunId, Sequence, CompatibilityHash, Payload, Checksum, SchemaVersion, Quality, QualityDirection, SegmentIds);
}
