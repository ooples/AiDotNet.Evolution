using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

namespace MultiFidelitySearch;

internal static class RegressionRecovery
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length is < 2 or > 3 || args[0] is not ("baseline" or "start" or "resume") || (args[0] == "resume") != (args.Length == 3))
        { Console.Error.WriteLine("Usage: MultiFidelitySearch --checkpoint-regression <baseline|start|resume> <new-output.json> [start-checkpoint.json]"); return 2; }
        string mode = args[0];
        var assembly = Assembly.GetExecutingAssembly();
        string binary = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant();
        string environment = EvolutionHash.Combine(new[] { Environment.Version.ToString(), RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString() });
        var ledger = new EvolutionResourceLedger("regression-recovery", EvolutionResources.Of("cost_units", 2100), retainedReceiptLimit: 128);
        EvolutionFidelityCheckpoint? checkpoint = null;
        if (mode == "resume")
        {
            var file = new FileInfo(args[2]);
            if (file.Length > 2 * 1024 * 1024) throw new InvalidDataException("Regression recovery input exceeds 2 MiB.");
            using var input = JsonDocument.Parse(await File.ReadAllTextAsync(file.FullName));
            var root = input.RootElement;
            if (root.GetProperty("Protocol").GetString() != "regression-process-recovery-v1" || root.GetProperty("Mode").GetString() != "start" ||
                root.GetProperty("AssemblySha256").GetString() != binary || root.GetProperty("EnvironmentHash").GetString() != environment)
                throw new InvalidDataException("Resume requires the same fixture binary/runtime/environment and a coordinated start checkpoint.");
            checkpoint = EvolutionFidelityCheckpoint.Parse(root.GetProperty("Checkpoint").GetString()!);
            ledger.RestoreState(checkpoint.GetResourceState());
        }
        // Rehydrating the explicitly supplied cohort and checkpoint I/O/process startup are not separately priced.
        // This fixture audits actual model-training/measurement work, not whole-machine restart economics.
        EvolutionCanonicalGenome<EvolutionSearchGenome>[] candidates;
        if (checkpoint is null)
            candidates = await EvolutionResourceWork.RunAsync(ledger, "initial-candidates", EvolutionResourceStage.Setup,
                EvolutionResources.Of("cost_units", 0.08m), EvolutionResources.Of("cost_units", 0.08m), _ =>
                    new ValueTask<EvolutionResourceResult<EvolutionCanonicalGenome<EvolutionSearchGenome>[]>>(
                        new EvolutionResourceResult<EvolutionCanonicalGenome<EvolutionSearchGenome>[]>(RegressionCampaign.CreateCandidates(), EvolutionResources.Of("cost_units", 0.08m))));
        else candidates = RegressionCampaign.CreateCandidates();
        var task = new IncrementalRegressionTask();
        var scheduler = new EvolutionFidelityScheduler<EvolutionSearchGenome>(RegressionCampaign.CreatePlan("GreedyHalving"), ledger,
            "regression-search-v1-continuation", "regression-confirm-v1", IncrementalRegressionTask.StateVersion, task.Evaluate, task.Evaluate);
        EvolutionFidelityCheckpoint? saved = null;
        EvolutionFidelityReport<EvolutionSearchGenome> report;
        if (mode == "baseline") report = await scheduler.RunAsync("regression-recovery-v1", candidates, 42);
        else report = await scheduler.RunCheckpointedAsync("regression-recovery-v1", candidates, 42, (value, _) =>
        { saved = value; return new(mode != "start" || value.SettledBatchCount < 9); }, checkpoint);
        bool valid = mode == "start" ? report.StopReason == EvolutionFidelityStopReason.Paused && saved?.SettledBatchCount == 9 : report.IsComplete;
        string json = JsonSerializer.Serialize(new
        {
            Protocol = "regression-process-recovery-v1",
            Mode = mode,
            Status = valid ? "completed" : "failed",
            ProcessId = Environment.ProcessId,
            AssemblyVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            AssemblySha256 = binary,
            EnvironmentHash = environment,
            EvaluatorCalls = task.Calls,
            task.ResumedCalls,
            task.ConfirmationCalls,
            ExecutedEpochs = task.Epochs,
            task.TrainingRowVisits,
            task.ValidationRowVisits,
            Measurements = task.Measurements,
            Report = RegressionCampaign.ExportReport(report),
            Checkpoint = mode == "start" ? saved?.ToJson() : null,
            Interpretation = "Coordinated settled-batch restart in separate processes; no unjournaled in-flight recovery. " +
                "Explicit token payload contains authored regression weights. Trusted local fixture only. Checkpoint I/O, process startup and state rehydration CPU are not separately priced."
        },
            new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(output); await writer.WriteLineAsync(json);
        return valid ? 0 : 1;
    }
}
