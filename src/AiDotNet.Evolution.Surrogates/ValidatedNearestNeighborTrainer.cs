using System.Globalization;

namespace AiDotNet.Evolution.Surrogates;

/// <summary>Optional bounded numeric interpolation with disjoint fit, residual-calibration and validation genome groups.</summary>
/// <remarks>
/// Fits detached deterministic models from 2–256 fresh records and at most 128 normalized typed features.
/// Replicate records for one genome stay in one partition and contribute an equally weighted mean of record qualities.
/// At least 18 distinct genomes are needed: at least eight fit groups, five calibration groups and five validation groups.
/// Partitions are deterministic genome-hash splits of supplied search data, not sealed deployment confirmation.
/// Adaptive collection and drift can invalidate generalization; empirical held-out coverage is not a formal coverage guarantee.
/// All declared tariffs are explicit work-unit prices, not measured elapsed CPU or monetary costs.
/// </remarks>
public sealed class ValidatedNearestNeighborTrainer : IEvolutionSurrogateTrainer<EvolutionSearchGenome>
{
    private readonly EvolutionSearchSpace _space;
    private readonly NumericSurrogateOptions _options;

    /// <summary>Creates a reusable stateless trainer; callers own and meter fresh observation acquisition.</summary>
    public ValidatedNearestNeighborTrainer(EvolutionSearchSpace space, NumericSurrogateOptions options)
    {
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (space.FeatureCount > 128) throw new ArgumentException("The numeric backend supports at most 128 features.", nameof(space));
        VersionHash = EvolutionHash.Combine(new[] { "validated-knn-v1-group-split", space.VersionHash, options.VersionHash });
    }
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <summary>Gets a conservative fitting reservation for the supplied bound, including feature encoding and all calibration/validation distances.</summary>
    public EvolutionResources MaximumTrainingCost(int observations = 256)
    {
        if (observations < 2 || observations > 256) throw new ArgumentOutOfRangeException(nameof(observations));
        return Cost(_options.TrainingBaseCost, (long)observations * _space.FeatureCount * (observations + 1));
    }
    /// <summary>Gets a conservative inference reservation, including encoding and distance work against all possible fit groups.</summary>
    public EvolutionResources MaximumInferenceCost(int candidates = 64, int trainingObservations = 256)
    {
        if (candidates < 1 || candidates > 64 || trainingObservations < 1 || trainingObservations > 256)
            throw new ArgumentOutOfRangeException(nameof(candidates));
        return Cost(_options.InferenceBaseCost, (long)candidates * _space.FeatureCount * (trainingObservations + 1));
    }

