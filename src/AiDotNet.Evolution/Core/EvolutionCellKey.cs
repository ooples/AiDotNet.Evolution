using System.Collections.ObjectModel;
using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Immutable, value-based key for one multidimensional archive cell.</summary>
/// <remarks>
/// <para>
/// A quality-diversity archive discretizes each behavior descriptor into bins, and a cell is the combination of one
/// bin index per descriptor. This key stores that combination and exposes it two ways: <see cref="Bins"/> for
/// programmatic access and <see cref="StableKey"/>, a culture-independent comma-separated string such as
/// <c>"2,0,4"</c>, which serves as the dictionary key, the checkpoint representation, and the deterministic sort order
/// for archive entries. Equality, hashing, and comparison all delegate to <see cref="StableKey"/> with ordinal
/// semantics, so two keys built from equal bin sequences are interchangeable regardless of where they were created.
/// </para>
/// <para>
/// Bin indices must be nonnegative and the sequence must be nonempty; the constructor copies the input so later
/// changes to the source cannot affect the key. Construction is O(d) for d descriptors. Comparison is an ordinal
/// string comparison, so the order is lexicographic over the textual form rather than numeric per dimension (for
/// example <c>"10,0"</c> sorts before <c>"2,0"</c>); it is stable, which is what archives require.
/// </para>
/// <para><b>For Beginners:</b> Think of the archive as a spreadsheet where each row is one setting of the first
/// descriptor and each column is one setting of the second. A cell key is the coordinate of one square, like "row 2,
/// column 4", written in a form the computer can compare and store safely. You rarely build one yourself; the archive
/// computes it from a candidate's descriptor values and attaches it to each elite as
/// <see cref="EvolutionArchiveEntry{TGenome}.Cell"/>. You use it when you want to look up who currently occupies a
/// particular square with <see cref="AiDotNet.Evolution.IEvolutionArchiveView{TGenome}.Get"/>, or when you print or
/// log which part of the map a result came from.</para>
/// </remarks>
public sealed class EvolutionCellKey : IEquatable<EvolutionCellKey>, IComparable<EvolutionCellKey>
{
    private readonly int[] _bins;
    private readonly ReadOnlyCollection<int> _view;

    /// <summary>Initializes a key from one bin index per descriptor.</summary>
    /// <param name="bins">The nonempty bin sequence.</param>
    /// <exception cref="ArgumentNullException"><paramref name="bins"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="bins"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bin index is negative.</exception>
    public EvolutionCellKey(IEnumerable<int> bins)
    {
        Guard.NotNull(bins);
        // Every completed evaluation builds a key, so an indexed input (archives pass arrays) is copied exactly;
        // presizing a list to the 64-dimension bound cost a 256-byte buffer per key (E-1).
        if (bins is IReadOnlyList<int> list)
        {
            if (list.Count > EvolutionCollectionLimits.MaximumArchiveDimensions) throw TooManyDimensions(nameof(bins));
            _bins = new int[list.Count];
            for (int i = 0; i < _bins.Length; i++) _bins[i] = list[i];
        }
        else
        {
            var bounded = new List<int>();
            foreach (int value in bins)
            {
                if (bounded.Count == EvolutionCollectionLimits.MaximumArchiveDimensions) throw TooManyDimensions(nameof(bins));
                bounded.Add(value);
            }
            _bins = bounded.ToArray();
        }
        if (_bins.Length == 0) throw new ArgumentException("A cell key requires at least one bin.", nameof(bins));
        foreach (int value in _bins)
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(bins));
        _view = Array.AsReadOnly(_bins);
        StableKey = FormatKey(_bins);
    }

    private static ArgumentException TooManyDimensions(string parameterName) =>
        new($"A cell key may contain at most {EvolutionCollectionLimits.MaximumArchiveDimensions} dimensions.", parameterName);

    // The same text string.Join(",", invariant bins) produces, without an iterator and a string per bin.
    private static string FormatKey(int[] bins)
    {
        if (bins.Length == 1) return bins[0].ToString(CultureInfo.InvariantCulture);
        var text = new System.Text.StringBuilder(bins.Length * 4);
        for (int i = 0; i < bins.Length; i++)
        {
            if (i > 0) text.Append(',');
            // Bins are validated nonnegative, and a nonnegative int formats as the same ASCII digits in every culture.
            text.Append(bins[i]);
        }
        return text.ToString();
    }

    /// <summary>Gets a read-only view of the bin indices.</summary>
    public IReadOnlyList<int> Bins => _view;

    /// <summary>Gets the culture-independent serialized key.</summary>
    public string StableKey { get; }

    /// <inheritdoc/>
    public bool Equals(EvolutionCellKey? other) => other is not null && StableKey == other.StableKey;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as EvolutionCellKey);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(StableKey);

    /// <inheritdoc/>
    public int CompareTo(EvolutionCellKey? other) => other is null ? 1 : StringComparer.Ordinal.Compare(StableKey, other.StableKey);

    /// <inheritdoc/>
    public override string ToString() => StableKey;
}
