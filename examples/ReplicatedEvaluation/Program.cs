using System.Text.Json;
using AiDotNet.Evolution;

int samples = 32;
if (args.Length > 1 || (args.Length == 1 && !int.TryParse(args[0], out samples)) || samples is < 2 or > 256)
{
    Console.Error.WriteLine("Usage: ReplicatedEvaluation [independent-samples-per-batch 2..256]");
    return 2;
}
// These are known support bounds of this synthetic generator, not observed minima/maxima.
// Each confirmation interval receives alpha/2 for this one predeclared two-candidate comparison.
var plan = new EvolutionReplicationPlan(samples, samples, 0.65, 0.9, 1, confidence: 0.975);
var ledger = new EvolutionResourceLedger("replication-example", EvolutionResources.Of("cost_units", 4 * samples));
int calls = 0;
var runner = new EvolutionReplicateRunner<int>("bounded-uniform-generator-v1", plan, ledger, (candidate, context, token) =>
{
    token.ThrowIfCancellationRequested(); calls++;
    double quality = (candidate == 0 ? 0.65 : 0.70) + 0.2 * context.EvaluationContext.CreateRandom().NextDouble();
    return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(quality, new Dictionary<string, double>(), costUnits: 1));
});
var reports = new List<EvolutionReplicationReport>();
foreach (var purpose in new[] { EvolutionReplicationPurpose.Search, EvolutionReplicationPurpose.Confirmation })
    for (int candidate = 0; candidate < 2; candidate++)
        reports.Add(await runner.RunAsync(new EvolutionCanonicalGenome<int>(candidate, "candidate-" + candidate),
            new EvolutionEvaluationContext(candidate, 42, 17, 1), "predeclared-pair-v1", purpose));

bool identitiesUnique = reports.SelectMany(report => report.Samples)
    .Select(sample => sample.Context.SampleIdentity).Distinct().Count() == calls;
bool valid = reports.All(report => report.IsComplete && report.Samples.Count == samples) && calls == 4 * samples &&
    ledger.Snapshot().Spent["cost_units"] == calls && identitiesUnique;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Kind = "synthetic-replication-example",
    SamplesPerBatch = samples,
    EvaluatorCalls = calls,
    CostUnits = ledger.Snapshot().Spent["cost_units"],
    AllSampleIdentitiesUnique = identitiesUnique,
    CandidateConfirmationExceedsBaselineInterval = reports[3].LowerBound > reports[2].UpperBound,
    Interpretation = "One fixed synthetic pair; no adaptive-search, held-out application or deployment claim. Confirmation is never fed to a proposer.",
    Reports = reports.Select((report, index) => new
    {
        Candidate = index % 2,
        Purpose = index < 2 ? "search" : "confirmation",
        report.BatchIdentity,
        report.IsComplete,
        StopReason = report.StopReason.ToString(),
        report.MeanQuality,
        report.StandardError,
        report.LowerBound,
        report.UpperBound,
        report.ChargedCostUnits,
        Samples = report.Samples.Select(sample => new { sample.Context.SampleIdentity, sample.Quality, sample.ChargedCostUnits, sample.UnknownCost })
    })
}, new JsonSerializerOptions { WriteIndented = true }));
return valid ? 0 : 1;
