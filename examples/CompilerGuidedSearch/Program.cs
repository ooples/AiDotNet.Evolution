using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using AiDotNet.Evolution;
using AiDotNet.Evolution.CSharp;
using AiDotNet.Evolution.Programs;

// TRUSTED, authored fixtures only. A child process with a timeout is NOT a hostile-code security sandbox.
// A deployment must supply an externally isolated verifier; never run arbitrary model code with this example.
if (args.Length == 4 && args[0] == "--trusted-worker")
{
    var bytes = File.ReadAllBytes(args[1]);
    if (ProgramSnapshot.Digest(bytes) != args[2]) return 2;
    var values = JsonSerializer.Deserialize<int[]>(args[3])!;
    using var image = new MemoryStream(bytes);
    var assembly = AssemblyLoadContext.Default.LoadFromStream(image);
    var method = assembly.GetType("Algorithm")!.GetMethod("Run")!.CreateDelegate<Func<int, long>>();
    var outputs = values.Select(method).ToArray();
    long checksum = 0;
    for (int i = 0; i < 32; i++) checksum ^= method(values[^1]);
    var samples = new double[7];
    for (int sample = 0; sample < samples.Length; sample++)
    {
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 64; i++) checksum ^= method(values[^1]);
        samples[sample] = (double)(Stopwatch.GetTimestamp() - start) / Stopwatch.Frequency / 64;
    }
    Console.WriteLine(JsonSerializer.Serialize(new WorkerResult(outputs, samples, checksum, Environment.Version.ToString())));
    return 0;
}
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: CompilerGuidedSearch <new-private-evidence-directory> (trusted fixture only)");
    return 64;
}
string root = Path.GetFullPath(args[0]);
if (Directory.Exists(root)) throw new IOException("Use a fresh evidence directory.");
Directory.CreateDirectory(root);
var parent = new ProgramSnapshot(new Dictionary<string, string>
{
    ["Algorithm.cs"] = "public static class Algorithm { public static long Run(int x) { long sum = 0; for (int i = 0; i < x; i++) sum += i; return sum; } }",
    ["Helper.cs"] = "internal static class Helper { internal static long Sum(int x) { return x; } }"
});
var ledger = new EvolutionResourceLedger("compiler-smoke", ProgramImprovement.Units(100));
var options = new ImprovementOptions("trusted-smoke", "Run spends linear work summing integers; test a closed form.",
    "sum-independent-oracle-v1/runtime-" + Environment.Version, root);
ProgramPlanner planner = (request, _) =>
{
    var edits = new List<SourceEdit>();
    var target = request.Targets.First(t => t.File == "Algorithm.cs" && t.Kind == "Block");
    edits.Add(new(target, "{ return Helper.Sum(x); }"));
    var helper = request.Targets.First(t => t.File == "Helper.cs" && t.Kind == "ReturnStatement");
    // First model-free plan compiles but fails public correctness; the repair is guided by that public result.
    if (request.Attempt > 1)
    {
        if (!request.Feedback.StartsWith("Public correctness", StringComparison.Ordinal)) throw new InvalidOperationException("Expected public repair feedback.");
        edits.Add(new(helper, "return (long)x * (x - 1) / 2;"));
    }
    return new(new EvolutionResourceResult<PatchPlan>(new(parent.Fingerprint,
        "Replace the measured linear summation with its exact nonnegative-integer closed form.", edits), ProgramImprovement.Units(1)));
};
async ValueTask<EvolutionResourceResult<VerificationReceipt>> Verify(VerificationRequest request, bool final, CancellationToken token)
{
    var values = final ? new[] { 3, 23, 999, 200_003 } : new[] { 0, 1, 2, 17, 100_000 };
    string prefix = Path.Combine(root, request.Nonce);
    await File.WriteAllBytesAsync(prefix + ".dll", request.Artifact.GetImage(), token);
    var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--trusted-worker");
    start.ArgumentList.Add(prefix + ".dll");
    start.ArgumentList.Add(request.Artifact.ImageFingerprint);
    start.ArgumentList.Add(JsonSerializer.Serialize(values));
    using var process = Process.Start(start) ?? throw new IOException("Could not start the trusted worker.");
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
    timeout.CancelAfter(TimeSpan.FromSeconds(20));
    var outputTask = ReadBounded(process.StandardOutput, timeout.Token);
    var errorTask = ReadBounded(process.StandardError, timeout.Token);
    try
    {
        await process.WaitForExitAsync(timeout.Token);
        string output = await outputTask, error = await errorTask;
        if (process.ExitCode != 0 || error.Length != 0) throw new InvalidDataException("Trusted worker failed.");
        var result = JsonSerializer.Deserialize<WorkerResult>(output) ?? throw new InvalidDataException();
        if (result.Runtime != Environment.Version.ToString() || result.Outputs.Length != values.Length ||
            result.Samples.Length != 7 || result.Samples.Any(s => !double.IsFinite(s) || s <= 0)) throw new InvalidDataException();
        // Independent oracle in the supervisor, not in the evolved assembly. Kept outside repair requests.
        bool correct = values.Select(x => Enumerable.Range(0, x).Sum(i => (long)i)).SequenceEqual(result.Outputs);
        await File.WriteAllTextAsync(prefix + ".json", JsonSerializer.Serialize(new
        {
            request.Nonce,
            request.Artifact.Fingerprint,
            Phase = final ? "held-out" : "public",
            Inputs = values,
            Raw = result,
            Correct = correct,
            Units = 1,
            Timing = "7 samples x 64 invocations after 32 warmups"
        }), token);
        return new(new(request.Nonce, request.Artifact.Fingerprint, options.OracleIdentity, correct,
            result.Samples.OrderBy(x => x).ElementAt(3), correct ? "" : "Public correctness failed: sum differs from independent enumeration."),
            ProgramImprovement.Units(1));
    }
    finally
    {
        if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
        timeout.Cancel();
        try { await Task.WhenAll(outputTask, errorTask); } catch (OperationCanceledException) { }
    }
}
var outcome = await ProgramImprovement.RunAsync(parent,
    () => new CSharpProgramCompiler(new[] { typeof(object).Assembly.Location }, "runtime-" + Environment.Version),
    planner, (request, token) => Verify(request, false, token), (request, token) => Verify(request, true, token), ledger, options);
await File.WriteAllTextAsync(Path.Combine(root, "final-ledger.json"), ledger.CaptureState());
Console.WriteLine(JsonSerializer.Serialize(new
{
    outcome.Promoted,
    outcome.Attempts,
    outcome.Reason,
    outcome.BaselineConfirmation,
    outcome.CandidateConfirmation,
    Resources = ledger.Snapshot().Spent,
    Scope = "Authored integration fixture, not representative superiority or independent statistical confirmation."
}));
// Timing is environment-sensitive: smoke requires verified correctness and repair, not an artificial guaranteed win.
return outcome.Attempts == 2 && outcome.CandidateConfirmation is { Correct: true } &&
    outcome.BaselineConfirmation is { Correct: true } ? 0 : 1;

static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
{
    var buffer = new char[8193];
    int length = 0, count;
    while ((count = await reader.ReadAsync(buffer.AsMemory(length, buffer.Length - length), token)) != 0)
    {
        length += count;
        if (length > 8192) throw new InvalidDataException("Worker output exceeded its bound.");
    }
    return new string(buffer, 0, length);
}

internal sealed record WorkerResult(long[] Outputs, double[] Samples, long Checksum, string Runtime);
