namespace AiDotNet.Evolution.Programs.Novelty;

/// <summary>Owned, bounded nonzero finite vector with overflow-safe cosine arithmetic.</summary>
public sealed class EmbeddingVector
{
    /// <summary>The most components a vector may have.</summary>
    public const int MaximumDimensions = 16_384;
    private readonly double[] _unit;

    /// <summary>Copies and validates a vector.</summary>
    /// <param name="components">1 to 16,384 finite components, not all zero.</param>
    /// <exception cref="ArgumentException">The vector is empty, too long, zero, or has a non-finite component.</exception>
    public EmbeddingVector(IEnumerable<double> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        var copy = components.Take(MaximumDimensions + 1).ToArray();
        if (copy.Length is < 1 or > MaximumDimensions || copy.Any(x => !double.IsFinite(x)))
            throw new ArgumentException("Embedding dimensions and components must be bounded and finite.", nameof(components));
        double scale = copy.Max(Math.Abs);
        if (scale == 0) throw new ArgumentException("A zero vector is not similarity evidence.", nameof(components));
        _unit = copy.Select(x => x / scale).ToArray();
        double length = Math.Sqrt(_unit.Sum(x => x * x));
        Magnitude = scale > double.MaxValue / length ? double.MaxValue : scale * length;
        for (int i = 0; i < _unit.Length; i++) _unit[i] /= length;
        Components = Array.AsReadOnly(copy);
    }
    /// <summary>Gets the components as supplied.</summary>
    public IReadOnlyList<double> Components { get; }

    /// <summary>Gets the number of components.</summary>
    public int Dimensions => _unit.Length;
    /// <summary>Euclidean magnitude, saturated at Double.MaxValue when unrepresentable.</summary>
    public double Magnitude { get; }
    /// <summary>Computes cosine similarity without overflow.</summary>
    /// <param name="first">One vector.</param>
    /// <param name="second">Another with the same dimensions.</param>
    /// <returns>The similarity, in [-1, 1].</returns>
    /// <exception cref="ArgumentException">The dimensions differ.</exception>
    public static double CosineSimilarity(EmbeddingVector first, EmbeddingVector second)
    {
        ArgumentNullException.ThrowIfNull(first); ArgumentNullException.ThrowIfNull(second);
        if (first.Dimensions != second.Dimensions) throw new ArgumentException("Embedding dimensions differ.");
        double dot = 0;
        for (int i = 0; i < first.Dimensions; i++) dot += first._unit[i] * second._unit[i];
        return Math.Clamp(dot, -1, 1);
    }
    /// <summary>Summarises the vector without its values.</summary>
    /// <returns>A short description.</returns>
    public override string ToString() => $"embedding({Dimensions})";
}
