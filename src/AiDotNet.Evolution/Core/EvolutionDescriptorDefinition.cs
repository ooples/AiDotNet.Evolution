using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>Defines one named, bounded dimension of a quality-diversity archive.</summary>
/// <remarks>
/// <para>
/// A MAP-Elites archive is a grid, and each <see cref="EvolutionDescriptorDefinition"/> is one axis of that grid.
/// The definition fixes the axis <see cref="Name"/>, its finite <see cref="Minimum"/> and <see cref="Maximum"/>,
/// the number of <see cref="BinCount"/> cells between them, where the cell boundaries lie (<see cref="Binning"/>),
/// and what happens to a value outside the bounds: <see cref="EvolutionOutOfRangePolicy.Reject"/> makes
/// <see cref="TryGetBin"/> return <c>false</c> so the candidate is not archived,
/// <see cref="EvolutionOutOfRangePolicy.Clamp"/> folds the value into the first or last interior bin, and
/// <see cref="EvolutionOutOfRangePolicy.OverflowBins"/> reserves one extra cell below and one above the range, which
/// is why <see cref="EffectiveBinCount"/> can exceed <see cref="BinCount"/>.
/// <see cref="ToCanonicalString"/> is folded into the archive definition hash and therefore into checkpoint
/// compatibility, so changing any bound, bin count, or binning mode invalidates older checkpoints by design.
/// </para>
/// <para>
/// The constructor builds <see cref="EvolutionDescriptorBinning.Linear"/> bins. <see cref="Logarithmic"/>,
/// <see cref="WithEdges"/>, <see cref="AdaptiveMinMax"/>, and <see cref="AdaptiveQuantile"/> build the other modes.
/// An adaptive definition is immutable like any other: <see cref="Observe"/> returns the definition refitted to one
/// more value, and a <see cref="MapElitesArchive{TGenome}"/> adopts it and re-keys its elites.
/// </para>
/// <para><b>For Beginners:</b> Quality-diversity search does not keep only the single best solution; it keeps the
/// best solution for every "kind" of behaviour, and descriptors are how you tell the engine which kinds matter.
/// Suppose you are evolving small neural networks and care about model size and latency: you would define two
/// descriptors, <c>"parameters"</c> from 1,000 to 1,000,000 in 10 bins and <c>"latencyMs"</c> from 0 to 50 in 5
/// bins, giving a 10 x 5 grid in which each cell remembers the most accurate network of that size and speed. A
/// descriptor is like one axis of a chart with a fixed number of tick marks. Choose bounds that cover the values you
/// expect to see; use <see cref="EvolutionOutOfRangePolicy.Clamp"/> or
/// <see cref="EvolutionOutOfRangePolicy.OverflowBins"/> if surprising values should still be archived, or keep the
/// default <see cref="EvolutionOutOfRangePolicy.Reject"/> if a value outside the range indicates a broken
/// candidate. If you cannot guess the range at all, use <see cref="AdaptiveMinMax"/> or
/// <see cref="AdaptiveQuantile"/> and let the search's own results place the tick marks.</para>
/// <para>
/// Background: Mouret &amp; Clune (2015), "Illuminating search spaces by mapping elites", arXiv:1504.04909. Linear
/// binning is O(1): the interior index is <c>floor((value - Minimum) / (Maximum - Minimum) * BinCount)</c>, with the
/// exact <see cref="Maximum"/> assigned to the last interior bin; edge-based binning is a binary search. Every
/// computation uses the invariant culture so results are identical on every machine.
/// </para>
/// </remarks>
public sealed class EvolutionDescriptorDefinition
{
    /// <summary>The number of observed values an adaptive quantile axis keeps by default.</summary>
    public const int DefaultQuantileSampleCapacity = 4096;

    private readonly double[] _edges;
    private readonly double[] _sample;
    private IReadOnlyList<double>? _edgeView;

