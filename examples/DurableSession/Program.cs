using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

if (args.Length != 1) throw new ArgumentException("Usage: DurableSession <new-evidence-directory>");
string directory = Path.GetFullPath(args[0]);
if (Directory.Exists(directory)) throw new IOException("Use a new evidence directory; never reset a recovered campaign.");
Directory.CreateDirectory(directory);
using var session = Fixture.Session();
using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ask = (await session.AskAsync(1, guard.Token)).Single();
EvolutionWorkIdentity workerIdentity;
string payload;
using (var coordinator = Fixture.Coordinator(directory, session))
{
    var bridge = Fixture.Bridge(session, coordinator);
    Fixture.Require(bridge.Enqueue(ask), "enqueue original session work");
    using var protocol = new EvolutionWorkProtocol(coordinator);
    string claim = "{\"id\":1,\"protocol\":1,\"op\":\"claim\",\"worker\":{\"workerId\":\"native-worker\",\"compatibilityHash\":\"" + session.CompatibilityHash + "\"}}";
    using var response = JsonDocument.Parse(protocol.ProcessJson(claim));
    Fixture.Require(response.RootElement.GetProperty("ok").GetBoolean(), "wire claim");
    JsonElement lease = response.RootElement.GetProperty("lease");
    JsonElement identity = lease.GetProperty("identity");
    workerIdentity = new EvolutionWorkIdentity(identity.GetProperty("runId").GetString()!,
        long.Parse(identity.GetProperty("evaluationId").GetString()!, CultureInfo.InvariantCulture),
        identity.GetProperty("attempt").GetInt32(), identity.GetProperty("leaseId").GetString()!);
    payload = lease.GetProperty("payload").GetString()!;
}

// Deliberately recover just the delivery coordinator while keeping the original engine
// alive. This is NOT a claim of recovering that engine across a process crash.
using var restored = Fixture.Coordinator(directory, session);
var resumed = Fixture.Bridge(session, restored);
var envelope = EvolutionDurableEvaluationPayload.FromJson(payload);
Fixture.Require(envelope.Context.RootSeed == ulong.MaxValue && envelope.Context.SeedStream == ask.Context.SeedStream, "full context bits");
int genome = int.Parse(envelope.GenomePayload, CultureInfo.InvariantCulture);
double quality = genome * genome; // Exactly one physical evaluator invocation in this probe.
Fixture.Require(restored.Commit(workerIdentity, "native-worker", quality.ToString(CultureInfo.InvariantCulture), "integer-square-v1", Fixture.Cost(2))
    == EvolutionWorkCommitDisposition.Accepted, "durable receipt");
Fixture.Require(resumed.DeliverAvailableResults() == 1 && resumed.DeliverAvailableResults() == 0, "fenced engine delivery");
var completed = await session.Completion;
Fixture.Require(completed.Best?.Evaluation.Quality == 49, "engine archive receives decoded result");
bool forkRequired = false;
using (var newSession = Fixture.Session())
{
    try { _ = Fixture.Bridge(newSession, restored); }
    catch (InvalidOperationException) { forkRequired = true; }
}
Fixture.Require(forkRequired, "new engine cannot attach by matching run/evaluation IDs");
string processPath = Environment.ProcessPath!;
bool managed = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
var report = new ProbeReport
{
    CoreInformationalVersion = typeof(EvolutionSession<int>).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
    ExecutableSha256 = Fixture.Hash(processPath),
    ManagedEntrySha256 = managed ? Fixture.Hash(Path.Combine(AppContext.BaseDirectory, "DurableSession.dll")) : null,
    ManagedCoreSha256 = managed ? Fixture.Hash(Path.Combine(AppContext.BaseDirectory, "AiDotNet.Evolution.dll")) : null,
    SourceSessionId = session.InstanceId,
    SourceLeaseId = ask.WorkIdentity!.LeaseId,
    WorkerLeaseId = workerIdentity.LeaseId,
    RootSeed = envelope.Context.RootSeed.ToString(CultureInfo.InvariantCulture),
    SeedStream = envelope.Context.SeedStream.ToString(CultureInfo.InvariantCulture),
    Quality = quality,
    PhysicalEvaluations = 1,
    CoordinatorReopenedWhileEngineAlive = restored.WasRecovered,
    DifferentEngineRequiresExplicitFork = forkRequired,
    Spent = restored.Resources.Spent["cost_units"].ToString(CultureInfo.InvariantCulture),
    Reserved = restored.Resources.Reserved["cost_units"].ToString(CultureInfo.InvariantCulture),
    Settled = restored.Resources.Settled,
};
string json = JsonSerializer.Serialize(report, ProbeJson.Default.ProbeReport);
File.WriteAllText(Path.Combine(directory, "session.json"), json);
Console.WriteLine(json);

internal static class Fixture
{
    internal static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);
    internal static DurableEvolutionWorkCoordinator Coordinator(string directory, EvolutionSession<int> session) => new(directory, session.RunId, session.CompatibilityHash, Cost(10));
    internal static EvolutionDurableSessionBridge<int> Bridge(EvolutionSession<int> session, DurableEvolutionWorkCoordinator coordinator) => new(session, coordinator,
        payload => EvolutionTaskResult.Completed(double.Parse(payload, CultureInfo.InvariantCulture), new Dictionary<string, double> { ["x"] = 7 }), new(), Cost(1), Cost(5));
    internal static EvolutionSession<int> Session() => new(task => new EvolutionEngine<int>(task, new UnusedVariation(),
        _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("x", 0, 10, 10) }),
        new EvolutionEngineOptions
        {
            RunId = "durable-session-probe",
            Seed = ulong.MaxValue,
            MaxProposals = 1,
            MaxEvaluationAttempts = 1,
            MaxGenerations = 0,
            CheckpointInterval = 0,
            EvaluationTimeout = TimeSpan.FromSeconds(60)
        }, genomeCodec: new IntegerCodec()),
        new[] { 7 }, value => "integer:" + value.ToString(CultureInfo.InvariantCulture), new EvolutionExternalTaskIdentity("integer", "task-v1", "square-result-codec-v1"));
    internal static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException("Probe failed: " + name); }
    internal static string Hash(string path) { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant(); }
}
internal sealed class UnusedVariation : IVariationOperator<int>
{
    public string Id => "unused";
    public string VersionHash => "v1";
    public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Only the seed is admitted.");
}
internal sealed class IntegerCodec : IEvolutionGenomeCodec<int>
{
    public string Id => "integer";
    public string VersionHash => "v1";
    public string Serialize(int value) => value.ToString(CultureInfo.InvariantCulture);
    public int Deserialize(string value) => int.Parse(value, CultureInfo.InvariantCulture);
}
internal sealed class ProbeReport
{
    public string CoreInformationalVersion { get; set; } = "";
    public string ExecutableSha256 { get; set; } = "";
    public string? ManagedEntrySha256 { get; set; }
    public string? ManagedCoreSha256 { get; set; }
    public string SourceSessionId { get; set; } = "";
    public string SourceLeaseId { get; set; } = "";
    public string WorkerLeaseId { get; set; } = "";
    public string RootSeed { get; set; } = "";
    public string SeedStream { get; set; } = "";
    public double Quality { get; set; }
    public int PhysicalEvaluations { get; set; }
    public bool CoordinatorReopenedWhileEngineAlive { get; set; }
    public bool DifferentEngineRequiresExplicitFork { get; set; }
    public string Spent { get; set; } = "";
    public string Reserved { get; set; } = "";
    public long Settled { get; set; }
}
[JsonSerializable(typeof(ProbeReport))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class ProbeJson : JsonSerializerContext;