    /// <inheritdoc/>
    public ValueTask<EvolutionResourceResult<IEvolutionSurrogateModel<EvolutionSearchGenome>>> FitAsync(
        IReadOnlyList<EvolutionSurrogateObservation<EvolutionSearchGenome>> observations, CancellationToken cancellationToken = default)
    {
        if (observations is null) throw new ArgumentNullException(nameof(observations));
        cancellationToken.ThrowIfCancellationRequested();
        var records = observations.Take(257).ToArray();
        if (records.Length < 2 || records.Length > 256 || records.Any(record => record is null))
            throw new ArgumentException("Require 2–256 nonnull fresh observations.", nameof(observations));
        var first = records[0].Evaluation;
        var samples = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evaluation = record.Evaluation;
            if (record.Candidate.Genome.SchemaHash != _space.VersionHash || record.Candidate.Id != record.Candidate.Genome.Identity ||
                evaluation.TaskVersionHash != first.TaskVersionHash || evaluation.EvaluatorVersionHash != first.EvaluatorVersionHash ||
                evaluation.Direction != first.Direction || evaluation.Quality < _options.QualityMinimum || evaluation.Quality > _options.QualityMaximum ||
                !identities.Add(EvolutionHash.Combine(new[] { record.Candidate.Id, evaluation.EvaluationId.ToString(CultureInfo.InvariantCulture) })))
                throw new ArgumentException("Require compatible typed genomes, quality support, provenance and unique measured records.", nameof(observations));
            foreach (string sample in evaluation.MeasurementOrigin?.SampleIds ?? Array.Empty<string>())
                if (!samples.Add(sample)) throw new ArgumentException("Original measurement samples overlap.", nameof(observations));
        }
        records = records.OrderBy(record => record.Candidate.Id, StringComparer.Ordinal).ThenBy(record => record.Evaluation.EvaluationId).ToArray();
        long work = 0;
        var encoded = records.Select(record =>
        {
            double[] features = _space.EncodeFeatures(record.Candidate.Genome).ToArray();
            work += features.Length;
            return new NumericPoint(record.Candidate.Id, features,
                (record.Evaluation.Quality!.Value - _options.QualityMinimum) / (_options.QualityMaximum - _options.QualityMinimum));
        }).ToArray();
        var groups = encoded.GroupBy(point => point.Id, StringComparer.Ordinal)
            .Select(group => new NumericPoint(group.Key, group.First().Features, group.Average(point => point.Quality)))
            .OrderBy(point => EvolutionHash.Combine(new[] { "knn-genome-partition-v1", point.Id }), StringComparer.Ordinal)
            .ThenBy(point => point.Id, StringComparer.Ordinal).ToArray();
        bool enough = groups.Length >= 18;
        int heldOut = enough ? Math.Max(5, groups.Length / 5) : 0;
        NumericPoint[] training = groups.Take(groups.Length - heldOut * 2).ToArray();
        NumericPoint[] calibration = groups.Skip(training.Length).Take(heldOut).ToArray();
        NumericPoint[] validation = groups.Skip(training.Length + heldOut).ToArray();
        double radius = 0, error = 0, coverage = 0;
        if (enough)
        {
            double[] residuals = calibration.Select(point => Math.Abs(Estimate(point.Features, training, _options.Neighbors, ref work, cancellationToken).Mean - point.Quality))
                .OrderBy(value => value).ToArray();
            radius = residuals[(int)Math.Ceiling(_options.CalibrationQuantile * residuals.Length) - 1];
            double[] errors = validation.Select(point => Math.Abs(Estimate(point.Features, training, _options.Neighbors, ref work, cancellationToken).Mean - point.Quality)).ToArray();
            error = errors.Average(); coverage = errors.Count(value => value <= radius) / (double)errors.Length;
        }
        string reason = !enough ? "insufficient-distinct-genomes" : error > _options.MaximumRelativeMeanAbsoluteError
            ? "validation-error" : coverage < _options.MinimumValidationCoverage ? "validation-coverage" : "accepted";
        var diagnostics = new NumericSurrogateValidation(training.Length, calibration.Length, validation.Length,
            radius * (_options.QualityMaximum - _options.QualityMinimum), error, coverage, reason == "accepted", reason, work);
        string identity = EvolutionHash.Combine(new[] { VersionHash, first.TaskVersionHash, first.EvaluatorVersionHash, first.Direction.ToString() }
            .Concat(records.Select(record => EvolutionHash.Combine(new[] { record.Candidate.Id,
                record.Evaluation.EvaluationId.ToString(CultureInfo.InvariantCulture), EvolutionHash.EncodeDouble(record.Evaluation.Quality!.Value),
                record.Evaluation.MeasurementOrigin is { } origin ? EvolutionHash.Combine(new[] { origin.ToJson() }) : "unreported-samples" }))));
        IEvolutionSurrogateModel<EvolutionSearchGenome> model = new ValidatedNearestNeighborModel(_space, _options, training,
            calibration.Select(point => point.Id).ToArray(), validation.Select(point => point.Id).ToArray(), diagnostics, identity);
        return new(new EvolutionResourceResult<IEvolutionSurrogateModel<EvolutionSearchGenome>>(model, Cost(_options.TrainingBaseCost, work)));
    }

    private EvolutionResources Cost(decimal fixedCost, long work) => EvolutionResources.Of("cost_units", fixedCost + work * _options.CoordinateCost);
    internal static (double Mean, double Distance) Estimate(double[] features, NumericPoint[] training, int count, ref long work, CancellationToken token)
    {
        var distances = new (int Index, double Distance)[training.Length];
        for (int i = 0; i < training.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            double sum = 0;
            for (int d = 0; d < features.Length; d++) { double delta = features[d] - training[i].Features[d]; sum += delta * delta; work++; }
            distances[i] = (i, Math.Sqrt(sum / features.Length));
        }
        var neighbors = distances.OrderBy(item => item.Distance).ThenBy(item => item.Index).Take(count).ToArray();
        double total = 0, mean = 0;
        foreach (var neighbor in neighbors)
        {
            double weight = 1 / (1e-12 + neighbor.Distance); total += weight;
            mean += (training[neighbor.Index].Quality - mean) * (weight / total);
        }
        return (Math.Max(0, Math.Min(1, mean)), neighbors[0].Distance);
    }
}

