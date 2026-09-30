// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:src/Evolution/Programs/Novelty/ProgramTokenSetDistance.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.

namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Jaccard distance between the token sets of two normalised programs; cheap and order-insensitive.</summary>
/// <remarks>Programs in different languages are at distance 1. Token sets are memoised by program id.</remarks>
public sealed class ProgramTokenSetDistance : IGenomeDistance<ProgramGenome>
{
    /// <summary>The metric identity reported as <see cref="Id"/>.</summary>
    public const string MetricId = "program-token-set";

    /// <summary>The default number of memoised token sets.</summary>
    public const int DefaultMemoCapacity = 1_024;

    private readonly Dictionary<string, HashSet<string>> _memo = new(StringComparer.Ordinal);
    private readonly Queue<string> _memoOrder = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _sizes = new(StringComparer.Ordinal);
    private long _memoCharacters;

    /// <summary>Creates the distance.</summary>
    /// <param name="memoCapacity">How many token sets are memoised, 0 to 8192; 0 disables the memo.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity is out of range.</exception>
    public ProgramTokenSetDistance(int memoCapacity = DefaultMemoCapacity)
    {
        if (memoCapacity < 0 || memoCapacity > 8_192)
        {
            throw new ArgumentOutOfRangeException(nameof(memoCapacity), memoCapacity,
                "Value must be between 0 and 8192.");
        }

        MemoCapacity = memoCapacity;
    }

    /// <summary>Gets how many token sets are memoised.</summary>
    public int MemoCapacity { get; }

    /// <inheritdoc />
    public string Id => MetricId;

    /// <inheritdoc />
    public string VersionHash => MetricId + "-v2";

    /// <inheritdoc />
    public double Distance(ProgramGenome first, ProgramGenome second)
    {
        ProgramGuard.NotNull(first);
        ProgramGuard.NotNull(second);
        if (first.Language != second.Language) return 1;
        if (string.Equals(first.Id, second.Id, StringComparison.Ordinal)) return 0.0;
        return Jaccard(TokensFor(first), TokensFor(second));
    }

    /// <summary>Empties the memo.</summary>
    public void ClearMemo()
    {
        lock (_gate)
        {
            _memo.Clear();
            _memoOrder.Clear();
            _sizes.Clear(); _memoCharacters = 0;
        }
    }

    private HashSet<string> TokensFor(ProgramGenome genome)
    {
        if (MemoCapacity == 0) return ProgramTokenizer.Tokenize(genome.NormalizedSource);

        lock (_gate)
        {
            if (_memo.TryGetValue(genome.Id, out HashSet<string>? remembered)) return remembered;
        }

        // Tokenizing outside the lock keeps a long program from blocking every other comparison; a duplicate
        // computation under contention is harmless because the result is a pure function of the genome.
        HashSet<string> tokens = ProgramTokenizer.Tokenize(genome.NormalizedSource);
        lock (_gate)
        {
            if (_memo.TryGetValue(genome.Id, out HashSet<string>? raced)) return raced;
            while ((_memo.Count >= MemoCapacity || _memoCharacters + genome.Source.Length > 16_777_216) && _memoOrder.Count > 0)
            {
                string id = _memoOrder.Dequeue();
                _memo.Remove(id); _memoCharacters -= _sizes[id]; _sizes.Remove(id);
            }

            if (_memo.Count < MemoCapacity)
            {
                _memo[genome.Id] = tokens;
                _memoOrder.Enqueue(genome.Id);
                _sizes.Add(genome.Id, genome.Source.Length); _memoCharacters += genome.Source.Length;
            }
        }

        return tokens;
    }

    /// <summary>Computes the distance between two source texts without the memo.</summary>
    /// <param name="first">One source.</param>
    /// <param name="second">The other.</param>
    /// <returns>A distance in [0, 1].</returns>
    public static double ComputeDistance(string first, string second)
    {
        ProgramGuard.NotNull(first);
        ProgramGuard.NotNull(second);
        return Compute(ProgramText.Normalize(first), ProgramText.Normalize(second));
    }

    private static double Compute(string first, string second)
    {
        if (first.Length > ProgramGenome.MaxSourceLength || second.Length > ProgramGenome.MaxSourceLength)
            throw new ArgumentException("Structural comparison source exceeds the program bound.");
        if (string.Equals(first, second, StringComparison.Ordinal)) return 0.0;
        return Jaccard(ProgramTokenizer.Tokenize(first), ProgramTokenizer.Tokenize(second));
    }

    private static double Jaccard(HashSet<string> firstTokens, HashSet<string> secondTokens)
    {
        if (firstTokens.Count == 0 && secondTokens.Count == 0) return 0.0;

        HashSet<string> smaller = firstTokens.Count <= secondTokens.Count ? firstTokens : secondTokens;
        HashSet<string> larger = ReferenceEquals(smaller, firstTokens) ? secondTokens : firstTokens;

        int shared = 0;
        foreach (string token in smaller)
        {
            if (larger.Contains(token)) shared++;
        }

        int union = firstTokens.Count + secondTokens.Count - shared;
        if (union == 0) return 0.0;

        double distance = 1.0 - ((double)shared / union);
        return distance < 0.0 ? 0.0 : distance > 1.0 ? 1.0 : distance;
    }
}
