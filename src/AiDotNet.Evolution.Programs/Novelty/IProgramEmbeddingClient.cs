// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Interfaces/IEmbeddingClient.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Turns program texts into embedding vectors.</summary>
public interface IProgramEmbeddingClient
{
    /// <summary>Gets the embedding model identity.</summary>
    string ModelId { get; }
    /// <summary>Caller-maintained model/configuration revision; must change if vectors can change.</summary>
    string VersionHash { get; }

    /// <summary>Embeds texts in one request.</summary>
    /// <param name="texts">The texts, at most <see cref="EmbeddingBatch.MaximumVectors"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>One vector per text in order, or <see cref="EmbeddingBatch.Failure"/>.</returns>
    ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
