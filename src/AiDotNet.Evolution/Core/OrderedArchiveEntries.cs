namespace AiDotNet.Evolution;

/// <summary>
/// Archive entries kept in ordinal cell-key order, with positional access, in bounded chunks.
/// </summary>
/// <remarks>
/// A single list made inserting a new cell O(n), because every later element shifts. Chunks of at most
/// <see cref="MaximumChunk"/> entries bound that shift, and a Fenwick tree over the chunk sizes turns a position into
/// a chunk and back in O(log n). Inserting or removing is O(log n + chunk), and a position lookup is O(log n).
/// </remarks>
internal sealed class OrderedArchiveEntries<TGenome>
{
    private const int MaximumChunk = 512;
    private readonly List<List<EvolutionArchiveEntry<TGenome>>> _chunks = new();
    private int[] _tree = Array.Empty<int>();

    internal OrderedArchiveEntries(IEnumerable<EvolutionArchiveEntry<TGenome>> ordered)
    {
        var chunk = new List<EvolutionArchiveEntry<TGenome>>(MaximumChunk);
        foreach (EvolutionArchiveEntry<TGenome> entry in ordered)
        {
            if (chunk.Count == MaximumChunk / 2)
            {
                _chunks.Add(chunk);
                chunk = new List<EvolutionArchiveEntry<TGenome>>(MaximumChunk);
            }

            chunk.Add(entry);
            Count++;
        }

        if (chunk.Count > 0 || _chunks.Count == 0) _chunks.Add(chunk);
        Rebuild();
    }

    internal int Count { get; private set; }

    internal EvolutionArchiveEntry<TGenome> this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            (int chunk, int local) = Locate(index);
            return _chunks[chunk][local];
        }
    }

    internal IEnumerable<EvolutionArchiveEntry<TGenome>> Items() => _chunks.SelectMany(chunk => chunk);

    /// <summary>Returns the position of a key when present, else the complement of where it would be inserted.</summary>
    internal int IndexOf(string stableKey)
    {
        (int chunk, int local) = Find(stableKey);
        int start = Prefix(chunk);
        return local >= 0 ? start + local : ~(start + ~local);
    }

    /// <summary>Replaces the entry that holds this key.</summary>
    internal void Replace(string stableKey, EvolutionArchiveEntry<TGenome> entry)
    {
        (int chunk, int local) = Find(stableKey);
        if (local < 0) throw new InvalidOperationException("The cell is not in the ordered view.");
        _chunks[chunk][local] = entry;
    }

    /// <summary>Inserts an entry for a key not yet present.</summary>
    internal void Insert(EvolutionArchiveEntry<TGenome> entry)
    {
        (int chunk, int local) = Find(entry.Cell.StableKey);
        if (local >= 0) throw new InvalidOperationException("The cell is already in the ordered view.");
        List<EvolutionArchiveEntry<TGenome>> items = _chunks[chunk];
        items.Insert(~local, entry);
        Count++;
        if (items.Count > MaximumChunk)
        {
            int half = items.Count / 2;
            var upper = items.GetRange(half, items.Count - half);
            items.RemoveRange(half, items.Count - half);
            _chunks.Insert(chunk + 1, upper);
            Rebuild();
        }
        else
        {
            Add(chunk, 1);
        }
    }

    /// <summary>Removes the entry that holds this key.</summary>
    internal void Remove(string stableKey)
    {
        (int chunk, int local) = Find(stableKey);
        if (local < 0) throw new InvalidOperationException("The cell is not in the ordered view.");
        List<EvolutionArchiveEntry<TGenome>> items = _chunks[chunk];
        items.RemoveAt(local);
        Count--;
        if (items.Count == 0 && _chunks.Count > 1)
        {
            _chunks.RemoveAt(chunk);
            Rebuild();
        }
        else
        {
            Add(chunk, -1);
        }
    }

    // The chunk that holds or would hold the key, and the key's slot in it (complemented when absent).
    private (int Chunk, int Local) Find(string stableKey)
    {
        int low = 0, high = _chunks.Count - 1;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            List<EvolutionArchiveEntry<TGenome>> candidate = _chunks[middle];
            if (candidate.Count > 0 && string.CompareOrdinal(candidate[candidate.Count - 1].Cell.StableKey, stableKey) < 0) low = middle + 1;
            else high = middle;
        }

        List<EvolutionArchiveEntry<TGenome>> items = _chunks[low];
        int first = 0, last = items.Count - 1;
        while (first <= last)
        {
            int middle = first + ((last - first) >> 1);
            int comparison = string.CompareOrdinal(items[middle].Cell.StableKey, stableKey);
            if (comparison == 0) return (low, middle);
            if (comparison < 0) first = middle + 1;
            else last = middle - 1;
        }

        return (low, ~first);
    }

    private (int Chunk, int Local) Locate(int index)
    {
        // Fenwick lower-bound: the chunk whose cumulative size first exceeds the index.
        int position = 0, remaining = index;
        for (int step = HighestPowerOfTwo(_tree.Length); step > 0; step >>= 1)
        {
            int next = position + step;
            if (next <= _tree.Length && _tree[next - 1] <= remaining)
            {
                position = next;
                remaining -= _tree[next - 1];
            }
        }

        return (position, remaining);
    }

    private int Prefix(int chunk)
    {
        int sum = 0;
        for (int i = chunk; i > 0; i -= i & -i) sum += _tree[i - 1];
        return sum;
    }

    private void Add(int chunk, int delta)
    {
        for (int i = chunk + 1; i <= _tree.Length; i += i & -i) _tree[i - 1] += delta;
    }

    private void Rebuild()
    {
        _tree = new int[_chunks.Count];
        for (int i = 0; i < _chunks.Count; i++) Add(i, _chunks[i].Count);
    }

    private static int HighestPowerOfTwo(int value)
    {
        int power = 1;
        while (power <= value >> 1) power <<= 1;
        return value == 0 ? 0 : power;
    }
}
