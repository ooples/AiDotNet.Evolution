namespace AiDotNet.Evolution;

/// <summary>How an engine's checkpoints store its deduplication set and evaluation cache.</summary>
/// <remarks>
/// <para>Both grow with every distinct genome a run evaluates. Stored inline, every checkpoint repeats all of them, so
/// the cost of each save grows with the run. Stored in segments, each save adds only what changed since the previous one.</para>
/// <para><b>For Beginners:</b> leave this at <see cref="Auto"/>. Short and medium runs keep ordinary self-contained
/// checkpoints that any version of the engine can read and that you can copy anywhere. A long run switches to segments
/// once it has remembered enough genomes for that to matter, and then its checkpoint store (for
/// <see cref="DirectoryEvolutionCheckpointStore"/>, its whole directory) is what you copy to move the run.</para>
/// </remarks>
public enum EvolutionCheckpointFormat
{
    /// <summary>
    /// Inline until the deduplication set and cache together reach
    /// <see cref="EvolutionEngineOptions.CheckpointSegmentThreshold"/> entries, then segments for the rest of the run,
    /// when the checkpoint store supports them.
    /// </summary>
    Auto = 0,

    /// <summary>Every checkpoint is self-contained: portable, readable by older engines, and costlier as the run grows.</summary>
    Inline = 1,

    /// <summary>
    /// Always segments. Each save costs what changed since the previous one. Requires a store that implements
    /// <see cref="IEvolutionCheckpointSegmentStore"/>.
    /// </summary>
    Segmented = 2
}