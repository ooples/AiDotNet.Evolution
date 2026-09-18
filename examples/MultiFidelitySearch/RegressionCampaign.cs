using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

namespace MultiFidelitySearch;

internal static class RegressionCampaign
{
    internal static async Task<int> RunAsync(string[] args)
    {
        int seeds = 2;
        if (args.Length > 2 || (args.Length > 0 && !int.TryParse(args[0], out seeds)) || seeds is < 1 or > 32)
        { Console.Error.WriteLine("Usage: MultiFidelitySearch --regression [seed-count 1..32 [new-output.json]]"); return 2; }
        using var output = args.Length == 2 ? new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
        var runs = new List<object>(); bool allValid = true;
        foreach (string method in new[] { "FullCohort", "GreedyHalving", "ExploratoryHalving", "RestartOnlyHalving" })
            for (ulong seed = 0; seed < (ulong)seeds; seed++)
            {
                var ledger = new EvolutionResourceLedger("regression-" + method + "-" + seed,
                    EvolutionResources.Of("cost_units", 2100), retainedReceiptLimit: 128);
                var initial = await EvolutionResourceWork.RunAsync(ledger, "initial-candidates", EvolutionResourceStage.Setup,
                    EvolutionResources.Of("cost_units", 0.08m), EvolutionResources.Of("cost_units", 0.08m), _ =>
                    {
                        var candidates = CreateCandidates();
                        return new ValueTask<EvolutionResourceResult<EvolutionCanonicalGenome<EvolutionSearchGenome>[]>>(
                            new EvolutionResourceResult<EvolutionCanonicalGenome<EvolutionSearchGenome>[]>(candidates, EvolutionResources.Of("cost_units", 0.08m)));
                    });
                var task = new IncrementalRegressionTask(enableContinuation: method != "RestartOnlyHalving");
                var plan = CreatePlan(method);
                var scheduler = new EvolutionFidelityScheduler<EvolutionSearchGenome>(plan, ledger,
                    method == "RestartOnlyHalving" ? "regression-search-v1-restart-only" : "regression-search-v1-continuation", "regression-confirm-v1",
                    IncrementalRegressionTask.StateVersion, task.Evaluate, task.Evaluate);
                var report = await scheduler.RunAsync("regression-cohort-v1", initial, seed);
                var resources = ledger.Snapshot();
                bool valid = report.IsComplete && task.ConfirmationCalls == (method == "FullCohort" ? 16 : 4) &&
                    report.ChargedCostUnits == task.Epochs + task.Calls * 0.25m &&
                    resources.Spent["cost_units"] == report.ChargedCostUnits + 0.08m && resources.Spent["cost_units"] <= 2100 &&
                    resources.Unknown == 0 && resources.Admitted == resources.Settled && resources.DroppedReceipts == 0 && !resources.MaximumViolated &&
                    task.TrainingRowVisits == task.Epochs * 128 && task.ValidationRowVisits == task.Calls * 64 &&
                    (method != "RestartOnlyHalving" || task.ResumedCalls == 0);
                allValid &= valid;
                runs.Add(new
                {
                    Task = "AuthoredLinearRegression4",
                    Method = method,
                    Seed = seed,
                    Status = valid ? "completed" : "failed",
                    InitialPopulationHash = EvolutionHash.Combine(initial.Select(candidate => candidate.Id)),
                    EvaluatorCalls = task.Calls,
                    task.ResumedCalls,
                    task.ConfirmationCalls,
                    ExecutedEpochs = task.Epochs,
                    task.TrainingRowVisits,
                    task.ValidationRowVisits,
                    BestConfirmedQuality = report.BestConfirmed?.Measurements.MeanQuality,
                    BestConfirmedGenome = report.BestConfirmed?.Candidate.Id,
                    Measurements = task.Measurements,
                    Report = ExportReport(report)
                });
            }
        string json = JsonSerializer.Serialize(new
        {
            Protocol = "trained-regression-fidelity-v1",
            Seeds = seeds,
            CostCap = 2100,
            AssemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
            AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(System.Reflection.Assembly.GetExecutingAssembly().Location))).ToLowerInvariant(),
            Interpretation = "Real full-batch gradient descent on an authored synthetic regression dataset, not AiDotNet AutoML or a representative external benchmark. " +
                "Equal total caps and candidate cohorts, not equal spent budgets: every method finishes its finite bracket. FullCohort retains/confirms all candidates. " +
                "Search and confirmation independently regenerate distinct training and held-out data; each replicate retrains fresh for confirmation. " +
                "Epoch and fixed per-measurement tariffs include declared data/scoring/token work, not elapsed CPU or dollars. No deployment or superiority claim.",
            Runs = runs
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        if (output is null) Console.WriteLine(json);
        else { using var writer = new StreamWriter(output); await writer.WriteLineAsync(json); }
        return allValid ? 0 : 1;
    }

    internal static EvolutionCanonicalGenome<EvolutionSearchGenome>[] CreateCandidates()
    {
        var space = new EvolutionSearchSpaceBuilder().Add(EvolutionParameter.Real("learning-rate", 0.003, 0.3))
            .Add(EvolutionParameter.Integer("warmup", 0, 8)).Build();
        return (from rate in new[] { 0.003, 0.01, 0.05, 0.3 }
                from warmup in new[] { 0, 8 }
                select space.CreateGenome(new[] { new KeyValuePair<string, EvolutionParameterValue>("learning-rate", EvolutionParameterValue.Numeric(rate)),
                    new KeyValuePair<string, EvolutionParameterValue>("warmup", EvolutionParameterValue.Numeric(warmup)) }))
            .Select(genome => new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, genome.Identity)).ToArray();
    }

    internal static EvolutionFidelityPlan CreatePlan(string method) => new(new[] { new EvolutionFidelityLevel("four-epochs", 4, 4.25m),
        new("sixteen-epochs", 16, 16.25m), new("full", 64, 64.25m) }, 0, 1,
        minimumSurvivors: method == "FullCohort" ? 64 : 2, explorationFraction: method == "ExploratoryHalving" ? 0.25 : 0);

    // Never reflect-serialize both numeric and category getters of a typed parameter union.
    internal static object ExportReport(EvolutionFidelityReport<EvolutionSearchGenome> report) => new
    {
        report.RunIdentity,
        report.SchedulerVersionHash,
        report.SearchEvaluatorVersionHash,
        report.ConfirmationEvaluatorVersionHash,
        report.InitialCandidateIds,
        report.Plan,
        report.StopReason,
        report.IsComplete,
        report.ChargedCostUnits,
        report.Resources,
        report.Promotions,
        Batches = report.Batches.Select(batch => new
        {
            Candidate = new
            {
                batch.Candidate.Id,
                batch.Candidate.Genome.SchemaHash,
                LearningRate = batch.Candidate.Genome.Number("learning-rate"),
                Warmup = batch.Candidate.Genome.Number("warmup")
            },
            batch.Level,
            batch.Purpose,
            batch.Measurements,
            batch.ResumedFromSampleIdentities,
            batch.AcceptedContinuationTokens,
            batch.RejectedContinuationTokens
        }).ToArray()
    };
}