    /// <summary>Initializes a fixed descriptor definition with equal-width bins.</summary>
    /// <param name="name">The unique descriptor name.</param>
    /// <param name="minimum">The finite lower bound.</param>
    /// <param name="maximum">The finite upper bound, greater than <paramref name="minimum"/>.</param>
    /// <param name="binCount">The number of bins inside the configured bounds.</param>
    /// <param name="outOfRangePolicy">How values outside the bounds are handled.</param>
    public EvolutionDescriptorDefinition(
        string name,
        double minimum,
        double maximum,
        int binCount,
        EvolutionOutOfRangePolicy outOfRangePolicy = EvolutionOutOfRangePolicy.Reject)
        : this(Validate(name, minimum, maximum, binCount, outOfRangePolicy, EvolutionDescriptorBinning.Linear),
            minimum, maximum, binCount, outOfRangePolicy, EvolutionDescriptorBinning.Linear,
            Array.Empty<double>(), 0, 0, 0, Array.Empty<double>(), 0, 1)
    {
    }

    private EvolutionDescriptorDefinition(
        string name,
        double minimum,
        double maximum,
        int binCount,
        EvolutionOutOfRangePolicy outOfRangePolicy,
        EvolutionDescriptorBinning binning,
        double[] edges,
        long observationCount,
        double observedMinimum,
        double observedMaximum,
        double[] sample,
        int sampleCapacity,
        long sampleStride)
    {
        Name = name;
        Minimum = minimum;
        Maximum = maximum;
        BinCount = binCount;
        OutOfRangePolicy = outOfRangePolicy;
        Binning = binning;
        _edges = edges;
        ObservationCount = observationCount;
        _observedMinimum = observedMinimum;
        _observedMaximum = observedMaximum;
        _sample = sample;
        SampleCapacity = sampleCapacity;
        SampleStride = sampleStride;
    }

    private readonly double _observedMinimum;
    private readonly double _observedMaximum;

    /// <summary>Creates a definition whose bins are equal in log-value.</summary>
    /// <param name="name">The unique descriptor name.</param>
    /// <param name="minimum">The finite, positive lower bound.</param>
    /// <param name="maximum">The finite upper bound, greater than <paramref name="minimum"/>.</param>
    /// <param name="binCount">The number of bins inside the bounds.</param>
    /// <param name="outOfRangePolicy">How values outside the bounds are handled; <see cref="EvolutionOutOfRangePolicy.Grow"/>
    /// adds whole bins of the same log-width.</param>
    /// <returns>The logarithmic definition.</returns>
    /// <remarks>
    /// Use this for a quantity that spans orders of magnitude, such as a parameter count or a latency: with 3 bins
    /// from 1 to 1,000 the boundaries fall at 10 and 100, where linear bins would put almost every small value in the
    /// first bin.
    /// </remarks>
    public static EvolutionDescriptorDefinition Logarithmic(
        string name,
        double minimum,
        double maximum,
        int binCount,
        EvolutionOutOfRangePolicy outOfRangePolicy = EvolutionOutOfRangePolicy.Reject)
    {
        string trimmed = Validate(name, minimum, maximum, binCount, outOfRangePolicy, EvolutionDescriptorBinning.Logarithmic);
        return new EvolutionDescriptorDefinition(trimmed, minimum, maximum, binCount, outOfRangePolicy,
            EvolutionDescriptorBinning.Logarithmic, Array.Empty<double>(), 0, 0, 0, Array.Empty<double>(), 0, 1);
    }

    /// <summary>Creates a definition whose bins are bounded by the given edges.</summary>
    /// <param name="name">The unique descriptor name.</param>
    /// <param name="edges">At least two finite, strictly increasing boundaries; the first and last are the bounds, and
    /// each consecutive pair is one bin.</param>
    /// <param name="outOfRangePolicy">How values outside the first and last edge are handled. Growth is not
    /// available, because there is no bin width to extend by.</param>
    /// <returns>The explicit-edge definition.</returns>
    public static EvolutionDescriptorDefinition WithEdges(
        string name,
        IEnumerable<double> edges,
        EvolutionOutOfRangePolicy outOfRangePolicy = EvolutionOutOfRangePolicy.Reject)
    {
        Guard.NotNull(edges);
        double[] copy = edges.ToArray();
        if (copy.Length < 2) throw new ArgumentException("At least two edges are required.", nameof(edges));
        for (int i = 0; i < copy.Length; i++)
        {
            if (!IsFinite(copy[i])) throw new ArgumentOutOfRangeException(nameof(edges), "Every edge must be finite.");
            if (i > 0 && copy[i] <= copy[i - 1])
                throw new ArgumentException("The edges must be strictly increasing.", nameof(edges));
        }

        double minimum = copy[0];
        double maximum = copy[copy.Length - 1];
        string trimmed = Validate(name, minimum, maximum, copy.Length - 1, outOfRangePolicy, EvolutionDescriptorBinning.ExplicitEdges);
        return new EvolutionDescriptorDefinition(trimmed, minimum, maximum, copy.Length - 1, outOfRangePolicy,
            EvolutionDescriptorBinning.ExplicitEdges, copy, 0, 0, 0, Array.Empty<double>(), 0, 1);
    }

