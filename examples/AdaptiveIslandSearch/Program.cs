using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution;

int seeds = 2, budget = 128;
if (args.Length != 0 && (args.Length is < 2 or > 3 || !int.TryParse(args[0], out seeds) || !int.TryParse(args[1], out budget)) ||
    seeds is < 1 or > 30 || budget is < 32 or > 512)
{
    Console.Error.WriteLine("Usage: AdaptiveIslandSearch [seed-count 1..30 evaluator-call-cap 32..512 [new-output.json]]"); return 2;
}
using var output = args.Length == 3 ? new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
var runs = new List<object>(); bool valid = true;
foreach (string taskName in new[] { "ShiftedQuadratic1", "SeparatedBasins1" })
    foreach (string method in new[] { "Uniform", "Adaptive", "UniformRestart", "AdaptiveRestart" })
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
        {
            var first = await Run(taskName, method, seed, budget, false);
            var replay = await Run(taskName, method, seed, budget, false);
            var resumed = seed == 0 ? await Run(taskName, method, seed, budget, true) : null;
            bool passed = first.Valid && replay.Valid && first.Result.StateHash == replay.Result.StateHash &&
                first.Ledger.CaptureState() == replay.Ledger.CaptureState() &&
                (resumed is null || (resumed.Valid && first.Result.StateHash == resumed.Result.StateHash && first.Ledger.CaptureState() == resumed.Ledger.CaptureState()));
            valid &= passed;
            double best = double.NegativeInfinity;
            runs.Add(new
            {
                Task = taskName,
                Method = method,
                Seed = seed,
                Status = passed ? "completed" : "failed",
                InitialPopulationHash = EvolutionHash.Combine(new[] { "0.2", "0.21" }),
                EvaluatorCalls = first.Measurements.Count,
                Proposals = first.Result.Counters.Proposals,
                FinalQuality = first.Result.Best!.Evaluation.Quality,
                first.Result.StateHash,
                ReplayStateHash = replay.Result.StateHash,
                ResumeStateHash = resumed?.Result.StateHash,
                Statistics = first.Policy.Statistics,
                Decisions = first.Policy.RecentDecisions,
                PolicyState = first.Policy.CaptureState(),
                Resources = first.Ledger.Snapshot(),
                Measurements = first.Measurements.Select(measurement =>
                {
                    best = Math.Max(best, measurement.Quality);
                    return new { measurement.EvaluationId, measurement.Generation, measurement.Island, measurement.X, measurement.Quality, BestQuality = best };
                }).ToArray()
            });
        }
string json = JsonSerializer.Serialize(new
{
    Protocol = "fixed-adaptive-islands-pilot-v1",
    Seeds = seeds,
    EvaluatorCallCap = budget,
    AssemblyVersion = typeof(Genome).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
    AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(Genome).Assembly.Location))).ToLowerInvariant(),
    Interpretation = "Authored one-dimensional development fixtures, fixed initial seeds near the local basin, not held-out confirmation or competitor evidence. Four frozen allocation/restart ablations share the evaluator-call cap. Every first run is independently replayed; seed 0 of each task/method also restores an aligned engine plus resource-ledger checkpoint. Evaluation cost units are one local CPU objective call, not dollars or whole-pipeline compute. Proposal/setup/checkpoint CPU is not priced. No model or network calls.",
    AllValid = valid,
    Runs = runs
}, new JsonSerializerOptions { WriteIndented = true });
if (output is null) Console.WriteLine(json);
else { using var writer = new StreamWriter(output); writer.Write(json); Console.WriteLine($"{runs.Count} primary runs; replay/accounting/checkpoint checks: {(valid ? "PASS" : "FAIL")}"); }
return valid ? 0 : 1;

