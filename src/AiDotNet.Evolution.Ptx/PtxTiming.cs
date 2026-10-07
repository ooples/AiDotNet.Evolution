using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Ptx;

/// <summary>Order statistics of post-warm-up kernel times, in milliseconds.</summary>
/// <remarks>The median and the nearest-rank P95 are computed exactly as AiDotNet.Tensors' <c>KernelTimingStatistics</c>
/// computes them, so evidence exported from here reproduces the same numbers there. Quartiles use linear interpolation
/// between order statistics (the common "type 7" definition).</remarks>
[Experimental("AIDEVO005")]
public sealed class PtxTimingStatistics
{
    private PtxTimingStatistics(double[] sorted)
    {
        Samples = Array.AsReadOnly(sorted);
        Median = Quantile(sorted, 0.5);
        Q1 = Quantile(sorted, 0.25);
        Q3 = Quantile(sorted, 0.75);
        P95 = sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * 0.95d) - 1)];
    }

    /// <summary>The fewest samples accepted.</summary>
    public const int MinimumSampleCount = 3;

    /// <summary>Gets the samples, ascending.</summary>
    public IReadOnlyList<double> Samples { get; }
    /// <summary>Gets the sample count.</summary>
    public int SampleCount => Samples.Count;
    /// <summary>Gets the median.</summary>
    public double Median { get; }
    /// <summary>Gets the first quartile.</summary>
    public double Q1 { get; }
    /// <summary>Gets the third quartile.</summary>
    public double Q3 { get; }
    /// <summary>Gets the interquartile range.</summary>
    public double Iqr => Q3 - Q1;
    /// <summary>Gets the nearest-rank 95th percentile.</summary>
    public double P95 { get; }
    /// <summary>Gets the fastest sample.</summary>
    public double Minimum => Samples[0];
    /// <summary>Gets the slowest sample.</summary>
    public double Maximum => Samples[Samples.Count - 1];

    /// <summary>Computes statistics.</summary>
    /// <param name="milliseconds">At least three finite, positive samples.</param>
    /// <returns>The statistics.</returns>
    /// <exception cref="ArgumentException">Too few samples, or one is not finite and positive.</exception>
    public static PtxTimingStatistics FromSamples(IEnumerable<double> milliseconds)
    {
        if (milliseconds is null) throw new ArgumentNullException(nameof(milliseconds));
        double[] sorted = milliseconds.ToArray();
        if (sorted.Length < MinimumSampleCount) throw new ArgumentException("At least " + MinimumSampleCount + " samples are required.", nameof(milliseconds));
        if (sorted.Any(value => !double.IsFinite(value) || value <= 0)) throw new ArgumentException("Every sample must be finite and positive.", nameof(milliseconds));
        Array.Sort(sorted);
        return new PtxTimingStatistics(sorted);
    }

    private static double Quantile(double[] sorted, double q)
    {
        if (q == 0.5 && sorted.Length % 2 == 0) return sorted[sorted.Length / 2 - 1] + (sorted[sorted.Length / 2] - sorted[sorted.Length / 2 - 1]) / 2;
        if (q == 0.5) return sorted[sorted.Length / 2];
        double position = (sorted.Length - 1) * q;
        int lower = (int)Math.Floor(position);
        int upper = Math.Min(sorted.Length - 1, lower + 1);
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }
}

/// <summary>One interleaved candidate/incumbent measurement.</summary>
[Experimental("AIDEVO005")]
public readonly record struct PtxPairedSample
{
    /// <summary>Creates a pair.</summary>
    /// <param name="candidateMilliseconds">The candidate's time; finite and positive.</param>
    /// <param name="incumbentMilliseconds">The incumbent's time; finite and positive.</param>
    [JsonConstructor]
    public PtxPairedSample(double candidateMilliseconds, double incumbentMilliseconds)
    {
        if (!double.IsFinite(candidateMilliseconds) || candidateMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(candidateMilliseconds));
        if (!double.IsFinite(incumbentMilliseconds) || incumbentMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(incumbentMilliseconds));
        CandidateMilliseconds = candidateMilliseconds;
        IncumbentMilliseconds = incumbentMilliseconds;
    }

    /// <summary>Gets the candidate's time.</summary>
    public double CandidateMilliseconds { get; }
    /// <summary>Gets the incumbent's time.</summary>
    public double IncumbentMilliseconds { get; }
    /// <summary>Gets incumbent / candidate; above one favours the candidate.</summary>
    [JsonIgnore]
    public double Speedup => IncumbentMilliseconds / CandidateMilliseconds;
}

/// <summary>Sealed paired-replay evidence: interleaved pairs plus a separately calibrated incumbent/incumbent noise floor.</summary>
/// <remarks>This mirrors AiDotNet.Tensors' <c>KernelTuningPairedEvidence</c> and its promotion gate exactly: the median
/// within-pair speedup must reach both the minimum promotion ratio and the calibrated noise ratio, the empirical lower 5%
/// speedup must be at least one, and the candidate's P95 must not regress past the allowed ratio. The noise ratio is the
/// P95 of <c>max(s, 1/s)</c> over identical incumbent pairs, so a "win" smaller than the machine's own jitter is refused.</remarks>
[Experimental("AIDEVO005")]
public sealed class PtxPairedTimingEvidence
{
    /// <summary>The fewest pairs, for both the holdout and the control, the gate accepts.</summary>
    public const int MinimumSampleCount = 7;

