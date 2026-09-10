using System.Globalization;
using AiDotNet.Evolution;

namespace SurrogateSearch;

// Example adapter, not a core dependency or a calibrated probabilistic model. Its synthetic costs are declared
// work units for this demonstration, not measured CPU time, money or universal evaluator/model cost ratios.
internal sealed class NearestNeighborSurrogate(EvolutionSearchSpace space) : IEvolutionSurrogateTrainer<EvolutionSearchGenome>
{
    internal static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);
    public string VersionHash => EvolutionHash.Combine(new[] { "example-knn-v1-known-support-minus8-to0", space.VersionHash });

    public ValueTask<EvolutionResourceResult<IEvolutionSurrogateModel<EvolutionSearchGenome>>> FitAsync(
        IReadOnlyList<EvolutionSurrogateObservation<EvolutionSearchGenome>> observations, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (observations.Count is < 2 or > 32 || space.FeatureCount > 32 || observations.Any(o => o.Evaluation.Quality is < -8 or > 0))
            throw new ArgumentException("This example backend supports 2..32 records, at most 32 features and known quality support [-8,0].");
        var features = observations.Select(o => space.EncodeFeatures(o.Candidate.Genome).ToArray()).ToArray();
        double[] quality = observations.Select(o => o.Evaluation.Quality!.Value).ToArray();
        var errors = features.Select((point, index) => Math.Abs(Estimate(point, features, quality, index).Mean - quality[index])).ToArray();
        string identity = EvolutionHash.Combine(new[] { VersionHash }.Concat(observations.Select(o =>
            EvolutionHash.Combine(new[] { o.Candidate.Id, o.Evaluation.Quality!.Value.ToString("R", CultureInfo.InvariantCulture) }))));
        double rmse = Math.Sqrt(errors.Select(error => error * error).Average());
        // Leave-one-out predictive error is an internal diagnostic, not independent calibration or held-out confirmation.
        var model = new Model(space, features, quality, rmse, errors.Average() / 8 <= 0.15, identity);
        decimal cost = 0.02m + observations.Count * observations.Count * space.FeatureCount * 0.000001m;
        return new(new EvolutionResourceResult<IEvolutionSurrogateModel<EvolutionSearchGenome>>(model, Cost(cost)));
    }

    private static (double Mean, double Distance) Estimate(double[] point, double[][] features, double[] quality, int exclude = -1)
    {
        var neighbors = features.Select((other, index) => new { Index = index, Distance = Math.Sqrt(point.Zip(other, (a, b) => (a - b) * (a - b)).Average()) })
            .Where(value => value.Index != exclude).OrderBy(value => value.Distance).ThenBy(value => value.Index).Take(3).ToArray();
        double total = 0, mean = 0;
        foreach (var neighbor in neighbors)
        {
            double weight = 1 / (0.000001 + neighbor.Distance); total += weight;
            mean += (quality[neighbor.Index] - mean) * (weight / total);
        }
        return (mean, neighbors[0].Distance);
    }

    private sealed class Model(EvolutionSearchSpace space, double[][] features, double[] quality, double residual,
        bool reliable, string identity) : IEvolutionSurrogateModel<EvolutionSearchGenome>
    {
        public string VersionHash => identity;
        public bool IsReliable => reliable;
        public ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>> PredictAsync(
            IReadOnlyList<EvolutionCanonicalGenome<EvolutionSearchGenome>> candidates, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<EvolutionSurrogatePrediction> predictions = candidates.Select(candidate =>
            {
                var estimate = Estimate(space.EncodeFeatures(candidate.Genome).ToArray(), features, quality);
                // Distance/residual proxy deliberately has no confidence-coverage claim.
                return new EvolutionSurrogatePrediction(candidate.Id, estimate.Mean, residual + 8 * estimate.Distance, estimate.Distance <= 0.25);
            }).ToArray();
            decimal cost = 0.005m + candidates.Count * features.Length * space.FeatureCount * 0.000001m;
            return new(new EvolutionResourceResult<IReadOnlyList<EvolutionSurrogatePrediction>>(predictions, Cost(cost)));
        }
    }
}