    /// <summary>Creates a definition whose equal-width bins span the smallest and largest value observed.</summary>
    /// <param name="name">The unique descriptor name.</param>
    /// <param name="initialMinimum">The lower bound used until two distinct values have been observed.</param>
    /// <param name="initialMaximum">The upper bound used until two distinct values have been observed.</param>
    /// <param name="binCount">The number of bins; it never changes, so the grid size is fixed.</param>
    /// <param name="outOfRangePolicy">How a value outside the current range is handled by a caller that bins without
    /// observing first. An archive observes every value before binning it, so its own candidates are always in range.
    /// Growth is not available, because the range already follows the values.</param>
    /// <returns>The adaptive min-max definition.</returns>
    /// <remarks>
    /// This is OpenEvolve's default feature scaling with one difference: when the range moves, every elite is re-keyed
    /// onto the new bins, where OpenEvolve leaves each program in the cell it was filed under when it arrived.
    /// </remarks>
    public static EvolutionDescriptorDefinition AdaptiveMinMax(
        string name,
        double initialMinimum,
        double initialMaximum,
        int binCount,
        EvolutionOutOfRangePolicy outOfRangePolicy = EvolutionOutOfRangePolicy.Clamp)
    {
        string trimmed = Validate(name, initialMinimum, initialMaximum, binCount, outOfRangePolicy,
            EvolutionDescriptorBinning.AdaptiveMinMax);
        return new EvolutionDescriptorDefinition(trimmed, initialMinimum, initialMaximum, binCount, outOfRangePolicy,
            EvolutionDescriptorBinning.AdaptiveMinMax, Array.Empty<double>(), 0, 0, 0, Array.Empty<double>(), 0, 1);
    }

    /// <summary>Creates a definition whose bins are bounded by quantiles of the values observed.</summary>
    /// <param name="name">The unique descriptor name.</param>
    /// <param name="initialMinimum">The lower bound used until two distinct values have been observed.</param>
    /// <param name="initialMaximum">The upper bound used until two distinct values have been observed.</param>
    /// <param name="binCount">The number of bins; it never changes, so the grid size is fixed.</param>
    /// <param name="outOfRangePolicy">As for <see cref="AdaptiveMinMax"/>. Growth is not available.</param>
    /// <param name="sampleCapacity">How many observed values are kept to place the quantiles; at least
    /// <paramref name="binCount"/> and at least 2.</param>
    /// <returns>The adaptive quantile definition.</returns>
    /// <remarks>
    /// <para>
    /// The outer edges are always the exact smallest and largest values observed; the interior edges are the
    /// <c>k / BinCount</c> quantiles of the kept values. Up to <paramref name="sampleCapacity"/> observations every
    /// value is kept and the quantiles are exact. Past that, the kept values are halved by keeping every other one in
    /// sorted order and only every second later value is recorded, then every fourth, and so on: a deterministic
    /// compaction that keeps memory and the per-observation cost bounded while every kept value stands for the same
    /// number of observations, so the quantiles stay representative of the whole run.
    /// </para>
    /// <para><b>For Beginners:</b> With 4 bins the cuts go where a quarter, half, and three quarters of the results
    /// so far lie, so each bin holds roughly a quarter of what the search has produced. If most programs are between
    /// 400 and 550 characters long, the bins concentrate there instead of being spread evenly over a range nobody
    /// reaches.</para>
    /// </remarks>
    public static EvolutionDescriptorDefinition AdaptiveQuantile(
        string name,
        double initialMinimum,
        double initialMaximum,
        int binCount,
        EvolutionOutOfRangePolicy outOfRangePolicy = EvolutionOutOfRangePolicy.Clamp,
        int sampleCapacity = DefaultQuantileSampleCapacity)
    {
        string trimmed = Validate(name, initialMinimum, initialMaximum, binCount, outOfRangePolicy,
            EvolutionDescriptorBinning.AdaptiveQuantile);
        if (sampleCapacity < 2 || sampleCapacity < binCount)
            throw new ArgumentOutOfRangeException(nameof(sampleCapacity), "The sample capacity must be at least 2 and at least the bin count.");
        return new EvolutionDescriptorDefinition(trimmed, initialMinimum, initialMaximum, binCount, outOfRangePolicy,
            EvolutionDescriptorBinning.AdaptiveQuantile, Array.Empty<double>(), 0, 0, 0, Array.Empty<double>(), sampleCapacity, 1);
    }

