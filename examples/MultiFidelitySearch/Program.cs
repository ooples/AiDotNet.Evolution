using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;
using MultiFidelitySearch;

if (args.Length == 1 && args[0] == "--verify-training") { IncrementalRegressionTask.VerifyTraining(); return 0; }
if (args.Length > 0 && args[0] == "--regression") return await RegressionCampaign.RunAsync(args.Skip(1).ToArray());
if (args.Length > 0 && args[0] == "--checkpoint-regression") return await RegressionRecovery.RunAsync(args.Skip(1).ToArray());

int seeds = 2;
if (args.Length > 2 || (args.Length > 0 && !int.TryParse(args[0], out seeds)) || seeds is < 1 or > 32)
{
    Console.Error.WriteLine("Usage: MultiFidelitySearch [seed-count 1..32 [new-output.json]]"); return 2;
}
using var output = args.Length == 2 ? new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
var runs = new List<object>(); bool valid = true;
foreach (string task in new[] { "Aligned", "LateImprover" })
    foreach (bool explore in new[] { false, true })
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
        {
            string method = explore ? "ExploratoryHalving" : "GreedyHalving";
            var ledger = new EvolutionResourceLedger(task + "-" + method + "-" + seed, EvolutionResources.Of("cost_units", 100), retainedReceiptLimit: 128);
            var initial = await EvolutionResourceWork.RunAsync(ledger, "initial-candidates", EvolutionResourceStage.Setup,
                EvolutionResources.Of("cost_units", 0.08m), EvolutionResources.Of("cost_units", 0.08m), _ =>
                new ValueTask<EvolutionResourceResult<EvolutionCanonicalGenome<int>[]>>(new EvolutionResourceResult<EvolutionCanonicalGenome<int>[]>(
                    Enumerable.Range(0, 8).Select(i => new EvolutionCanonicalGenome<int>(i, "candidate-" + i)).ToArray(), EvolutionResources.Of("cost_units", 0.08m))));
            var plan = new EvolutionFidelityPlan(new[] { new EvolutionFidelityLevel("one-step", 1, 1), new("three-step", 3, 3), new("nine-step", 9, 9) },
                0, 1, explorationFraction: explore ? 0.25 : 0);
            int calls = 0, resumed = 0, confirmationCalls = 0, steps = 0;
            ValueTask<EvolutionFidelityEvaluationResult> Evaluate(int candidate, EvolutionFidelityEvaluationContext context, CancellationToken token, bool confirmation)
            {
                token.ThrowIfCancellationRequested(); calls++;
                int completed = 0;
                if (confirmation)
                {
                    confirmationCalls++;
                    if (context.Resume is not null || context.Level.ResourceLevel != 9 || context.Replicate.Purpose != EvolutionReplicationPurpose.Confirmation)
                        throw new InvalidOperationException("Confirmation must start afresh at full fidelity.");
                }
                else if (context.Resume is { } previous)
                {
                    byte[] state = previous.CopyToken();
                    if (state.Length != 3 || state[0] != candidate || state[1] != context.Replicate.Index || state[2] != previous.SourceLevel.ResourceLevel)
                        throw new InvalidOperationException("Continuation token provenance differs.");
                    completed = state[2]; resumed++;
                }
                int actual = 0;
                for (int step = completed; step < context.Level.ResourceLevel; step++) { token.ThrowIfCancellationRequested(); steps++; actual++; }
                // Synthetic learning curve: early ranking is deliberately misleading for the late-improver family.
                double quality = task == "LateImprover" && context.Level.ResourceLevel == 9 && candidate < 4 ? 0.95 : 0.1 + candidate * 0.1;
                quality += (context.Replicate.EvaluationContext.CreateRandom().NextDouble() - 0.5) * 0.02;
                var measurement = EvolutionTaskResult.Completed(quality, new Dictionary<string, double>(), costUnits: actual);
                return new(new EvolutionFidelityEvaluationResult(measurement,
                    confirmation ? null : new[] { (byte)candidate, (byte)context.Replicate.Index, (byte)context.Level.ResourceLevel },
                    confirmation ? null : "step-token-v1"));
            }
            var scheduler = new EvolutionFidelityScheduler<int>(plan, ledger, "synthetic-search-" + task + "-v1", "synthetic-confirm-" + task + "-v1", "step-token-v1",
                (candidate, context, token) => Evaluate(candidate, context, token, false),
                (candidate, context, token) => Evaluate(candidate, context, token, true));
            var report = await scheduler.RunAsync("predeclared-candidate-set", initial, seed);
            bool complete = report.IsComplete && calls == 32 && resumed == 12 && confirmationCalls == 4 && steps == 92 &&
                report.ChargedCostUnits == steps && ledger.Snapshot().Spent["cost_units"] == 92.08m && ledger.Snapshot().Unknown == 0;
            valid &= complete;
            runs.Add(new
            {
                Task = task,
                Method = method,
                Seed = seed,
                Status = complete ? "completed" : "failed",
                InitialPopulationHash = EvolutionHash.Combine(initial.Select(candidate => candidate.Id)),
                EvaluatorCalls = calls,
                ResumedCalls = resumed,
                ConfirmationCalls = confirmationCalls,
                ExecutedSteps = steps,
                BestConfirmedQuality = report.BestConfirmed?.Measurements.MeanQuality,
                BestConfirmedGenome = report.BestConfirmed?.Candidate.Id,
                Report = report
            });
        }
string json = JsonSerializer.Serialize(new
{
    Protocol = "synthetic-fidelity-example-v1",
    Seeds = seeds,
    CostCap = 100,
    AssemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
    AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location))).ToLowerInvariant(),
    Interpretation = "Synthetic step costs and learning curves only. One successive-halving bracket, not full Hyperband. Fresh full-fidelity confirmation is required but is not deployment or superiority approval. Search/resume intervals are exploratory.",
    Runs = runs
}, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
if (output is null) Console.WriteLine(json);
else { using var writer = new StreamWriter(output); await writer.WriteLineAsync(json); }
return valid ? 0 : 1;
