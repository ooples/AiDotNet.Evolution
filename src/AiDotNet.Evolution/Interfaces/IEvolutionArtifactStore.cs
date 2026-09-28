namespace AiDotNet.Evolution;

/// <summary>Keeps artifact content that is too large, or not text, to retain inline in an evaluation.</summary>
/// <remarks>
/// The engine writes here only when <see cref="EvolutionArtifactOptions.Store"/> is set. What stays inline is a bounded
/// text preview plus the content address, so checkpoints and the run's state hash remain text-only and reproducible
/// while the full content is still recoverable. This is the counterpart of OpenEvolve's on-disk artifact storage
/// (<c>artifacts_base_path</c>, <c>artifact_size_threshold</c>).
/// </remarks>
public interface IEvolutionArtifactStore
{
    /// <summary>Stores content and returns its content address (<c>sha256:</c> plus 64 hex digits).</summary>
    /// <remarks>Storing identical bytes twice returns the same address and keeps one copy.</remarks>
    string Put(byte[] content);

    /// <summary>Reads content previously stored under <paramref name="reference"/>, verifying it against the address.</summary>
    bool TryRead(string reference, out byte[] content);
}