static async Task<RunData> Run(string taskName, string method, ulong seed, int budget, bool split)
{
    var measurements = new List<Measurement>();
    var store = new InMemoryEvolutionCheckpointStore();
    EvolutionResourceLedger Ledger() => new("island-example", EvolutionResources.Of("cost_units", budget), retainedReceiptLimit: 4096);
    AdaptiveIslandSearch<Genome> Policy() => new(new[] {
        new EvolutionIslandStrategy<Genome>("local-small", new Mutation(0.025), new Restart()),
        new EvolutionIslandStrategy<Genome>("local-large", new Mutation(0.12), new Restart()) },
        new EvolutionIslandPolicyOptions(8, 1, rewardWindow: 16, gainScale: 0.01, diversityWeight: 0.1,
            stagnationOutcomes: 12, restartProposals: 6, adaptiveAllocation: method.StartsWith("Adaptive", StringComparison.Ordinal),
            enableRestarts: method.EndsWith("Restart", StringComparison.Ordinal)));
    async Task<EvolutionRunResult<Genome>> Engine(AdaptiveIslandSearch<Genome> policy, EvolutionResourceLedger ledger, int cap, bool resume)
    {
        var task = new ResourceMeteredEvolutionTask<Genome>(new Objective(taskName, measurements), ledger, new[] { 1m });
        var options = new EvolutionEngineOptions
        {
            RunId = "island-example",
            Seed = seed,
            IslandCount = 2,
            MaxEvaluationAttempts = cap,
            MaxProposals = budget * 8,
            MaxGenerations = budget * 8,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            MigrationInterval = 16,
            Resume = resume
        };
        return await new EvolutionEngine<Genome>(task, policy,
            _ => new MapElitesArchive<Genome>(new[] { new EvolutionDescriptorDefinition("all", 0, 1, 1) }), options,
            checkpointStore: store, genomeCodec: new Codec()).RunAsync(new[] { new Genome(0.2), new Genome(0.21) });
    }
    var ledger = Ledger(); var policy = Policy();
    if (split)
    {
        await Engine(policy, ledger, budget / 2, false);
        string ledgerState = ledger.CaptureState(); ledger = Ledger(); ledger.RestoreState(ledgerState); policy = Policy();
    }
    var result = await Engine(policy, ledger, budget, split);
    var resources = ledger.Snapshot();
    bool valid = measurements.Count == budget && result.Counters.EvaluationAttempts == measurements.Count &&
        resources.Spent["cost_units"] == measurements.Count && resources.Reserved.Values.All(amount => amount == 0) &&
        resources.Unknown == 0 && !resources.MaximumViolated &&
        policy.Statistics.Sum(stat => stat.Proposals) == result.Counters.Proposals - 2 &&
        policy.Statistics.All(stat => stat.Proposals == stat.Outcomes) && result.Best!.Evaluation.Quality >= 0.5;
    return new RunData(result, policy, ledger, measurements, valid);
}

internal sealed record Genome(double X) : IImmutableEvolutionGenome<Genome>
{
    public Genome CreateOwnedSnapshot() => new(X);
}
internal sealed class Codec : IEvolutionGenomeCodec<Genome>
{
    public string Id => "scalar";
    public string VersionHash => "scalar-v1";
    public string Serialize(Genome genome) => genome.X.ToString("G17", CultureInfo.InvariantCulture);
    public Genome Deserialize(string payload) => new(double.Parse(payload, CultureInfo.InvariantCulture));
}
internal sealed class Mutation(double step) : IVariationOperator<Genome>
{
    public string Id => "triangular-mutation";
    public string VersionHash => "v1-" + step.ToString("G17", CultureInfo.InvariantCulture);
    public ValueTask<Genome> ProposeAsync(EvolutionVariationContext<Genome> context, CancellationToken cancellationToken = default)
    {
        double value = context.Parent.Candidate.CanonicalGenome.Genome.X + step * (context.Random.NextDouble() - context.Random.NextDouble());
        return new(new Genome(Math.Clamp(value, 0, 1)));
    }
}
internal sealed class Restart : IVariationOperator<Genome>
{
    public string Id => "uniform-restart";
    public string VersionHash => "v1";
    public ValueTask<Genome> ProposeAsync(EvolutionVariationContext<Genome> context, CancellationToken cancellationToken = default) => new(new Genome(context.Random.NextDouble()));
}
internal sealed class Objective(string task, List<Measurement> measurements) : IEvolutionTask<Genome>
{
    public string Id => task;
    public string VersionHash => "island-fixture-v1-" + task;
    public string EvaluatorVersionHash => VersionHash;
    public ValueTask<EvolutionCanonicalGenome<Genome>> CanonicalizeAsync(Genome genome, CancellationToken cancellationToken = default) =>
        new(new EvolutionCanonicalGenome<Genome>(genome, genome.X.ToString("G17", CultureInfo.InvariantCulture)));
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<Genome> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        double x = candidate.CanonicalGenome.Genome.X;
        double quality = task == "ShiftedQuadratic1" ? 1 - (x - 0.8) * (x - 0.8) :
            Math.Max(0.5 - 10 * (x - 0.2) * (x - 0.2), 1 - 60 * (x - 0.8) * (x - 0.8));
        measurements.Add(new Measurement(candidate.EvaluationId, candidate.Lineage.Generation, candidate.Lineage.Island, x, quality));
        return new(EvolutionTaskResult.Completed(quality, new Dictionary<string, double> { ["all"] = 0.5 }, costUnits: 1));
    }
}
internal sealed record Measurement(long EvaluationId, long Generation, int Island, double X, double Quality);
internal sealed record RunData(EvolutionRunResult<Genome> Result, AdaptiveIslandSearch<Genome> Policy,
    EvolutionResourceLedger Ledger, List<Measurement> Measurements, bool Valid);
