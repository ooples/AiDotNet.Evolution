using System.Text.Json;
using AiDotNet.Evolution;

// API demonstration only: the score is a synthetic proxy, not a trained model or runtime benchmark.
int budget = 64;
if (args.Length > 1 || (args.Length == 1 && !int.TryParse(args[0], out budget)) || budget is < 2 or > 512)
{
    Console.Error.WriteLine("Usage: TypedParameterSearch [evaluation-budget 2..512]");
    return 2;
}

EvolutionSearchSpace space = new EvolutionSearchSpaceBuilder()
    .Add(EvolutionParameter.Categorical("family", new[] { "tree", "linear" }))
    .Add(EvolutionParameter.Integer("depth", 1, 20).When("family", EvolutionParameterValue.Categorical("tree")))
    .Add(EvolutionParameter.Logarithmic("rate", 0.0001, 10).When("family", EvolutionParameterValue.Categorical("linear")))
    .Add(EvolutionParameter.Real("mix", 0, 1))
    .Build();

static double Score(EvolutionSearchGenome genome)
{
    double mixError = genome.Number("mix") - 0.25;
    double familyError = genome.Category("family") == "tree" ? genome.Number("depth") - 7 : Math.Log10(genome.Number("rate") / 0.1);
    return -familyError * familyError - mixError * mixError - (genome.Category("family") == "linear" ? 5 : 0);
}

var task = new EvolutionSearchTask(space, "mixed-api-example", "v1", "synthetic-proxy-v1", (genome, _, _) =>
    new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(Score(genome),
        new Dictionary<string, double> { ["family"] = genome.Category("family") == "tree" ? 0 : 1 }, costUnits: 1)));
var ledger = new EvolutionResourceLedger("mixed-example", EvolutionResources.Of("cost_units", budget));
var variation = EvolutionSearchPresets.CreateAdaptiveMixed(space);
var initial = new[] { space.Sample(StableRandom.CreateStream(42, 0)), space.Sample(StableRandom.CreateStream(42, 1)) };
var engine = new EvolutionEngine<EvolutionSearchGenome>(new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m }), variation,
    _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("family", 0, 2, 2) }),
    new EvolutionEngineOptions
    {
        RunId = "mixed-example",
        Seed = 42,
        MaxEvaluationAttempts = budget,
        MaxProposals = budget * 10,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        InspirationCount = 2,
        MigrationInterval = 0
    });
EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(initial);
var best = result.Best;
if (best is null) { Console.Error.WriteLine("No valid result; retain the original implementation."); return 1; }
using JsonDocument genomeJson = JsonDocument.Parse(space.Serialize(best.Candidate.CanonicalGenome.Genome));
Console.WriteLine(JsonSerializer.Serialize(new
{
    Kind = "synthetic-api-example",
    InitialQuality = initial.Max(Score),
    BestQuality = best.Evaluation.Quality,
    Improved = best.Evaluation.Quality > initial.Max(Score),
    StopReason = result.StopReason.ToString(),
    EvaluationCostUnits = ledger.Snapshot().Spent["cost_units"],
    TaskVersion = task.VersionHash,
    EvaluatorVersion = task.EvaluatorVersionHash,
    Genome = genomeJson.RootElement,
    Operators = variation.Statistics,
    StateHash = result.StateHash
}, new JsonSerializerOptions { WriteIndented = true }));
return ledger.Snapshot().Spent["cost_units"] <= budget ? 0 : 1;