    /// <summary>Gets the unique descriptor name.</summary>
    public string Name { get; }

    /// <summary>Gets the finite lower bound; for an adaptive axis, the current one.</summary>
    public double Minimum { get; }

    /// <summary>Gets the finite upper bound; for an adaptive axis, the current one.</summary>
    public double Maximum { get; }

    /// <summary>Gets the number of bins within the range.</summary>
    public int BinCount { get; }

    /// <summary>Gets the out-of-range policy.</summary>
    public EvolutionOutOfRangePolicy OutOfRangePolicy { get; }

    /// <summary>Gets where the bin boundaries lie.</summary>
    public EvolutionDescriptorBinning Binning { get; }

    /// <summary>Gets whether the boundaries are learned from observed values.</summary>
    public bool IsAdaptive => Binning is EvolutionDescriptorBinning.AdaptiveMinMax or EvolutionDescriptorBinning.AdaptiveQuantile;

    /// <summary>Gets the bin boundaries when they are not evenly spaced, from <see cref="Minimum"/> to <see cref="Maximum"/>.</summary>
    /// <remarks>
    /// <see cref="BinCount"/> + 1 values for <see cref="EvolutionDescriptorBinning.ExplicitEdges"/>, and for
    /// <see cref="EvolutionDescriptorBinning.AdaptiveQuantile"/> once two distinct values have been observed. Empty
    /// otherwise. A quantile edge can repeat when many observations share one value; the bin between two equal edges
    /// is then empty.
    /// </remarks>
    public IReadOnlyList<double> Edges => _edgeView ??= Array.AsReadOnly(_edges);

    /// <summary>Gets how many values an adaptive axis has observed; 0 for a fixed axis.</summary>
    public long ObservationCount { get; }

    /// <summary>Gets the smallest value an adaptive axis has observed, or <c>null</c> before the first.</summary>
    public double? ObservedMinimum => ObservationCount == 0 ? null : _observedMinimum;

    /// <summary>Gets the largest value an adaptive axis has observed, or <c>null</c> before the first.</summary>
    public double? ObservedMaximum => ObservationCount == 0 ? null : _observedMaximum;

    /// <summary>Gets how many observed values an adaptive quantile axis keeps; 0 for every other mode.</summary>
    public int SampleCapacity { get; }

    /// <summary>Gets the kept observations of a quantile axis, sorted ascending.</summary>
    internal IReadOnlyList<double> Sample => _sample;

    /// <summary>Gets how many observations each newly kept value stands for; a power of two.</summary>
    internal long SampleStride { get; }

    /// <summary>Gets the number of physical cells contributed by this dimension.</summary>
    public int EffectiveBinCount => OutOfRangePolicy == EvolutionOutOfRangePolicy.OverflowBins
        ? checked(BinCount + 2)
        : BinCount;

