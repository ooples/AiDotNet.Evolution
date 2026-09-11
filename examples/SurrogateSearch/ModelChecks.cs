using AiDotNet.Evolution;

namespace SurrogateSearch;

internal static class ModelChecks
{
    internal static async Task RunAsync()
    {
        var space = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, 1)).Build();
        EvolutionCanonicalGenome<EvolutionSearchGenome> Genome(double x)
        {
            var genome = space.CreateGenome(new[] { new KeyValuePair<string, EvolutionParameterValue>("x", EvolutionParameterValue.Numeric(x)) });
            return new(genome, genome.Identity);
        }
        EvolutionSurrogateObservation<EvolutionSearchGenome> Measured(int id, double x, double quality)
        {
            var genome = Genome(x); var lineage = new EvolutionLineage(null, null, "test", null, id, 0, 1);
            var candidate = new EvolutionCandidate<EvolutionSearchGenome>(id, genome, lineage);
            return new(candidate, new EvolutionEvaluation(id, genome.Id, EvolutionEvaluationStatus.Completed, quality,
                EvolutionOptimizationDirection.Maximize, new Dictionary<string, double>(), Array.Empty<double>(), Array.Empty<double>(),
                new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.NotChecked,
                Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config"));
        }
        var backend = new NearestNeighborSurrogate(space);
        var records = Enumerable.Range(0, 5).Select(i => Measured(i, i / 4d, -i / 4d)).ToList();
        var fitted = await backend.FitAsync(records);
        var query = new[] { Genome(0.5) };
        var predicted = await fitted.Value.PredictAsync(query);
        Check(fitted.Value.IsReliable && predicted.Value.Count == 1 && Math.Abs(predicted.Value[0].Mean + 0.5) < 1e-10, "Known numeric interpolation failed.");
        Check(predicted.Value[0].WithinTrainingDomain && predicted.Value[0].Uncertainty >= 0, "Supported prediction metadata failed.");
        Check(fitted.Actual["cost_units"] == 0.02005m && predicted.Actual["cost_units"] == 0.00501m, "Numeric backend cost formula changed.");
        records.Clear();
        var detached = await fitted.Value.PredictAsync(query);
        Check(detached.Value[0].Mean == predicted.Value[0].Mean && detached.Value[0].Uncertainty == predicted.Value[0].Uncertainty, "Fitted model retained mutable caller data.");
        var weak = await backend.FitAsync(new[] { Measured(0, 0, 0), Measured(1, 1, -8) });
        Check(!weak.Value.IsReliable, "Poor leave-one-out validation must not enable acquisition.");
        var local = await backend.FitAsync(new[] { Measured(0, 0, 0), Measured(1, 0.01, -0.01) });
        var unfamiliar = await local.Value.PredictAsync(new[] { Genome(1) });
        Check(!unfamiliar.Value[0].WithinTrainingDomain, "Distant candidate bypassed support guard.");
        Check(local.Value.VersionHash != weak.Value.VersionHash, "Fitted identities omitted measured data.");
        bool rejected = false;
        try { await backend.FitAsync(new[] { Measured(0, 0, 1), Measured(1, 1, 0) }); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Unknown score support was accepted.");
        Console.WriteLine("Numeric surrogate checks passed: interpolation, detached data, costs, weak-model/domain fallback and fitted identities.");
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
