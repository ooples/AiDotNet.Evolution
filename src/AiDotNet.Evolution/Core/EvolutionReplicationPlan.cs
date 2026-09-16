using System.Globalization;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

/// <summary>Frozen scalar replication policy for independent, identically distributed bounded measurements.</summary>
/// <remarks>
/// Bounds must be known before sampling, not estimated from observed extrema. Optional precision stopping uses
/// Hoeffding intervals with a union bound over the finite planned looks. Confidence applies to one batch, not to
/// an adaptively selected population of candidates. This does not validate independence or evaluator correctness.
/// </remarks>
public sealed class EvolutionReplicationPlan
{
    /// <summary>Creates a bounded replication policy. A zero normalized interval-width target uses a fixed sample count.</summary>
    public EvolutionReplicationPlan(int minimumSamples, int maximumSamples, double minimumQuality, double maximumQuality,
        decimal maximumCostPerSample, double confidence = 0.95, double normalizedWidthTarget = 0,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        if (minimumSamples < 2 || maximumSamples < minimumSamples || maximumSamples > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumSamples), "Require 2 <= minimum <= maximum <= 256.");
        if (!EvolutionDescriptorDefinition.IsFinite(minimumQuality) || !EvolutionDescriptorDefinition.IsFinite(maximumQuality) ||
            maximumQuality <= minimumQuality || !EvolutionDescriptorDefinition.IsFinite(maximumQuality - minimumQuality))
            throw new ArgumentException("Declare finite, ordered quality bounds with a finite span.", nameof(maximumQuality));
        if (maximumCostPerSample < 0 || maximumCostPerSample > EvolutionResources.MaximumAmount)
            throw new ArgumentOutOfRangeException(nameof(maximumCostPerSample));
        if (!EvolutionDescriptorDefinition.IsFinite(confidence) || confidence < 0.8 || confidence > 0.999999)
            throw new ArgumentOutOfRangeException(nameof(confidence));
        if (!EvolutionDescriptorDefinition.IsFinite(normalizedWidthTarget) || normalizedWidthTarget < 0 || normalizedWidthTarget > 1)
            throw new ArgumentOutOfRangeException(nameof(normalizedWidthTarget));
        if (!Enum.IsDefined(typeof(EvolutionOptimizationDirection), direction)) throw new ArgumentOutOfRangeException(nameof(direction));
        MinimumSamples = minimumSamples; MaximumSamples = maximumSamples; MinimumQuality = minimumQuality;
        MaximumQuality = maximumQuality; MaximumCostPerSample = maximumCostPerSample; Confidence = confidence;
        NormalizedWidthTarget = normalizedWidthTarget; Direction = direction;
        VersionHash = EvolutionHash.Combine(new[] { "bounded-replication-plan-v1", minimumSamples.ToString(CultureInfo.InvariantCulture),
            maximumSamples.ToString(CultureInfo.InvariantCulture), Bits(minimumQuality), Bits(maximumQuality),
            maximumCostPerSample.ToString(CultureInfo.InvariantCulture), Bits(confidence), Bits(normalizedWidthTarget), direction.ToString() });
    }

    /// <summary>Gets the earliest precision decision point.</summary>
    public int MinimumSamples { get; }
    /// <summary>Gets the fixed sample horizon, at most 256.</summary>
    public int MaximumSamples { get; }
    /// <summary>Gets the declared lower support bound.</summary>
    public double MinimumQuality { get; }
    /// <summary>Gets the declared upper support bound.</summary>
    public double MaximumQuality { get; }
    /// <summary>Gets the externally enforced maximum reserved before each measurement.</summary>
    public decimal MaximumCostPerSample { get; }
    /// <summary>Gets the confidence level for this batch's bounded-mean interval.</summary>
    public double Confidence { get; }
    /// <summary>Gets the desired full interval width divided by the declared quality span; zero disables early stopping.</summary>
    public double NormalizedWidthTarget { get; }
    /// <summary>Gets the required scalar direction of every measurement.</summary>
    public EvolutionOptimizationDirection Direction { get; }
    /// <summary>Gets the semantic policy identity.</summary>
    public string VersionHash { get; }

    internal double Span => MaximumQuality - MinimumQuality;
    internal void Bounds(double mean, int samples, out double lower, out double upper)
    {
        // Round outward so an interval cannot collapse to zero at a large offset or subnormal span.
        double width = Adjacent(Radius(samples) * Span, upward: true);
        lower = Math.Max(MinimumQuality, Adjacent(mean - width, upward: false));
        upper = Math.Min(MaximumQuality, Adjacent(mean + width, upward: true));
    }
    internal double Radius(int samples)
    {
        int looks = NormalizedWidthTarget > 0 ? MaximumSamples - MinimumSamples + 1 : 1;
        return Math.Min(1, Math.Sqrt(Math.Log(2d * looks / (1 - Confidence)) / (2d * samples)));
    }
    private static string Bits(double value) => BitConverter.DoubleToInt64Bits(value == 0 ? 0 : value).ToString("x16", CultureInfo.InvariantCulture);
    private static double Adjacent(double value, bool upward)
    {
        if (double.IsInfinity(value)) return value;
        if (value == 0) return upward ? double.Epsilon : -double.Epsilon;
        long bits = BitConverter.DoubleToInt64Bits(value);
        return BitConverter.Int64BitsToDouble(bits + ((value > 0) == upward ? 1 : -1));
    }
}