    /// <summary>Attempts to map a descriptor value to a stable bin index.</summary>
    /// <param name="value">The value to bin.</param>
    /// <param name="bin">The resulting zero-based bin when successful.</param>
    /// <returns><c>true</c> when the value is finite and accepted by the configured policy.</returns>
    public bool TryGetBin(double value, out int bin)
    {
        bin = -1;
        if (!IsFinite(value)) return false;

        // Grow reports failure here exactly as Reject does, because widening the range is the archive's decision:
        // it owns the existing entries that a wider grid re-keys, and this method must stay a pure function of the
        // definition it is called on.
        if (value < Minimum)
        {
            if (OutOfRangePolicy is EvolutionOutOfRangePolicy.Reject or EvolutionOutOfRangePolicy.Grow) return false;
            bin = 0;
            return true;
        }

        if (value > Maximum)
        {
            if (OutOfRangePolicy is EvolutionOutOfRangePolicy.Reject or EvolutionOutOfRangePolicy.Grow) return false;
            bin = EffectiveBinCount - 1;
            return true;
        }

        int interior = InteriorBin(value);
        bin = OutOfRangePolicy == EvolutionOutOfRangePolicy.OverflowBins ? interior + 1 : interior;
        return true;
    }

    /// <summary>Gets the width of one interior bin when the bins are evenly spaced in value.</summary>
    /// <remarks>For logarithmic and edge-based bins this is the average width, <c>(Maximum - Minimum) / BinCount</c>.</remarks>
    public double BinWidth => (Maximum - Minimum) / BinCount;

    /// <summary>Gets the width of one bin in the space the bins are evenly spaced in: log-value for logarithmic bins.</summary>
    internal double UniformWidth => Binning == EvolutionDescriptorBinning.Logarithmic
        ? (Math.Log(Maximum) - Math.Log(Minimum)) / BinCount
        : BinWidth;

    /// <summary>Bins a value as if the range contained it, whatever the out-of-range policy says.</summary>
    /// <param name="value">The value to bin.</param>
    /// <param name="interiorBin">
    /// The resulting zero-based interior bin. This does not include the +1 offset that
    /// <see cref="TryGetBin"/> applies when overflow bins are enabled.
    /// </param>
    /// <returns><c>true</c> when the value is finite and inside the range.</returns>
    /// <remarks>
    /// Used by a growing archive to check that a widened definition really accepts the value it was widened for,
    /// before adopting it. <see cref="TryGetBin"/> cannot answer that question, because under
    /// <see cref="EvolutionOutOfRangePolicy.Grow"/> it deliberately reports every out-of-range value as unbinnable.
    /// </remarks>
    internal bool TryGetBinIgnoringPolicy(double value, out int interiorBin)
    {
        interiorBin = -1;
        if (!IsFinite(value) || value < Minimum || value > Maximum) return false;
        interiorBin = InteriorBin(value);
        return true;
    }

    private int InteriorBin(double value)
    {
        if (value == Maximum) return BinCount - 1;

        int interior;
        if (_edges.Length != 0)
        {
            // The last edge not above the value starts its bin, so a value on a repeated edge lands in the last of
            // the bins that start there.
            int low = 0;
            int high = _edges.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (_edges[middle] <= value) low = middle + 1;
                else high = middle;
            }
            interior = low - 1;
        }
        else
        {
            double normalized = Binning == EvolutionDescriptorBinning.Logarithmic
                ? (Math.Log(value) - Math.Log(Minimum)) / (Math.Log(Maximum) - Math.Log(Minimum))
                : (value - Minimum) / (Maximum - Minimum);
            interior = (int)(normalized * BinCount);
        }

