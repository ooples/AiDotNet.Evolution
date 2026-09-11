using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

internal static class Program
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    private static readonly EvolutionWorkCoordinatorOptions Options = new(16, 3, 256 * 1024, 4096, 8, TimeSpan.FromSeconds(10));
    private const string RunId = "durable-process-check";
    private const string Compatibility = "integer-square/task-v1/evaluator-v1/codec-v1";
    private static int _physicalEvaluations;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: DurableWork verify|claim-and-hold|commit-and-hold|worker-and-hold <evidence-directory>");
        if (args[0] == "verify")
        {
            Directory.CreateDirectory(args[1]);
            RecoveryReport report = await VerifyAsync(Path.GetFullPath(args[1]));
            string json = JsonSerializer.Serialize(report, EvidenceJson.Default.RecoveryReport);
            File.WriteAllText(Path.Combine(args[1], "recovery.json"), json);
            Console.WriteLine(json);
            return 0;
        }
        if (args[0] == "worker-and-hold")
        {
            Require(Square("7") == "49", "worker evaluation");
            Marker(new ProcessMarker { Stage = "evaluated-without-receipt", ProcessId = Environment.ProcessId });
        }
        else
        {
            using var coordinator = Open(args[1], args[0] == "commit-and-hold" ? Start.AddSeconds(1) : Start);
            if (args[0] == "claim-and-hold")
            {
                Enqueue(coordinator);
                EvolutionWorkLease lease = coordinator.Claim(Worker("original")) ?? throw new InvalidOperationException("No initial lease.");
                Marker(new ProcessMarker { Stage = "dispatch-published", ProcessId = Environment.ProcessId, Identity = lease.Identity });
            }
            else if (args[0] == "commit-and-hold")
            {
                EvolutionWorkLease lease = coordinator.GetUnsettledDeliveries("original").Single();
                EvolutionWorkCommitDisposition disposition = coordinator.Commit(lease.Identity, lease.WorkerId,
                    Square(lease.Payload), "square-v1/input:7", Cost(2));
                Require(disposition == EvolutionWorkCommitDisposition.Accepted, "first result commit");
                Marker(new ProcessMarker { Stage = "result-published", ProcessId = Environment.ProcessId, Identity = lease.Identity });
            }
            else throw new ArgumentException("Unknown child mode.");
            // Parent terminates this process while the coordinator still owns its lock; no Dispose/finally cleanup runs.
            await Task.Delay(Timeout.InfiniteTimeSpan);
        }
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 1;
    }

    private static async Task<RecoveryReport> VerifyAsync(string root)
    {
        string recovery = Path.Combine(root, "coordinator-" + Guid.NewGuid().ToString("N"));
        ProcessMarker dispatch = await KillAfterMarkerAsync("claim-and-hold", recovery);
        Require(dispatch.Stage == "dispatch-published" && dispatch.Identity is not null, "dispatch marker");
        using (var coordinator = Open(recovery, Start))
        {
            Require(coordinator.WasRecovered, "actual process recovery");
            var unresolved = coordinator.GetUnsettledDeliveries("original").Single();
            Require(unresolved.Identity.LeaseId == dispatch.Identity!.LeaseId, "same persisted delivery, not redispatch");
            Require(coordinator.Resources.Reserved["cost_units"] == 5 && coordinator.Resources.Admitted == 1, "persisted reservation");
            Require(coordinator.Claim(Worker("other")) is null, "recovery cannot redispatch a live lease");
        }
        ProcessMarker committed = await KillAfterMarkerAsync("commit-and-hold", recovery);
        Require(committed.Stage == "result-published" && committed.Identity?.LeaseId == dispatch.Identity!.LeaseId, "committed marker");
        using (var coordinator = Open(recovery, Start.AddSeconds(2)))
        {
            Require(coordinator.GetResult(7, 1)?.Payload == "49", "result survives coordinator death");
            Require(coordinator.Commit(dispatch.Identity!, "original", "49", "square-v1/input:7", Cost(2))
                == EvolutionWorkCommitDisposition.Duplicate, "lost acknowledgement is idempotent");
            Require(coordinator.Resources.Spent["cost_units"] == 2 && coordinator.Resources.Reserved["cost_units"] == 0
                && coordinator.Resources.Settled == 1, "one physical receipt after two restarts");
        }

        string workers = Path.Combine(root, "worker-" + Guid.NewGuid().ToString("N"));
        DateTimeOffset now = Start;
        ProcessMarker killedWorker;
        EvolutionWorkIdentity oldIdentity;
        EvolutionWorkIdentity retryIdentity;
        using (var coordinator = new DurableEvolutionWorkCoordinator(workers, RunId, Compatibility, Cost(10), Options, () => now))
        {
            Enqueue(coordinator);
            EvolutionWorkLease original = coordinator.Claim(Worker("lost-worker"))!;
            oldIdentity = original.Identity;
            killedWorker = await KillAfterMarkerAsync("worker-and-hold", workers);
            Require(killedWorker.Stage == "evaluated-without-receipt", "worker died before reporting receipt");
            now = now.AddSeconds(11);
            EvolutionWorkLease retry = coordinator.Claim(Worker("replacement-incarnation"))!;
            retryIdentity = retry.Identity;
            Require(retry.Identity.LeaseId != original.Identity.LeaseId && retry.DeliveryNumber == 2, "worker retry fencing");
            Require(coordinator.Resources.Reserved["cost_units"] == 10, "lost work was not refunded");
            Require(coordinator.Commit(retry.Identity, retry.WorkerId, Square(retry.Payload), "square-v1/replacement", Cost(2))
                == EvolutionWorkCommitDisposition.Accepted, "replacement result");
            Require(coordinator.Resources.Spent["cost_units"] == 2 && coordinator.Resources.Reserved["cost_units"] == 5, "unreported liability survives retry");
        }
        using var final = Open(workers, Start.AddSeconds(12));
        Require(final.GetResult(7, 1)?.Identity.LeaseId == retryIdentity.LeaseId, "replacement persists");
        Require(final.GetUnsettledDeliveries("lost-worker").Single().Identity.LeaseId == oldIdentity.LeaseId, "lost receipt still reconcilable");
        Require(!final.SupportsExactSearchContinuation && final.SearchContinuationGuarantee.Contains("fork", StringComparison.Ordinal), "honest search continuity contract");

        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        using var stream = File.OpenRead(executable);
        var binaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string path in new[] { executable, Path.Combine(AppContext.BaseDirectory, "DurableWork.dll"),
            Path.Combine(AppContext.BaseDirectory, "AiDotNet.Evolution.dll") }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            using var binary = File.OpenRead(path);
            binaries.Add(Path.GetFileName(path), Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant());
        }
        int physicalEvaluations = dispatch.PhysicalEvaluations + committed.PhysicalEvaluations + killedWorker.PhysicalEvaluations + _physicalEvaluations;
        Require(physicalEvaluations == 3, "actual physical evaluation counter");
        return new RecoveryReport
        {
            Schema = 1,
            Verified = true,
            CapturedUtc = DateTimeOffset.UtcNow,
            Runtime = RuntimeInformation.FrameworkDescription,
            OperatingSystem = RuntimeInformation.OSDescription,
            ExecutableSha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
            BinarySha256 = binaries,
            CoreInformationalVersion = typeof(DurableEvolutionWorkCoordinator).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unavailable",
            KilledProcessIds = new[] { dispatch.ProcessId, committed.ProcessId, killedWorker.ProcessId },
            DispatchIdentity = dispatch.Identity!,
            RetryIdentity = retryIdentity,
            PhysicalEvaluations = physicalEvaluations,
            CoordinatorRecoverySpent = 2,
            CoordinatorRecoveryReserved = 0,
            WorkerRecoverySpent = final.Resources.Spent["cost_units"],
            WorkerRecoveryReserved = final.Resources.Reserved["cost_units"],
            ContinuationGuarantee = final.SearchContinuationGuarantee,
            AccountingUnits = "Authored cost_units, not measured CPU time or monetary spend.",
            CoordinatorDirectory = recovery,
            WorkerDirectory = workers,
        };
    }

    private static async Task<ProcessMarker> KillAfterMarkerAsync(string mode, string directory)
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing executable path.");
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "DurableWork.dll"));
        start.ArgumentList.Add(mode); start.ArgumentList.Add(directory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Child process did not start.");
        Task<string> errors = process.StandardError.ReadToEndAsync();
        string? line;
        try { line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally
        {
            // Only the exact child started above, never an unrelated dotnet/worker process.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        string stderr = await errors;
        if (line is null) throw new InvalidOperationException("Child produced no marker: " + stderr);
        ProcessMarker marker = JsonSerializer.Deserialize(line, EvidenceJson.Default.ProcessMarker) ?? throw new InvalidDataException("Missing process marker.");
        Require(marker.ProcessId == process.Id && process.ExitCode != 0, "observed abrupt child termination");
        return marker;
    }

    private static DurableEvolutionWorkCoordinator Open(string directory, DateTimeOffset now) => new(directory, RunId, Compatibility, Cost(10), Options, () => now);
    private static EvolutionWorkerProfile Worker(string id) => new(id, Compatibility);
    private static EvolutionResources Cost(decimal amount) => EvolutionResources.Of("cost_units", amount);
    private static void Enqueue(DurableEvolutionWorkCoordinator coordinator) => coordinator.Enqueue(7, 1, EvolutionHash.Compute("7"), "7", new(), Cost(2), Cost(5));
    private static string Square(string payload) { Interlocked.Increment(ref _physicalEvaluations); int value = int.Parse(payload, CultureInfo.InvariantCulture); return checked(value * value).ToString(CultureInfo.InvariantCulture); }
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException("Recovery invariant failed: " + name); }
    private static void Marker(ProcessMarker marker) { marker.PhysicalEvaluations = _physicalEvaluations; Console.WriteLine(JsonSerializer.Serialize(marker, EvidenceJson.Default.ProcessMarker)); Console.Out.Flush(); }
}

