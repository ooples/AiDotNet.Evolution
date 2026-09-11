using AiDotNet.Evolution;
using AiDotNet.Evolution.Surrogates;

var space = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("x", 0, 1)).Build();
var records = Enumerable.Range(0, 32).Select(i =>
{
    var genome = space.CreateGenome(new[] { new KeyValuePair<string, EvolutionParameterValue>("x", EvolutionParameterValue.Numeric(i / 31d)) });
    var canonical = new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, genome.Identity);
    var lineage = new EvolutionLineage(null, null, "package-smoke", null, i, 0, (ulong)i);
    var evaluation = new EvolutionEvaluation(i, canonical.Id, EvolutionEvaluationStatus.Completed, -0.5,
        EvolutionOptimizationDirection.Maximize, new Dictionary<string, double>(), Array.Empty<double>(), Array.Empty<double>(),
        new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "eval", "config");
    return new EvolutionSurrogateObservation<EvolutionSearchGenome>(new EvolutionCandidate<EvolutionSearchGenome>(i, canonical, lineage), evaluation);
}).ToArray();
var trainer = new ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(-1, 1));
var fit = await trainer.FitAsync(records);
if (fit.Value is not ValidatedNearestNeighborModel model || !model.IsReliable ||
    model.ValidationReport.Reason != "accepted" || model.Validation.ValidationGroups != 6)
    throw new InvalidOperationException("Packaged backend did not validate its independent genome groups.");
var predictions = await model.PredictAsync(records.Take(2).Select(record => record.Candidate).ToArray());
if (predictions.Value.Count != 2 || predictions.Value.Any(prediction => prediction.Mean != -0.5) || fit.Actual["cost_units"] != 0.020544m)
    throw new InvalidOperationException("Packaged backend prediction or charged work differs.");
Console.WriteLine("Isolated package consumer fitted, validated and predicted with the locally packed core and adapter.");