        return Math.Max(0, Math.Min(BinCount - 1, interior));
    }

    /// <summary>Returns a definition widened to contain <paramref name="value"/>, keeping the bin width fixed.</summary>
    /// <param name="value">The finite value the widened definition must accept.</param>
    /// <returns>The widened definition, or this instance when the value already fits or cannot be reached.</returns>
    /// <remarks>
    /// <para>
    /// Holding the bin width constant is what makes growth safe: a cell covers the same span of values before and
    /// after, so an archive can move its existing elites onto the wider grid by rebinning them rather than
    /// re-evaluating them. Only whole bins are added, and only as many as the value needs, which makes growth
    /// order-independent: widening to reach one value and then a more extreme one lands on the same bounds as
    /// widening straight to the more extreme one. Logarithmic bins keep their log-width, so a non-positive value
    /// cannot be reached. Edge-based and adaptive definitions never widen.
    /// </para>
    /// <para><b>For Beginners:</b> The archive is a grid of pigeonholes. This adds whole rows of pigeonholes at one
    /// end, all the same size as the existing ones, so nothing already filed has to be re-measured.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite.</exception>
    public EvolutionDescriptorDefinition Widen(double value)
    {
        if (!IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value), "The value must be finite.");
        if (value >= Minimum && value <= Maximum) return this;
        if (Binning is not (EvolutionDescriptorBinning.Linear or EvolutionDescriptorBinning.Logarithmic)) return this;

        bool logarithmic = Binning == EvolutionDescriptorBinning.Logarithmic;
        if (logarithmic && value <= 0) return this;

        double width = UniformWidth;
        if (!IsFinite(width) || width <= 0) return this;

        double minimum = logarithmic ? Math.Log(Minimum) : Minimum;
        double maximum = logarithmic ? Math.Log(Maximum) : Maximum;
        double target = logarithmic ? Math.Log(value) : value;
        long bins = BinCount;

        if (target < minimum)
        {
            long needed = Math.Max(1, (long)Math.Ceiling((minimum - target) / width));
            minimum -= needed * width;
            bins += needed;
        }
        else
        {
            long needed = Math.Max(1, (long)Math.Ceiling((target - maximum) / width));
            maximum += needed * width;
            bins += needed;
        }

        if (logarithmic)
        {
            minimum = value < Minimum ? Math.Exp(minimum) : Minimum;
            maximum = value > Maximum ? Math.Exp(maximum) : Maximum;
        }

        if (bins > int.MaxValue / 2 || !IsFinite(minimum) || !IsFinite(maximum) || !IsFinite(maximum - minimum) ||
            maximum <= minimum || (logarithmic && minimum <= 0))
            return this;

        return logarithmic
            ? Logarithmic(Name, minimum, maximum, (int)bins, OutOfRangePolicy)
            : new EvolutionDescriptorDefinition(Name, minimum, maximum, (int)bins, OutOfRangePolicy);
    }

    /// <summary>Returns this adaptive definition refitted to one more observed value.</summary>
    /// <param name="value">The observed descriptor value. A non-finite value is not observed.</param>
    /// <returns>The refitted definition; this instance for a fixed axis or a non-finite value.</returns>
    /// <remarks>
    /// The bounds follow the observed range once it holds two distinct values; before that the initial bounds stay.
    /// The result is a pure function of this definition and <paramref name="value"/>, so observing the same values in
    /// the same order always produces the same bins.
    /// </remarks>
    public EvolutionDescriptorDefinition Observe(double value)
    {
        if (!IsAdaptive || !IsFinite(value)) return this;

        long index = ObservationCount;
        double lowest = index == 0 ? value : Math.Min(_observedMinimum, value);
        double highest = index == 0 ? value : Math.Max(_observedMaximum, value);
        double[] sample = _sample;
        long stride = SampleStride;
        if (Binning == EvolutionDescriptorBinning.AdaptiveQuantile && index % stride == 0)
        {
            sample = InsertSorted(_sample, value);
            if (sample.Length > SampleCapacity)
            {
                var halved = new double[(sample.Length + 1) / 2];
                for (int i = 0; i < halved.Length; i++) halved[i] = sample[i * 2];
                sample = halved;
                stride = checked(stride * 2);
            }
        }

        return Fit(Name, Minimum, Maximum, BinCount, OutOfRangePolicy, Binning,
            checked(index + 1), lowest, highest, sample, SampleCapacity, stride);
    }

    private static double[] InsertSorted(double[] sorted, double value)
    {
        int position = Array.BinarySearch(sorted, value);
        if (position < 0) position = ~position;
        var grown = new double[sorted.Length + 1];
        Array.Copy(sorted, 0, grown, 0, position);
        grown[position] = value;
        Array.Copy(sorted, position, grown, position + 1, sorted.Length - position);
        return grown;
    }

    /// <summary>Builds an adaptive definition from its observation state, placing the bounds and edges it implies.</summary>
    private static EvolutionDescriptorDefinition Fit(
        string name,
        double initialMinimum,
        double initialMaximum,
        int binCount,
        EvolutionOutOfRangePolicy policy,
        EvolutionDescriptorBinning binning,
        long count,
        double lowest,
        double highest,
        double[] sample,
        int sampleCapacity,
        long stride)
    {
        if (count == 0 || !(highest > lowest) || !IsFinite(highest - lowest))
        {
            return new EvolutionDescriptorDefinition(name, initialMinimum, initialMaximum, binCount, policy, binning,
                Array.Empty<double>(), count, lowest, highest, sample, sampleCapacity, stride);
        }

        double[] edges = Array.Empty<double>();
        if (binning == EvolutionDescriptorBinning.AdaptiveQuantile)
        {
            edges = new double[binCount + 1];
            edges[0] = lowest;
            edges[binCount] = highest;
            for (int k = 1; k < binCount; k++)
            {
                double quantile = sample.Length == 0 ? lowest : sample[(int)((long)k * sample.Length / binCount)];
                edges[k] = Math.Max(lowest, Math.Min(highest, quantile));
            }
        }

        return new EvolutionDescriptorDefinition(name, lowest, highest, binCount, policy, binning,
            edges, count, lowest, highest, sample, sampleCapacity, stride);
    }

    /// <summary>Rebuilds a definition from checkpointed state, validating it as untrusted input.</summary>
    /// <remarks>
    /// An adaptive definition's bounds and edges are recomputed from its observation state rather than read back, so a
    /// checkpoint cannot carry bins that its own observations do not imply.
    /// </remarks>
    internal static EvolutionDescriptorDefinition Restore(
        string name,
        double minimum,
        double maximum,
        int binCount,
        EvolutionOutOfRangePolicy policy,
        EvolutionDescriptorBinning binning,
        IReadOnlyList<double>? edges,
        long observationCount,
        double observedMinimum,
        double observedMaximum,
        IReadOnlyList<double>? sample,
        int sampleCapacity,
        long sampleStride)
    {
        if (!Enum.IsDefined(typeof(EvolutionDescriptorBinning), binning)) throw new ArgumentOutOfRangeException(nameof(binning));
        switch (binning)
        {
            case EvolutionDescriptorBinning.Linear:
                return new EvolutionDescriptorDefinition(name, minimum, maximum, binCount, policy);
            case EvolutionDescriptorBinning.Logarithmic:
                return Logarithmic(name, minimum, maximum, binCount, policy);
            case EvolutionDescriptorBinning.ExplicitEdges:
                EvolutionDescriptorDefinition explicitEdges = WithEdges(name, edges ?? Array.Empty<double>(), policy);
                if (explicitEdges.Minimum != minimum || explicitEdges.Maximum != maximum || explicitEdges.BinCount != binCount)
                    throw new ArgumentException("The edges do not match the recorded bounds and bin count.", nameof(edges));
                return explicitEdges;
        }

        // The recorded bounds are the initial ones until two distinct values arrive, and are then replaced by Fit.
        EvolutionDescriptorDefinition initial = binning == EvolutionDescriptorBinning.AdaptiveMinMax
            ? AdaptiveMinMax(name, minimum, maximum, binCount, policy)
            : AdaptiveQuantile(name, minimum, maximum, binCount, policy, sampleCapacity);
        if (observationCount < 0) throw new ArgumentOutOfRangeException(nameof(observationCount));
        double[] kept = (sample ?? Array.Empty<double>()).ToArray();
        if (observationCount == 0)
        {
            if (kept.Length != 0) throw new ArgumentException("Observations were recorded without an observation count.", nameof(sample));
            return initial;
        }

        if (!IsFinite(observedMinimum) || !IsFinite(observedMaximum) || observedMaximum < observedMinimum)
            throw new ArgumentOutOfRangeException(nameof(observedMinimum), "The observed range is invalid.");
        if (binning == EvolutionDescriptorBinning.AdaptiveMinMax)
        {
            if (kept.Length != 0 || sampleStride != 1)
                throw new ArgumentException("A min-max axis keeps no observed values.", nameof(sample));
        }
        else
        {
            if (sampleStride < 1 || (sampleStride & (sampleStride - 1)) != 0)
                throw new ArgumentOutOfRangeException(nameof(sampleStride), "The sample stride must be a power of two.");
            if (kept.Length == 0 || kept.Length > sampleCapacity || kept.Length > observationCount)
                throw new ArgumentException("The kept observations do not fit the observation count and capacity.", nameof(sample));
            for (int i = 0; i < kept.Length; i++)
            {
                if (!IsFinite(kept[i]) || kept[i] < observedMinimum || kept[i] > observedMaximum || (i > 0 && kept[i] < kept[i - 1]))
                    throw new ArgumentException("The kept observations must be sorted and inside the observed range.", nameof(sample));
            }
        }

        return Fit(initial.Name, minimum, maximum, binCount, policy, binning,
            observationCount, observedMinimum, observedMaximum, kept, initial.SampleCapacity, sampleStride);
    }

    /// <summary>Returns a stable, culture-independent representation suitable for compatibility hashes.</summary>
    /// <remarks>
    /// A linear definition keeps the representation it has always had, so existing definition hashes and checkpoints
    /// stay valid. Every other mode appends the mode, its edges, and any observation state.
    /// </remarks>
    public string ToCanonicalString()
    {
        var parts = new List<string>
        {
            Name.Length.ToString(CultureInfo.InvariantCulture) + ":" + Name,
            EvolutionHash.EncodeDouble(Minimum),
            EvolutionHash.EncodeDouble(Maximum),
            BinCount.ToString(CultureInfo.InvariantCulture),
            ((int)OutOfRangePolicy).ToString(CultureInfo.InvariantCulture)
        };
        if (Binning == EvolutionDescriptorBinning.Linear) return string.Join("|", parts);

        parts.Add("binning:" + ((int)Binning).ToString(CultureInfo.InvariantCulture));
        parts.Add("edges:" + _edges.Length.ToString(CultureInfo.InvariantCulture));
        parts.AddRange(_edges.Select(EvolutionHash.EncodeDouble));
        if (IsAdaptive)
        {
            parts.Add("observed:" + ObservationCount.ToString(CultureInfo.InvariantCulture));
            parts.Add(EvolutionHash.EncodeDouble(_observedMinimum));
            parts.Add(EvolutionHash.EncodeDouble(_observedMaximum));
            parts.Add("sample:" + SampleCapacity.ToString(CultureInfo.InvariantCulture) + ":" +
                SampleStride.ToString(CultureInfo.InvariantCulture) + ":" + _sample.Length.ToString(CultureInfo.InvariantCulture));
            parts.AddRange(_sample.Select(EvolutionHash.EncodeDouble));
        }

        return string.Join("|", parts);
    }

    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string Validate(
        string name,
        double minimum,
        double maximum,
        int binCount,
        EvolutionOutOfRangePolicy outOfRangePolicy,
        EvolutionDescriptorBinning binning)
    {
        Guard.NotNullOrWhiteSpace(name);
        if (!IsFinite(minimum)) throw new ArgumentOutOfRangeException(nameof(minimum), "The bound must be finite.");
        if (!IsFinite(maximum)) throw new ArgumentOutOfRangeException(nameof(maximum), "The bound must be finite.");
        if (maximum <= minimum) throw new ArgumentException("The maximum must be greater than the minimum.", nameof(maximum));
        if (!IsFinite(maximum - minimum)) throw new ArgumentOutOfRangeException(nameof(maximum), "The descriptor span must be finite.");
        Guard.Positive(binCount);
        if (outOfRangePolicy == EvolutionOutOfRangePolicy.OverflowBins && binCount > int.MaxValue - 2)
            throw new ArgumentOutOfRangeException(nameof(binCount));
        if (!Enum.IsDefined(typeof(EvolutionOutOfRangePolicy), outOfRangePolicy))
            throw new ArgumentOutOfRangeException(nameof(outOfRangePolicy));
        if (binning == EvolutionDescriptorBinning.Logarithmic && minimum <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimum), "A logarithmic axis needs a positive minimum.");
        if (outOfRangePolicy == EvolutionOutOfRangePolicy.Grow &&
            binning is not (EvolutionDescriptorBinning.Linear or EvolutionDescriptorBinning.Logarithmic))
            throw new ArgumentException(
                "Only linear and logarithmic axes can grow: explicit edges have no bin width to extend by, and an " +
                "adaptive axis already follows the observed values.", nameof(outOfRangePolicy));
        return name.Trim();
    }
}