/// <summary>Separates search sampling and independent confirmation identities and accounting stages.</summary>
public enum EvolutionReplicationPurpose
{
    /// <summary>Measurements available to the search policy.</summary>
    Search,
    /// <summary>Fresh confirmation measurements; keep hidden checks and their feedback outside search.</summary>
    Confirmation
}

/// <summary>Why a bounded replication batch stopped.</summary>
public enum EvolutionReplicationStopReason
{
    /// <summary>The full planned sample count completed.</summary>
    Completed,
    /// <summary>The predeclared interval width was reached after the minimum count.</summary>
    PrecisionReached,
    /// <summary>A reservation was refused before dispatch.</summary>
    BudgetExhausted,
    /// <summary>A returned score violated its status, direction, feasibility or declared support contract.</summary>
    InvalidMeasurement,
    /// <summary>Actual returned cost exceeded the declared sample maximum.</summary>
    MaximumCostExceeded,
    /// <summary>The evaluator did not return a usable receipt; the reservation maximum was charged as unknown.</summary>
    UnknownCost,
    /// <summary>Cancellation stopped the batch; any in-flight unknown work remains charged.</summary>
    Canceled
}

/// <summary>Immutable identity and fresh deterministic stream for one independently executed replicate.</summary>
public sealed class EvolutionReplicateContext
{
    internal EvolutionReplicateContext(string batchIdentity, int index, EvolutionReplicationPurpose purpose, EvolutionEvaluationContext source)
    {
        BatchIdentity = batchIdentity; Index = index; Purpose = purpose;
        SampleIdentity = EvolutionHash.Combine(new[] { batchIdentity, index.ToString(CultureInfo.InvariantCulture) });
        EvaluationContext = new EvolutionEvaluationContext(source.EvaluationId, source.RootSeed,
            ulong.Parse(SampleIdentity.Substring(0, 16), NumberStyles.HexNumber, CultureInfo.InvariantCulture), source.AttemptCount);
    }
    /// <summary>Gets the opaque full batch identity.</summary>
    public string BatchIdentity { get; }
    /// <summary>Gets the opaque full measurement identity.</summary>
    public string SampleIdentity { get; }
    /// <summary>Gets the zero-based replicate index, not an engine evaluation ID.</summary>
    public int Index { get; }
    /// <summary>Gets the sampling purpose, which participates in identity.</summary>
    public EvolutionReplicationPurpose Purpose { get; }
    /// <summary>Gets a fresh stream while preserving the originating evaluation ID, root seed and attempt.</summary>
    public EvolutionEvaluationContext EvaluationContext { get; }
}

