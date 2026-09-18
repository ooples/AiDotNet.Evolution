using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Novelty;
// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Agentic/Embeddings/DeterministicEmbeddingClient.cs
// Original license retained in Programs/Legacy/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.CSharp.Tests.Novelty;

internal sealed class DeterministicEmbeddingClient : IProgramEmbeddingClient
{
    public const int DefaultDimensions = 64;

    public const string DefaultModelId = "deterministic-hashing-embedding";

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    private long _calls;
    private long _textsEmbedded;

    public DeterministicEmbeddingClient(int dimensions = DefaultDimensions, string modelId = DefaultModelId)
    {
        ProgramGuard.NotNullOrWhiteSpace(modelId);
        if (dimensions < 2 || dimensions > 4_096)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, "Value must be between 2 and 4096.");
        }

        Dimensions = dimensions;
        ModelId = modelId;
    }

    public string ModelId { get; }
    public string VersionHash => ModelId + "-" + Dimensions;

    public int Dimensions { get; }

    public long Calls => Interlocked.Read(ref _calls);

    public long TextsEmbedded => Interlocked.Read(ref _textsEmbedded);

    public ValueTask<EmbeddingBatch> EmbedAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        cancellationToken.ThrowIfCancellationRequested();

        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _textsEmbedded, texts.Count);

        var vectors = new List<EmbeddingVector>(texts.Count);
        foreach (string text in texts) vectors.Add(Embed(text));
        return new ValueTask<EmbeddingBatch>(EmbeddingBatch.Success(vectors));
    }

    public EmbeddingVector Embed(string text)
    {
        ProgramGuard.NotNull(text);
        var components = new double[Dimensions];
        int tokenCount = 0;

        int start = 0;
        while (start < text.Length)
        {
            if (char.IsWhiteSpace(text[start]))
            {
                start++;
                continue;
            }

            int end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            Accumulate(components, text, start, end - start);
            tokenCount++;
            start = end;
        }

        // A blank input, or a token multiset whose signed contributions happen to cancel exactly, would otherwise
        // produce a zero-magnitude vector, and such a vector compares as similarity zero against everything —
        // including a copy of itself. Both cases fall back to a single deterministic unit component.
        if (tokenCount == 0 || IsZero(components))
        {
            components[(int)(Hash(text, 0, text.Length) % (uint)Dimensions)] = 1.0;
        }

        return new EmbeddingVector(components);
    }

    private static bool IsZero(double[] components)
    {
        foreach (double component in components)
        {
            if (component != 0.0) return false;
        }

        return true;
    }

    private void Accumulate(double[] components, string text, int start, int length)
    {
        uint hash = Hash(text, start, length);
        int bucket = (int)(hash % (uint)Dimensions);
        double sign = (hash & 0x80000000u) == 0 ? 1.0 : -1.0;
        components[bucket] += sign;
    }

    private static uint Hash(string text, int start, int length)
    {
        uint hash = FnvOffsetBasis;
        for (int index = start; index < start + length; index++)
        {
            char character = text[index];
            unchecked
            {
                hash = (hash ^ (byte)(character & 0xFF)) * FnvPrime;
                hash = (hash ^ (byte)(character >> 8)) * FnvPrime;
            }
        }

        return hash;
    }
}
