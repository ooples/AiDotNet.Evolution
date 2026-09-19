namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Immutable bounded provider result; failure text never retains provider payloads.</summary>
public sealed class EmbeddingBatch
{
    public const int MaximumVectors = 257;
    private EmbeddingBatch(EmbeddingVector[] vectors, bool succeeded)
    { Vectors = Array.AsReadOnly(vectors); Succeeded = succeeded; }
    public bool Succeeded { get; }
    public IReadOnlyList<EmbeddingVector> Vectors { get; }
    public string FailureReason => Succeeded ? string.Empty : "Embedding provider unavailable.";
    public static EmbeddingBatch Success(IEnumerable<EmbeddingVector> vectors)
    {
        ArgumentNullException.ThrowIfNull(vectors);
        var copy = vectors.Take(MaximumVectors + 1).ToArray();
        if (copy.Length is < 1 or > MaximumVectors || copy.Any(v => v is null))
            throw new ArgumentException("A batch requires 1 to 257 non-null vectors.", nameof(vectors));
        return new(copy, true);
    }
    public static EmbeddingBatch Failure(string reason)
    { ArgumentNullException.ThrowIfNull(reason); return new(Array.Empty<EmbeddingVector>(), false); }
    public override string ToString() => Succeeded ? $"embeddings({Vectors.Count})" : "embeddings(failed)";
}
