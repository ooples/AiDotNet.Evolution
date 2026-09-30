namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Immutable bounded provider result; failure text never retains provider payloads.</summary>
public sealed class EmbeddingBatch
{
    /// <summary>The most vectors one batch may hold: a candidate and 256 neighbours.</summary>
    public const int MaximumVectors = 257;
    private EmbeddingBatch(EmbeddingVector[] vectors, bool succeeded)
    { Vectors = Array.AsReadOnly(vectors); Succeeded = succeeded; }

    /// <summary>Gets whether the provider returned vectors.</summary>
    public bool Succeeded { get; }

    /// <summary>Gets the vectors, one per requested text in order; empty on failure.</summary>
    public IReadOnlyList<EmbeddingVector> Vectors { get; }

    /// <summary>Gets a fixed failure description, or empty on success. Provider text is never kept.</summary>
    public string FailureReason => Succeeded ? string.Empty : "Embedding provider unavailable.";

    /// <summary>Creates a successful batch.</summary>
    /// <param name="vectors">1 to 257 vectors, in request order.</param>
    /// <returns>The batch.</returns>
    /// <exception cref="ArgumentException">There are no vectors, too many, or a null one.</exception>
    public static EmbeddingBatch Success(IEnumerable<EmbeddingVector> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var copy = vectors.Take(MaximumVectors + 1).ToArray();
        if (copy.Length is < 1 or > MaximumVectors || copy.Any(v => v is null))
            throw new ArgumentException("A batch requires 1 to 257 non-null vectors.", nameof(vectors));
        return new(copy, true);
    }
    /// <summary>Creates a failed batch.</summary>
    /// <param name="reason">Why it failed; required, but deliberately not retained, since it may carry provider data.</param>
    /// <returns>The batch.</returns>
    public static EmbeddingBatch Failure(string reason)
    { ArgumentNullException.ThrowIfNull(reason); return new(Array.Empty<EmbeddingVector>(), false); }

    /// <summary>Summarises the batch without its contents.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() => Succeeded ? $"embeddings({Vectors.Count})" : "embeddings(failed)";
}
