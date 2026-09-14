using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Quality;

internal static class RepresentativeSuite
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: --suite <frozen-request.json> <new-result.json>");
        if (new FileInfo(args[0]).Length > 128 * 1024) throw new InvalidDataException("Suite request exceeds 128 KiB.");
        byte[] payload = File.ReadAllBytes(args[0]);
        if (payload.Length > 128 * 1024) throw new InvalidDataException("Suite request exceeds 128 KiB.");
        var request = JsonSerializer.Deserialize<NumericSuiteRequest>(payload, Json) ?? throw new InvalidDataException("Missing suite request.");
        Validate(request);
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var runs = new List<object>();
        bool complete = true;
        var clock = Stopwatch.StartNew();
        foreach (var instance in request.Instances.OrderBy(value => value.Id, StringComparer.Ordinal).ThenBy(value => value.Seed))
        {
            var task = new SuiteNumericTask(instance.Id, instance.Seed);
            foreach (ulong searchSeed in instance.SearchSeeds)
                foreach (string methodName in request.Methods)
                {
                    QualityMethod method = Enum.Parse<QualityMethod>(methodName);
                    var measured = await QualityExperiment.RunAsync(QualityTask.Sphere, method, searchSeed, request.Budget, task);
                    complete &= measured.Status == "completed";
                    runs.Add(new
                    {
                        TaskId = task.Id,
                        task.Family,
                        task.VersionHash,
                        EvaluatorHash = task.VersionHash,
                        InstanceSeed = instance.Seed,
                        SearchSeed = searchSeed,
                        WorkUnitsPerEvaluation = task.WorkUnits,
                        Measurement = measured
                    });
                }
        }
        var binary = Assembly.GetExecutingAssembly().Location;
        var core = typeof(EvolutionEngineOptions).Assembly.Location;
        var dependencies = Path.ChangeExtension(binary, ".deps.json");
        using var dependencyDocument = JsonDocument.Parse(File.ReadAllBytes(dependencies));
        await JsonSerializer.SerializeAsync(output, new
        {
            Schema = "aidotnet-numeric-suite-result-v1",
            Request = request,
            RequestHash = Digest(payload),
            Status = complete ? "completed" : "incomplete",
            ElapsedSeconds = clock.Elapsed.TotalSeconds,
            Environment = new
            {
                Runtime = RuntimeInformation.FrameworkDescription,
                OS = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                ProcessorsAvailable = System.Environment.ProcessorCount,
                CpuIdentifier = CpuIdentifier(),
                ManagedMemoryAvailableBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                BenchmarkAssemblyHash = Digest(File.ReadAllBytes(binary)),
                CoreAssemblyHash = Digest(File.ReadAllBytes(core)),
                DependencyManifestHash = Digest(File.ReadAllBytes(dependencies)),
                DependencyManifest = dependencyDocument.RootElement
            },
            Semantics = "Same eight initial genomes and information per task/search seed; two fixed feasible anchors; descriptors are coordinates, not fitness. Cache reuse disabled. No model calls. Work units are task-defined, not FLOPs, price or CPU time. No timing-speedup claim.",
            Runs = runs
        }, Json);
        return complete ? 0 : 1;
    }

    private static void Validate(NumericSuiteRequest request)
    {
        if (request.Schema != "aidotnet-numeric-suite-request-v1" || request.Partition is not ("development" or "selection" or "final") ||
            request.Mode is not ("contract-smoke" or "registered") || !Hex(request.PlanHash, 64) || !Hex(request.ConfigurationHash, 64) ||
            !Hex(request.SourceRevision, 40) || request.Budget is < 8 or > 4096 || request.Instances is null || request.Instances.Length is < 3 or > 48 ||
            request.Methods is null || request.Methods.Length is < 3 or > 6 || request.Methods.Distinct().Count() != request.Methods.Length ||
            request.Methods.Any(value => !Enum.GetNames<QualityMethod>().Contains(value, StringComparer.Ordinal)) ||
            !request.Methods.Contains("RandomSearch") || !request.Methods.Contains("HillClimb"))
            throw new InvalidDataException("Invalid frozen suite contract.");
        string[] required = request.Partition switch
        {
            "development" => ["block-trap", "knapsack", "rastrigin"],
            "selection" => ["coupled-absolute", "diffusion-control", "stochastic-regression"],
            _ => ["inventory-risk", "robust-design", "spin-glass"]
        };
        if (request.Instances.Any(value => value is null || value.SearchSeeds is null || value.SearchSeeds.Length is < 1 or > 16 ||
            value.SearchSeeds.Distinct().Count() != value.SearchSeeds.Length || new SuiteNumericTask(value.Id, value.Seed).Partition != request.Partition) ||
            !request.Instances.Select(value => value.Id).Distinct().OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(required) ||
            request.Instances.Select(value => (value.Id, value.Seed)).Distinct().Count() != request.Instances.Length ||
            request.Instances.GroupBy(value => value.Id).Select(group => group.Count()).Distinct().Count() != 1 ||
            (long)request.Instances.Sum(value => value.SearchSeeds.Length) * request.Budget * Enum.GetValues<QualityMethod>().Length > 2_000_000)
            throw new InvalidDataException("Missing, duplicated, cross-partition or excessive suite instances.");
    }
    private static bool Hex(string? value, int length) => value is not null && value.Length == length && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static string CpuIdentifier()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return System.Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable";
        if (File.Exists("/proc/cpuinfo")) return File.ReadLines("/proc/cpuinfo").Take(128).FirstOrDefault(value => value.StartsWith("model name", StringComparison.Ordinal)) ?? "unavailable";
        return "unavailable";
    }

    private sealed record NumericSuiteRequest(string Schema, string Partition, string Mode, string PlanHash,
        string ConfigurationHash, string SourceRevision, int Budget, NumericSuiteInstance[] Instances, string[] Methods);
    private sealed record NumericSuiteInstance(string Id, uint Seed, ulong[] SearchSeeds);
}
