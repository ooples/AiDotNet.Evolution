namespace AiDotNet.Evolution.Surrogates;

/// <summary>Immutable support, reliability and explicit work-unit tariff for the optional numeric backend.</summary>
public sealed class NumericSurrogateOptions
{
    /// <summary>Declares quality support and backend checks. Prices are supplied work-unit tariffs, not measured CPU or dollars.</summary>
    public NumericSurrogateOptions(double qualityMinimum, double qualityMaximum, int neighbors = 3,
        double maximumDistance = 0.25, double calibrationQuantile = 0.9, double minimumValidationCoverage = 0.6,
        double maximumRelativeMeanAbsoluteError = 0.15, decimal coordinateCost = 0.000001m,
        decimal trainingBaseCost = 0.02m, decimal inferenceBaseCost = 0.005m)
    {
        if (!Finite(qualityMinimum) || !Finite(qualityMaximum) || qualityMaximum <= qualityMinimum || !Finite(qualityMaximum - qualityMinimum))
            throw new ArgumentOutOfRangeException(nameof(qualityMaximum));
        if (neighbors < 1 || neighbors > 16) throw new ArgumentOutOfRangeException(nameof(neighbors));
        if (!Finite(maximumDistance) || maximumDistance <= 0 || maximumDistance > 1) throw new ArgumentOutOfRangeException(nameof(maximumDistance));
        if (!Finite(calibrationQuantile) || calibrationQuantile < 0.5 || calibrationQuantile >= 1) throw new ArgumentOutOfRangeException(nameof(calibrationQuantile));
        if (!Finite(minimumValidationCoverage) || minimumValidationCoverage <= 0 || minimumValidationCoverage > 1)
            throw new ArgumentOutOfRangeException(nameof(minimumValidationCoverage));
        if (!Finite(maximumRelativeMeanAbsoluteError) || maximumRelativeMeanAbsoluteError < 0 || maximumRelativeMeanAbsoluteError > 1)
            throw new ArgumentOutOfRangeException(nameof(maximumRelativeMeanAbsoluteError));
        if (coordinateCost < 0 || coordinateCost > 1 || trainingBaseCost < 0 || trainingBaseCost > 1000 || inferenceBaseCost < 0 || inferenceBaseCost > 1000)
            throw new ArgumentOutOfRangeException(nameof(coordinateCost));
        QualityMinimum = qualityMinimum; QualityMaximum = qualityMaximum; Neighbors = neighbors; MaximumDistance = maximumDistance;
        CalibrationQuantile = calibrationQuantile; MinimumValidationCoverage = minimumValidationCoverage;
        MaximumRelativeMeanAbsoluteError = maximumRelativeMeanAbsoluteError; CoordinateCost = coordinateCost;
        TrainingBaseCost = trainingBaseCost; InferenceBaseCost = inferenceBaseCost;
        VersionHash = EvolutionHash.Combine(new[] { "numeric-surrogate-options-v1", EvolutionHash.EncodeDouble(qualityMinimum),
            EvolutionHash.EncodeDouble(qualityMaximum), neighbors.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EvolutionHash.EncodeDouble(maximumDistance), EvolutionHash.EncodeDouble(calibrationQuantile),
            EvolutionHash.EncodeDouble(minimumValidationCoverage), EvolutionHash.EncodeDouble(maximumRelativeMeanAbsoluteError),
            coordinateCost.ToString(System.Globalization.CultureInfo.InvariantCulture), trainingBaseCost.ToString(System.Globalization.CultureInfo.InvariantCulture),
            inferenceBaseCost.ToString(System.Globalization.CultureInfo.InvariantCulture) });
    }
    /// <summary>Gets the declared finite lower quality bound.</summary>
    public double QualityMinimum { get; }
    /// <summary>Gets the declared finite upper quality bound.</summary>
    public double QualityMaximum { get; }
    /// <summary>Gets the maximum number of interpolating neighbors.</summary>
    public int Neighbors { get; }
    /// <summary>Gets the maximum supported nearest-neighbor RMS distance in normalized typed features.</summary>
    public double MaximumDistance { get; }
    /// <summary>Gets the empirical residual quantile fitted only on the calibration partition.</summary>
    public double CalibrationQuantile { get; }
    /// <summary>Gets the minimum empirical coverage on the distinct validation partition.</summary>
    public double MinimumValidationCoverage { get; }
    /// <summary>Gets the maximum validation mean absolute error divided by the declared quality span.</summary>
    public double MaximumRelativeMeanAbsoluteError { get; }
    /// <summary>Gets the declared price per encoded feature or processed distance coordinate.</summary>
    public decimal CoordinateCost { get; }
    /// <summary>Gets the declared fixed fitting tariff, including bookkeeping.</summary>
    public decimal TrainingBaseCost { get; }
    /// <summary>Gets the declared fixed inference tariff, including bookkeeping.</summary>
    public decimal InferenceBaseCost { get; }
    /// <summary>Gets the fingerprint of every setting and tariff.</summary>
    public string VersionHash { get; }
    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

/// <summary>Detached fitting diagnostics; empirical validation is not independent deployment confirmation.</summary>
public sealed class NumericSurrogateValidation
{
    internal NumericSurrogateValidation(int training, int calibration, int validation, double radius, double error,
        double coverage, bool reliable, string reason, long work)
    {
        TrainingGroups = training; CalibrationGroups = calibration; ValidationGroups = validation; ResidualRadius = radius;
        RelativeMeanAbsoluteError = error; EmpiricalCoverage = coverage; IsReliable = reliable; Reason = reason; CoordinateWork = work;
    }
    /// <summary>Gets distinct genome groups used to fit interpolation, not individual replicate rows.</summary>
    public int TrainingGroups { get; }
    /// <summary>Gets disjoint genome groups used to fit the residual radius.</summary>
    public int CalibrationGroups { get; }
    /// <summary>Gets disjoint genome groups used only for reliability checks.</summary>
    public int ValidationGroups { get; }
    /// <summary>Gets the empirical absolute-error radius on the original quality scale.</summary>
    public double ResidualRadius { get; }
    /// <summary>Gets held-out validation MAE divided by the declared quality span; zero when unavailable.</summary>
    public double RelativeMeanAbsoluteError { get; }
    /// <summary>Gets validation fraction inside the calibrated radius; zero when unavailable.</summary>
    public double EmpiricalCoverage { get; }
    /// <summary>Gets whether all declared reliability checks pass.</summary>
    public bool IsReliable { get; }
    /// <summary>Gets the explicit acceptance/fallback reason.</summary>
    public string Reason { get; }
    /// <summary>Gets actual encoded-feature and distance-coordinate work in this fit.</summary>
    public long CoordinateWork { get; }
}