/// <summary>A compact immutable measurement receipt; raw artifacts and diagnostics are deliberately not retained.</summary>
public sealed class EvolutionReplicateMeasurement
{
    internal EvolutionReplicateMeasurement(EvolutionReplicateContext context, EvolutionEvaluationStatus status,
        double? quality, decimal chargedCostUnits, bool unknownCost, double? reportedCostUnits = null,
        EvolutionMeasurementOrigin? measurementOrigin = null)
    { Context = context; Status = status; Quality = quality; ChargedCostUnits = chargedCostUnits; UnknownCost = unknownCost; ReportedCostUnits = reportedCostUnits; MeasurementOrigin = measurementOrigin; }
    /// <summary>Gets the sample identity and stream.</summary>
    public EvolutionReplicateContext Context { get; }
    /// <summary>Gets the reported measurement status, or Failed/Canceled when no result returned.</summary>
    public EvolutionEvaluationStatus Status { get; }
    /// <summary>Gets the reported scalar, including rejected out-of-support measurements.</summary>
    public double? Quality { get; }
    /// <summary>Gets actual reported cost or the conservative maximum for unknown work.</summary>
    public decimal ChargedCostUnits { get; }
    /// <summary>Gets whether exact consumption was unavailable.</summary>
    public bool UnknownCost { get; }
    /// <summary>Gets the returned cost, including values that the decimal ledger cannot represent; null means no receipt.</summary>
    public double? ReportedCostUnits { get; }
    /// <summary>Gets supplied provenance, including invalid/reused receipts excluded from successful statistics.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvolutionMeasurementOrigin? MeasurementOrigin { get; }
}

/// <summary>Bounded batch evidence. Failed, canceled and under-budget batches never expose a successful mean or interval.</summary>
public sealed class EvolutionReplicationReport
{
    internal EvolutionReplicationReport(string identity, EvolutionReplicationPlan plan, EvolutionReplicationStopReason reason,
        IEnumerable<EvolutionReplicateMeasurement> samples, double mean, double deviationScale, double scaledSquares)
    {
        BatchIdentity = identity; Plan = plan; StopReason = reason; Samples = Array.AsReadOnly(samples.ToArray());
        ChargedCostUnits = Samples.Sum(sample => sample.ChargedCostUnits);
        if (reason is not (EvolutionReplicationStopReason.Completed or EvolutionReplicationStopReason.PrecisionReached)) return;
        MeanQuality = mean;
        double normalizedScale = deviationScale / plan.Span;
        NormalizedSampleVariance = normalizedScale * normalizedScale * scaledSquares / (Samples.Count - 1);
        StandardError = deviationScale * Math.Sqrt(scaledSquares / ((double)Samples.Count * (Samples.Count - 1)));
        plan.Bounds(mean, Samples.Count, out double lower, out double upper);
        LowerBound = lower; UpperBound = upper;
    }
    /// <summary>Gets the opaque identity tying evaluator, plan, candidate, context and purpose together.</summary>
    public string BatchIdentity { get; }
    /// <summary>Gets the immutable predeclared plan.</summary>
    public EvolutionReplicationPlan Plan { get; }
    /// <summary>Gets why sampling stopped.</summary>
    public EvolutionReplicationStopReason StopReason { get; }
    /// <summary>Gets all dispatched measurements, including failures and unknown costs.</summary>
    public IReadOnlyList<EvolutionReplicateMeasurement> Samples { get; }
    /// <summary>Gets the sum of actual and conservatively charged unknown work.</summary>
    public decimal ChargedCostUnits { get; }
    /// <summary>Gets whether the batch satisfied its measurement contract and stopping policy.</summary>
    public bool IsComplete => MeanQuality.HasValue;
    /// <summary>Gets the sample mean only for a successful batch.</summary>
    public double? MeanQuality { get; }
    /// <summary>Gets unbiased sample variance divided by the squared support span; extremely tiny ratios may underflow.
    /// StandardError is computed independently with scaled arithmetic.</summary>
    public double? NormalizedSampleVariance { get; }
    /// <summary>Gets the descriptive standard error, not an optional-stopping confidence interval.</summary>
    public double? StandardError { get; }
    /// <summary>Gets the support-clipped lower Hoeffding bound for one batch.</summary>
    public double? LowerBound { get; }
    /// <summary>Gets the support-clipped upper Hoeffding bound for one batch.</summary>
    public double? UpperBound { get; }
}
