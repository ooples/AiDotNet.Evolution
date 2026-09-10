using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Immutable, normalized nearest-centroid partition with a fixed number of cells.</summary>
/// <remarks>
/// Coordinates are supplied in [0,1], in descriptor order. Descriptor ranges define linear normalization;
/// only Reject and Clamp policies are supported. Bin counts do not route candidates. Centroids are not fitted
/// here: supply a frozen, independently prepared partition. Arbitrary sites are a Voronoi archive, not a claim
/// of a fitted centroidal Voronoi tessellation. Site order defines cell identity and resolves distance ties.
/// </remarks>
public sealed class CentroidArchiveDefinition
{
    /// <summary>Maximum number of sites supported by the bounded linear nearest-neighbor implementation.</summary>
    public const int MaximumCentroids = 10_000;
    /// <summary>Maximum total stored coordinates across all sites.</summary>
    public const int MaximumCoordinates = 1_000_000;

    /// <summary>Copies and validates descriptor normalization and normalized centroid coordinates.</summary>
    public CentroidArchiveDefinition(IEnumerable<EvolutionDescriptorDefinition> descriptors,
        IEnumerable<IReadOnlyList<double>> centroids)
    {
        Guard.NotNull(descriptors);
        Guard.NotNull(centroids);
        var axes = EvolutionCollection.ToBoundedArray(descriptors,
            EvolutionCollectionLimits.MaximumArchiveDimensions, nameof(descriptors));
        if (axes.Length == 0 || axes.Any(axis => axis is null) ||
            axes.Select(axis => axis.Name).Distinct(StringComparer.Ordinal).Count() != axes.Length)
            throw new ArgumentException("Descriptors must be nonempty, nonnull and uniquely named.", nameof(descriptors));
        if (axes.Any(axis => axis.OutOfRangePolicy != EvolutionOutOfRangePolicy.Reject &&
                             axis.OutOfRangePolicy != EvolutionOutOfRangePolicy.Clamp))
            throw new ArgumentException("Centroid normalization supports only Reject and Clamp.", nameof(descriptors));

        var sites = new List<IReadOnlyList<double>>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var identity = new List<string> { "normalized-centroid-definition-v1" };
        foreach (var axis in axes)
        {
            identity.Add(axis.Name); identity.Add(Bits(axis.Minimum)); identity.Add(Bits(axis.Maximum));
            identity.Add(axis.BinCount.ToString(CultureInfo.InvariantCulture));
            identity.Add(((int)axis.OutOfRangePolicy).ToString(CultureInfo.InvariantCulture));
        }
        foreach (var site in centroids)
        {
            if (site is null || sites.Count >= MaximumCentroids ||
                (long)(sites.Count + 1) * axes.Length > MaximumCoordinates)
                throw new ArgumentException("Null site or centroid storage bound exceeded.", nameof(centroids));
            var point = EvolutionCollection.CopyBounded(site, axes.Length, nameof(centroids));
            if (point.Length != axes.Length || point.Any(value =>
                    !EvolutionDescriptorDefinition.IsFinite(value) || value < 0 || value > 1))
                throw new ArgumentException("Each centroid must have one finite [0,1] coordinate per descriptor.", nameof(centroids));
            for (int i = 0; i < point.Length; i++) if (point[i] == 0) point[i] = 0; // canonical signed zero
            string key = string.Join("", point.Select(Bits));
            if (!unique.Add(key)) throw new ArgumentException("Duplicate centroids are not allowed.", nameof(centroids));
            identity.Add(key);
            sites.Add(Array.AsReadOnly(point));
        }
        if (sites.Count == 0) throw new ArgumentException("At least one centroid is required.", nameof(centroids));
        Descriptors = Array.AsReadOnly(axes);
        Centroids = sites.AsReadOnly();
        DefinitionHash = EvolutionHash.Combine(identity);
    }

    /// <summary>Gets immutable normalization axes in coordinate order.</summary>
    public IReadOnlyList<EvolutionDescriptorDefinition> Descriptors { get; }
    /// <summary>Gets the owned, immutable normalized sites in stable cell-index order.</summary>
    public IReadOnlyList<IReadOnlyList<double>> Centroids { get; }
    /// <summary>Gets the versioned identity of normalization, ordered sites and routing semantics.</summary>
    public string DefinitionHash { get; }

    /// <summary>Returns the nearest centroid index, or null for missing, nonfinite or rejected descriptors.</summary>
    /// <remarks>Uses squared Euclidean distance after normalization, O(KD) work and O(D) temporary storage.</remarks>
    public int? FindCell(IReadOnlyDictionary<string, double> values)
    {
        Guard.NotNull(values);
        var point = new double[Descriptors.Count];
        for (int i = 0; i < point.Length; i++)
        {
            var axis = Descriptors[i];
            if (!values.TryGetValue(axis.Name, out double value) || !EvolutionDescriptorDefinition.IsFinite(value)) return null;
            if (value < axis.Minimum || value > axis.Maximum)
            {
                if (axis.OutOfRangePolicy == EvolutionOutOfRangePolicy.Reject) return null;
                value = Math.Max(axis.Minimum, Math.Min(axis.Maximum, value));
            }
            point[i] = (value - axis.Minimum) / (axis.Maximum - axis.Minimum);
        }
        int best = 0;
        double bestDistance = double.PositiveInfinity;
        for (int site = 0; site < Centroids.Count; site++)
        {
            double distance = 0;
            for (int axis = 0; axis < point.Length; axis++)
            {
                double difference = point[axis] - Centroids[site][axis];
                distance += difference * difference;
            }
            if (distance < bestDistance) { best = site; bestDistance = distance; }
        }
        return best;
    }

    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value)
        .ToString("x16", CultureInfo.InvariantCulture);
}