internal sealed class NumericPoint
{
    internal NumericPoint(string id, double[] features, double quality) { Id = id; Features = features; Quality = quality; }
    internal string Id { get; }
    internal double[] Features { get; }
    internal double Quality { get; }
}

/// <summary>A detached numeric predictor with auditable partitions and explicit reliability/domain checks.</summary>
public sealed class ValidatedNearestNeighborModel : IEvolutionSurrogateModel<EvolutionSearchGenome>, IEvolutionSurrogateDiagnosticModel
{
    private readonly EvolutionSearchSpace _space;
    private readonly NumericSurrogateOptions _options;
    private readonly NumericPoint[] _training;
    internal ValidatedNearestNeighborModel(EvolutionSearchSpace space, NumericSurrogateOptions options, NumericPoint[] training,
        string[] calibration, string[] validation, NumericSurrogateValidation diagnostics, string identity)
    {
        _space = space; _options = options; _training = training; Validation = diagnostics; VersionHash = identity;
        TrainingGenomeIds = Array.AsReadOnly(training.Select(point => point.Id).ToArray());
        CalibrationGenomeIds = Array.AsReadOnly(calibration); ValidationGenomeIds = Array.AsReadOnly(validation);
        ValidationReport = new EvolutionSurrogateValidationReport(EvolutionHash.Combine(new[] { "validated-knn-v1-group-split", options.VersionHash }),
            diagnostics.Reason, new Dictionary<string, double>
            {
                ["training_groups"] = diagnostics.TrainingGroups,
                ["calibration_groups"] = diagnostics.CalibrationGroups,
                ["validation_groups"] = diagnostics.ValidationGroups,
                ["residual_radius"] = diagnostics.ResidualRadius,
                ["relative_validation_mae"] = diagnostics.RelativeMeanAbsoluteError,
                ["empirical_validation_coverage"] = diagnostics.EmpiricalCoverage,
                ["required_coverage"] = options.MinimumValidationCoverage,
                ["maximum_relative_mae"] = options.MaximumRelativeMeanAbsoluteError,
                ["coordinate_work"] = diagnostics.CoordinateWork
            });
    }
    /// <inheritdoc/>
    public string VersionHash { get; }
    /// <inheritdoc/>
    public bool IsReliable => Validation.IsReliable;
    /// <summary>Gets detached counts, validation diagnostics, charged coordinate work and the acceptance/fallback reason.</summary>
    public NumericSurrogateValidation Validation { get; }
    /// <inheritdoc/>
    public EvolutionSurrogateValidationReport ValidationReport { get; }
    /// <summary>Gets genome groups used for interpolation.</summary>
    public IReadOnlyList<string> TrainingGenomeIds { get; }
    /// <summary>Gets disjoint groups used only to fit the residual radius.</summary>
    public IReadOnlyList<string> CalibrationGenomeIds { get; }
    /// <summary>Gets disjoint groups used only to assess the fitted radius and prediction error.</summary>
    public IReadOnlyList<string> ValidationGenomeIds { get; }

    /// <inheritdoc/>
    public ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>> PredictAsync(
        IReadOnlyList<EvolutionCanonicalGenome<EvolutionSearchGenome>> candidates, CancellationToken cancellationToken = default)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        cancellationToken.ThrowIfCancellationRequested();
        var pool = candidates.Take(65).ToArray();
        if (pool.Length == 0 || pool.Length > 64 || pool.Any(candidate => candidate is null || candidate.Genome.SchemaHash != _space.VersionHash ||
                candidate.Id != candidate.Genome.Identity) || pool.Select(candidate => candidate.Id).Distinct(StringComparer.Ordinal).Count() != pool.Length)
            throw new ArgumentException("Require 1–64 unique compatible typed candidates.", nameof(candidates));
        long work = 0;
        IReadOnlyList<EvolutionSurrogatePrediction> predictions = Array.AsReadOnly(pool.Select(candidate =>
        {
            double[] features = _space.EncodeFeatures(candidate.Genome).ToArray(); work += features.Length;
            var estimate = ValidatedNearestNeighborTrainer.Estimate(features, _training, _options.Neighbors, ref work, cancellationToken);
            double mean = _options.QualityMinimum + estimate.Mean * (_options.QualityMaximum - _options.QualityMinimum);
            return new EvolutionSurrogatePrediction(candidate.Id, mean, Validation.ResidualRadius, estimate.Distance <= _options.MaximumDistance);
        }).ToArray());
        return new(new EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>(predictions,
            EvolutionResources.Of("cost_units", _options.InferenceBaseCost + work * _options.CoordinateCost)));
    }
}