internal sealed class ProcessMarker
{
    public string Stage { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public int PhysicalEvaluations { get; set; }
    public EvolutionWorkIdentity? Identity { get; set; }
}
internal sealed class RecoveryReport
{
    public int Schema { get; set; }
    public bool Verified { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
    public string Runtime { get; set; } = string.Empty;
    public string OperatingSystem { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public Dictionary<string, string> BinarySha256 { get; set; } = new();
    public string CoreInformationalVersion { get; set; } = string.Empty;
    public int[] KilledProcessIds { get; set; } = Array.Empty<int>();
    public EvolutionWorkIdentity DispatchIdentity { get; set; } = null!;
    public EvolutionWorkIdentity RetryIdentity { get; set; } = null!;
    public int PhysicalEvaluations { get; set; }
    public decimal CoordinatorRecoverySpent { get; set; }
    public decimal CoordinatorRecoveryReserved { get; set; }
    public decimal WorkerRecoverySpent { get; set; }
    public decimal WorkerRecoveryReserved { get; set; }
    public string ContinuationGuarantee { get; set; } = string.Empty;
    public string AccountingUnits { get; set; } = string.Empty;
    public string CoordinatorDirectory { get; set; } = string.Empty;
    public string WorkerDirectory { get; set; } = string.Empty;
}

[JsonSerializable(typeof(ProcessMarker))]
[JsonSerializable(typeof(RecoveryReport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class EvidenceJson : JsonSerializerContext;
