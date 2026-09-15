using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;
using SurrogateSearch;

if (args.Length == 1 && args[0] == "--verify-model") { await ModelChecks.RunAsync(); return 0; }
int seeds = 2, budget = 64;
if (args.Length != 0 && (args.Length is < 2 or > 3 || !int.TryParse(args[0], out seeds) || !int.TryParse(args[1], out budget)) ||
    seeds is < 1 or > 20 || budget is < 16 or > 256)
{
    Console.Error.WriteLine("Usage: SurrogateSearch [seed-count 1..20 cost-cap 16..256 [new-output.json]]"); return 2;
}
using var output = args.Length == 3 ? new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
var builder = new EvolutionSearchSpaceBuilder();
for (int i = 0; i < 4; i++) builder.Add(EvolutionParameter.Real("x" + i, 0, 1));
EvolutionSearchSpace space = builder.Build();
var runs = new List<object>();
bool allValid = true;
foreach (string task in new[] { "ShiftedQuadratic4", "RippledQuadratic4" })
    foreach (string method in new[] { "Ordinary", "UniformPool", "SurrogatePool" })
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
        {
            var ledger = new EvolutionResourceLedger($"{task}-{method}-{seed}", NearestNeighborSurrogate.Cost(budget), retainedReceiptLimit: 2048, maximumOperations: 4096);
            var archive = new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("all", 0, 1, 1) });
            var observations = new List<EvolutionSurrogateObservation<EvolutionSearchGenome>>();
            var decisions = new List<object>(); var measured = new List<object>();
            var reasonCounts = new Dictionary<string, int>();
            string taskVersion = "surrogate-example-" + task + "-v1";
            int poolSize = method == "Ordinary" ? 1 : 4;
            var selector = new EvolutionSurrogateSelector<EvolutionSearchGenome>(new NearestNeighborSurrogate(space), ledger,
                taskVersion, taskVersion, NearestNeighborSurrogate.Cost(0.08m * poolSize),
                NearestNeighborSurrogate.Cost(0.03m), NearestNeighborSurrogate.Cost(0.007m),
                explorationProbability: method == "SurrogatePool" ? 0.2 : 1, optimism: 0.1);
            int calls = 0, generated = 0;
            EvolutionSearchGenome[] initial = await EvolutionResourceWork.RunAsync(ledger, "initial-population", EvolutionResourceStage.Setup,
                NearestNeighborSurrogate.Cost(0.08m), NearestNeighborSurrogate.Cost(0.08m), _ =>
                {
                    var initialRandom = StableRandom.CreateStream(seed, 123);
                    var population = Enumerable.Range(0, 8).Select(_ => space.Sample(initialRandom)).ToArray();
                    return new ValueTask<EvolutionResourceResult<EvolutionSearchGenome[]>>(new EvolutionResourceResult<EvolutionSearchGenome[]>(population, NearestNeighborSurrogate.Cost(0.08m)));
                });
            string initialHash = EvolutionHash.Combine(initial.Select(genome => genome.Identity));

            bool Measure(EvolutionCanonicalGenome<EvolutionSearchGenome> canonical)
            {
                using var reservation = ledger.TryReserve("true-evaluation/" + calls, EvolutionResourceStage.Evaluation,
                    NearestNeighborSurrogate.Cost(1), NearestNeighborSurrogate.Cost(1));
                if (reservation is null) return false;
                double loss = space.Parameters.Select((parameter, index) =>
                {
                    double delta = canonical.Genome.Number(parameter.Name) - new[] { 0.2, 0.6, 0.4, 0.8 }[index];
                    return delta * delta + (task == "RippledQuadratic4" ? 0.1 * (1 - Math.Cos(10 * Math.PI * delta)) : 0);
                }).Sum();
                var lineage = new EvolutionLineage(null, null, method, null, calls, 0, seed);
                var candidate = new EvolutionCandidate<EvolutionSearchGenome>(calls, canonical, lineage);
                var evaluation = new EvolutionEvaluation(calls, canonical.Id, EvolutionEvaluationStatus.Completed, -loss,
                    EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["all"] = 0 }, Array.Empty<double>(), Array.Empty<double>(),
                    new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.NotChecked,
                    Array.Empty<EvolutionDiagnostic>(), taskVersion, taskVersion, selector.VersionHash);
                reservation.Complete(NearestNeighborSurrogate.Cost(1)); calls++;
                archive.TryAdd(candidate, evaluation);
                observations.Add(new(candidate, evaluation));
                if (observations.Count > 32) observations.RemoveAt(0);
                measured.Add(new { candidate.EvaluationId, canonical.Id, Loss = loss, BestLoss = -archive.Best!.Evaluation.Quality!.Value });
                return true;
            }

            foreach (var genome in initial) Measure(new(genome, genome.Identity));
            string stop = "cost-cap";
            for (int step = 0; step < budget * 2; step++)
            {
                var random = StableRandom.CreateStream(seed, (ulong)(1000 + step));
                try
                {
                    var decision = await selector.SelectAsync("step-" + step, observations, random, _ =>
                    {
                        var pool = new Dictionary<string, EvolutionCanonicalGenome<EvolutionSearchGenome>>();
                        int attempts = 0;
                        while (pool.Count < poolSize && attempts < 8 * poolSize)
                        {
                            attempts++; generated++;
                            EvolutionSearchGenome parent = archive.Best!.Candidate.CanonicalGenome.Genome;
                            EvolutionSearchGenome genome = random.NextDouble() < 0.2 ? space.Sample(random) : space.CreateGenome(space.Parameters.Select(parameter =>
                                new KeyValuePair<string, EvolutionParameterValue>(parameter.Name,
                                    EvolutionParameterValue.Numeric(Math.Clamp(parent.Number(parameter.Name) + 0.1 * (2 * random.NextDouble() - 1), 0, 1)))));
                            pool.TryAdd(genome.Identity, new(genome, genome.Identity));
                        }
                        return new ValueTask<EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<EvolutionSearchGenome>>>>(
                            new EvolutionResourceResult<IReadOnlyList<EvolutionCanonicalGenome<EvolutionSearchGenome>>>(pool.Values.ToArray(), NearestNeighborSurrogate.Cost(0.01m * attempts),
                                pool.Count == poolSize ? EvolutionResourceOutcome.Completed : EvolutionResourceOutcome.Failed));
                    });
                    reasonCounts[decision.Reason.ToString()] = reasonCounts.GetValueOrDefault(decision.Reason.ToString()) + 1;
                    bool evaluated = Measure(decision.Candidate);
                    decisions.Add(new
                    {
                        decision.OperationIdentity,
                        decision.TrainingIdentity,
                        decision.ModelVersionHash,
                        decision.Reason,
                        decision.ExplorationProbability,
                        decision.PoolSize,
                        Selected = decision.Candidate.Id,
                        Evaluated = evaluated,
                        decision.Predictions
                    });
                    if (!evaluated) break;
                    if (step == budget * 2 - 1) stop = "proposal-cap";
                }
                catch (EvolutionResourceBudgetException) { break; }
            }
            EvolutionResourceSnapshot resources = ledger.Snapshot();
            bool valid = stop == "cost-cap" && resources.Spent["cost_units"] <= budget && resources.Unknown == 0 &&
                !resources.MaximumViolated && resources.Reserved.Values.All(value => value == 0) &&
                resources.Receipts.Where(receipt => receipt.Stage == EvolutionResourceStage.Evaluation).Sum(receipt => receipt.Charged["cost_units"]) == calls &&
                archive.Count == 1 && measured.Count == calls && calls >= 8;
            allValid &= valid;
            runs.Add(new
            {
                Task = task,
                Method = method,
                Seed = seed,
                Status = valid ? "completed" : "failed",
                StopReason = stop,
                InitialPopulationHash = initialHash,
                EvaluatorCalls = calls,
                GeneratedProposals = generated,
                FinalLoss = -archive.Best!.Evaluation.Quality!.Value,
                ReasonCounts = reasonCounts,
                Resources = resources,
                Measured = measured,
                Decisions = decisions
            });
        }
string json = JsonSerializer.Serialize(new
{
    Protocol = "synthetic-surrogate-example-v1",
    Seeds = seeds,
    CostCap = budget,
    AssemblyVersion = typeof(NearestNeighborSurrogate).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
    AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(NearestNeighborSurrogate).Assembly.Location))).ToLowerInvariant(),
    Interpretation = "Development example only. Explicit synthetic work-unit prices, not measured runtime or real evaluator/model cost ratios. KNN residual/distance uncertainty is not a confidence interval. No confirmation or deployment claim.",
    Runs = runs
}, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
if (output is null) Console.WriteLine(json);
else { using var writer = new StreamWriter(output); await writer.WriteLineAsync(json); }
return allValid ? 0 : 1;
