using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Configures how many numbered evolution checkpoints a directory store keeps.</summary>
/// <remarks>
/// <para>
/// A directory store writes one immutable numbered snapshot per save, so without retention a long run would leave a
/// snapshot per checkpoint interval on disk. Retention keeps two overlapping sets and deletes only what is in neither:
/// the <see cref="KeepLast"/> newest valid snapshots, which is what a crash needs, and the <see cref="KeepBest"/>
/// highest-quality ones, which is what a run that later drifted needs. The newest valid snapshot and the single
/// best-quality snapshot are protected unconditionally, so no combination of these values can delete the two snapshots
/// a run would actually want back.
/// </para>
/// <para>
/// Snapshots that cannot be read are never counted toward either quota and are never deleted by retention; a corrupt
/// file is evidence about a failure, and quietly discarding it would hide the failure while freeing almost nothing.
/// </para>
/// <para><b>For Beginners:</b> A checkpoint is a save file the engine writes as a search runs, so the work is not lost
/// if the process stops. Keeping every save would fill the disk, and keeping only the newest would lose a good result
/// that a later, unluckier stretch of the search replaced. These two numbers say how many recent saves and how many
/// high-scoring saves to keep. The defaults keep the five most recent plus the single best, which is a sensible
/// starting point; raise <see cref="KeepBest"/> if you want to compare several strong snapshots afterwards, or raise
/// <see cref="KeepLast"/> if you need a deeper undo history.</para>
/// </remarks>
public sealed class EvolutionCheckpointRetentionOptions
{
    /// <summary>Gets or sets how many of the newest valid snapshots are kept; must be at least one.</summary>
    public int KeepLast { get; set; } = 5;

    /// <summary>Gets or sets how many of the highest-quality valid snapshots are kept, beyond the newest ones.</summary>
    /// <remarks>
    /// Quality comes from <c>EvolutionCheckpoint.Quality</c> and is ranked in that checkpoint's own direction.
    /// Snapshots that record no quality rank last. Zero is allowed and still protects the single best snapshot, because
    /// retention never deletes it.
    /// </remarks>
    public int KeepBest { get; set; } = 1;

    /// <summary>Validates the values and returns an independent copy the store can hold.</summary>
    /// <returns>A validated copy that later edits to this instance cannot affect.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A quota is negative, <see cref="KeepLast"/> is zero, or the protected quota leaves no room for the next
    /// atomic snapshot under the package file limit.
    /// </exception>
    internal EvolutionCheckpointRetentionOptions SnapshotAndValidate()
    {
        if (KeepLast < 1) throw new ArgumentOutOfRangeException(nameof(KeepLast), KeepLast,
            "At least one checkpoint must be retained.");
        if (KeepBest < 0) throw new ArgumentOutOfRangeException(nameof(KeepBest), KeepBest,
            "The best-checkpoint quota cannot be negative.");
        if (KeepLast > EvolutionCollectionLimits.MaximumCheckpointFiles)
            throw new ArgumentOutOfRangeException(nameof(KeepLast), KeepLast,
                $"At most {EvolutionCollectionLimits.MaximumCheckpointFiles} recent checkpoints may be retained.");
        if (KeepBest > EvolutionCollectionLimits.MaximumCheckpointFiles)
            throw new ArgumentOutOfRangeException(nameof(KeepBest), KeepBest,
                $"At most {EvolutionCollectionLimits.MaximumCheckpointFiles} best checkpoints may be retained.");
        long protectedQuota = (long)KeepLast + Math.Max(KeepBest, 1);
        if (protectedQuota >= EvolutionCollectionLimits.MaximumCheckpointFiles)
            throw new ArgumentOutOfRangeException(nameof(KeepBest), KeepBest,
                $"The recent and best checkpoint quotas must protect fewer than " +
                $"{EvolutionCollectionLimits.MaximumCheckpointFiles} files so the next snapshot has a free slot.");
        return new EvolutionCheckpointRetentionOptions { KeepLast = KeepLast, KeepBest = KeepBest };
    }

    /// <summary>Encodes the retention policy for diagnostics and equality checks.</summary>
    internal string ToCanonicalString() => string.Join("|", new[]
    {
        KeepLast.ToString(CultureInfo.InvariantCulture),
        KeepBest.ToString(CultureInfo.InvariantCulture)
    });
}