    /// <summary>Creates evidence.</summary>
    /// <param name="samples">The candidate/incumbent pairs.</param>
    /// <param name="calibratedNoiseRatio">The incumbent/incumbent noise ratio, at least one.</param>
    /// <exception cref="ArgumentException">Fewer than <see cref="MinimumSampleCount"/> pairs, or an invalid noise ratio.</exception>
    [JsonConstructor]
    public PtxPairedTimingEvidence(IReadOnlyList<PtxPairedSample> samples, double calibratedNoiseRatio)
    {
        PtxPairedSample[] copy = (samples ?? throw new ArgumentNullException(nameof(samples))).ToArray();
        if (copy.Length < MinimumSampleCount) throw new ArgumentException("At least " + MinimumSampleCount + " pairs are required.", nameof(samples));
        if (!double.IsFinite(calibratedNoiseRatio) || calibratedNoiseRatio < 1) throw new ArgumentOutOfRangeException(nameof(calibratedNoiseRatio));
        Samples = Array.AsReadOnly(copy);
        CalibratedNoiseRatio = calibratedNoiseRatio;
        CandidateTiming = PtxTimingStatistics.FromSamples(copy.Select(s => s.CandidateMilliseconds));
        IncumbentTiming = PtxTimingStatistics.FromSamples(copy.Select(s => s.IncumbentMilliseconds));
        double[] speedups = copy.Select(s => s.Speedup).OrderBy(v => v).ToArray();
        MedianSpeedup = speedups.Length % 2 == 0 ? (speedups[speedups.Length / 2 - 1] + speedups[speedups.Length / 2]) / 2 : speedups[speedups.Length / 2];
        LowerSpeedupBound = speedups[Math.Max(0, (int)Math.Floor(speedups.Length * 0.05d))];
    }

    /// <summary>Gets the raw pairs in measurement order.</summary>
    public IReadOnlyList<PtxPairedSample> Samples { get; }
    /// <summary>Gets the incumbent/incumbent noise ratio.</summary>
    public double CalibratedNoiseRatio { get; }
    /// <summary>Gets the candidate's statistics.</summary>
    [JsonIgnore]
    public PtxTimingStatistics CandidateTiming { get; }
    /// <summary>Gets the incumbent's statistics.</summary>
    [JsonIgnore]
    public PtxTimingStatistics IncumbentTiming { get; }
    /// <summary>Gets the median within-pair speedup.</summary>
    [JsonIgnore]
    public double MedianSpeedup { get; }
    /// <summary>Gets the empirical lower 5% speedup.</summary>
    [JsonIgnore]
    public double LowerSpeedupBound { get; }
    /// <summary>Gets candidate P95 / incumbent P95.</summary>
    [JsonIgnore]
    public double P95LatencyRatio => CandidateTiming.P95 / IncumbentTiming.P95;

    /// <summary>Computes the noise ratio from identical incumbent/incumbent pairs.</summary>
    /// <param name="controls">At least <see cref="MinimumSampleCount"/> control pairs.</param>
    /// <returns>The P95 of <c>max(s, 1/s)</c>.</returns>
    public static double NoiseRatio(IEnumerable<PtxPairedSample> controls)
    {
        double[] deviations = (controls ?? throw new ArgumentNullException(nameof(controls)))
            .Select(s => Math.Max(s.Speedup, 1 / s.Speedup)).OrderBy(v => v).ToArray();
        if (deviations.Length < MinimumSampleCount) throw new ArgumentException("At least " + MinimumSampleCount + " control pairs are required.", nameof(controls));
        return deviations[Math.Max(0, (int)Math.Ceiling(deviations.Length * 0.95d) - 1)];
    }

    /// <summary>Applies the Tensors promotion gate.</summary>
    /// <param name="minimumPromotionRatio">The least median speedup worth deploying, at least one (Tensors uses 1.05).</param>
    /// <param name="maximumP95LatencyRatio">The largest allowed tail ratio (Tensors uses 1.0).</param>
    /// <returns>Whether the candidate qualifies.</returns>
    public bool QualifiesForPromotion(double minimumPromotionRatio, double maximumP95LatencyRatio)
    {
        if (!double.IsFinite(minimumPromotionRatio) || minimumPromotionRatio < 1) throw new ArgumentOutOfRangeException(nameof(minimumPromotionRatio));
        if (!double.IsFinite(maximumP95LatencyRatio) || maximumP95LatencyRatio <= 0) throw new ArgumentOutOfRangeException(nameof(maximumP95LatencyRatio));
        return MedianSpeedup >= Math.Max(minimumPromotionRatio, CalibratedNoiseRatio) && LowerSpeedupBound >= 1 && P95LatencyRatio <= maximumP95LatencyRatio;
    }
}