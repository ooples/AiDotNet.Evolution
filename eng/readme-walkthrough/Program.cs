using AiDotNet.Evolution;

// 1. Describe the space you want to search.
EvolutionSearchSpace space = new EvolutionSearchSpaceBuilder()
    .Add(EvolutionParameter.Integer("depth", 1, 20))
    .Add(EvolutionParameter.Logarithmic("rate", 0.0001, 1.0))
    .Build();

// 2. Say how good a candidate is.
var task = new EvolutionSearchTask(space, "quickstart", "v1", "score-v1", (genome, _, _) =>
    new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
        quality: -Math.Abs(genome.Number("depth") - 7) - Math.Abs(Math.Log10(genome.Number("rate") / 0.01)),
        descriptors: new Dictionary<string, double> { ["depth"] = genome.Number("depth") },
        costUnits: 1)));

var descriptors = new[] { new EvolutionDescriptorDefinition("depth", 1, 21, 10) };

// 3. Write a trace, so the run can be inspected and reported afterwards.
using var tracer = new EvolutionTraceObserver<EvolutionSearchGenome>(
    new EvolutionTraceOptions { Enabled = true, Path = "trace.jsonl" }, "quickstart", descriptors);

// 4. Run the search.
var engine = new EvolutionEngine<EvolutionSearchGenome>(
    task,
    EvolutionSearchPresets.CreateAdaptiveMixed(space),
    _ => new MapElitesArchive<EvolutionSearchGenome>(descriptors),
    new EvolutionEngineOptions { RunId = "quickstart", Seed = 42, MaxEvaluationAttempts = 200 },
    observer: tracer);

EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(
    new[] { space.Sample(StableRandom.CreateStream(42, 0)) });

if (result.Best is null)
{
    Console.Error.WriteLine("no candidate was scored");
    return 1;
}

Console.WriteLine($"best quality : {result.Best.Evaluation.Quality:F4}");
Console.WriteLine($"depth        : {result.Best.Candidate.CanonicalGenome.Genome.Number("depth")}");
return 0;
