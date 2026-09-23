// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Interfaces/IEmbeddingClient.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs.Novelty;

public interface IProgramEmbeddingClient
{
    string ModelId { get; }
    /// <summary>Caller-maintained model/configuration revision; must change if vectors can change.</summary>
    string VersionHash { get; }

    ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
