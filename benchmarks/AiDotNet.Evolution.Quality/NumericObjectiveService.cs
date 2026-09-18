using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Quality;

/// <summary>Local benchmark-only JSONL bridge; external optimizers execute the same C# objective and initialization.</summary>
internal static class NumericObjectiveService
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    internal static int Run(string[] args, bool suite = false, TextReader? input = null, TextWriter? output = null)
    {
        input ??= Console.In;
        output ??= Console.Out;
        void Write(object value) { output.WriteLine(JsonSerializer.Serialize(value, Json)); output.Flush(); }
        QualityTask task = default;
        ulong instanceSeed = 0;
        if (args.Length != (suite ? 4 : 3) ||
            (!suite && (!Enum.TryParse(args[0], out task) || !Enum.IsDefined(task) || task.ToString() != args[0])) ||
            !ulong.TryParse(args[1], out ulong seed) || (!suite && seed > 999) ||
            !int.TryParse(args[2], out int budget) || budget is < 8 or > 1_000_000 ||
            (suite && (budget > 4096 || !uint.TryParse(args[3], out _) || !ulong.TryParse(args[3], out instanceSeed))))
        {
            Console.Error.WriteLine("Usage: --numeric-service <named-task> <seed 0..999> <cap 8..1000000>; --suite-numeric-service <suite-task> <search-seed> <cap 8..4096> <uint-instance-seed>");
            return 2;
        }
        SuiteNumericTask? suiteTask;
        try { suiteTask = suite ? new SuiteNumericTask(args[0], instanceSeed) : null; }
        catch (ArgumentException) { Console.Error.WriteLine("Unregistered suite task."); return 2; }
        int work = suiteTask?.WorkUnits ?? 1;
        var ledger = new EvolutionResourceLedger($"external-{task}-{seed}", new EvolutionResources(
            new Dictionary<string, decimal> { ["cost_units"] = (decimal)budget * work, ["proposal_calls"] = budget - QualityExperiment.InitialPopulation }),
            retainedReceiptLimit: 64, maximumOperations: budget);
        double[][] initial = QualityExperiment.SharedInitialUnits(seed, suite);
        string[] hashes = initial.Select(values => QualityExperiment.GenomeIdentity(QualityExperiment.ToCoordinates(values))).ToArray();
        var assembly = typeof(NumericObjectiveService).Assembly;
        Write(new
        {
            Kind = "manifest",
            Protocol = suite ? "suite-numeric-objective-service-v1" : "numeric-objective-service-v1",
            Task = suiteTask?.Id ?? task.ToString(),
            InstanceSeed = suite ? (ulong?)instanceSeed : null,
            VersionHash = suiteTask?.VersionHash,
            WorkUnitsPerEvaluation = work,
            Seed = seed,
            Budget = budget,
            Dimensions = QualityExperiment.Dimensions,
            InitialUnits = initial,
            InitialGenomeHashes = hashes,
            InitialPopulationHash = EvolutionHash.Combine(hashes),
            AssemblyVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            AssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))).ToLowerInvariant()
        });
        int calls = 0;
        double? best = null;
        var samples = new List<SampleRecord>();
        try
        {
            while (true)
            {
                string line = ReadBoundedLine(input) ?? throw new InvalidDataException("Explicit finish required.");
                // JSON null is the sole terminal request; no success is inferred from a disconnected controller.
                if (line == "null")
                {
                    Write(new { Kind = "summary", EvaluatorCalls = calls, BestLoss = best, Samples = Published(samples, suite), Resources = ledger.Snapshot() });
                    return 0;
                }
                double[] units = JsonSerializer.Deserialize<double[]>(line) ?? throw new InvalidDataException("Missing coordinates.");
                if (units.Length != QualityExperiment.Dimensions || units.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
                    throw new InvalidDataException("Expected eight finite unit coordinates.");
                string genomeHash = QualityExperiment.GenomeIdentity(QualityExperiment.ToCoordinates(units));
                if (calls < hashes.Length && genomeHash != hashes[calls]) throw new InvalidDataException("Initial population differs.");
                var cost = new EvolutionResources(new Dictionary<string, decimal>
                { ["cost_units"] = work, ["proposal_calls"] = calls < hashes.Length ? 0 : 1 });
                using var reservation = ledger.TryReserve("dispatch-" + calls, EvolutionResourceStage.Evaluation, cost, cost)
                    ?? throw new InvalidDataException("Evaluation cap exceeded.");
                calls++;
                double[] coordinates = QualityExperiment.ToCoordinates(units);
                (double loss, double violation) = suiteTask?.Evaluate(coordinates, calls - 1)
                    ?? (QualityExperiment.Loss(task, coordinates), 0d);
                reservation.Complete(cost);
                if (violation <= 0) best = Math.Min(best ?? double.MaxValue, loss);
                samples.Add(new(calls - 1, EvolutionEvaluationStatus.Completed, best, 1, work, Array.Empty<string>(), -loss,
                    suite ? new[] { violation } : Array.Empty<double>(), new Dictionary<string, double>
                    {
                        ["coordinate-0"] = -5 + 10 * units[0],
                        ["coordinate-1"] = -5 + 10 * units[1]
                    }, genomeHash));
                Write(new { Kind = "measurement", EvaluationId = calls - 1, Loss = loss, Violation = violation, GenomeHash = genomeHash, CostUnits = work });
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Write(new { Kind = "error", Error = exception.GetType().Name, EvaluatorCalls = calls, BestLoss = best, Samples = Published(samples, suite), Resources = ledger.Snapshot() });
            return 1;
        }
    }

    private static string? ReadBoundedLine(TextReader reader)
    {
        var value = new StringBuilder();
        for (int count = 0; count <= 4096; count++)
        {
            int next = reader.Read();
            if (next < 0) return value.Length == 0 ? null : value.ToString();
            if (next == '\n') return value.ToString().TrimEnd('\r');
            value.Append((char)next);
        }
        throw new InvalidDataException("Request exceeds 4096 characters.");
    }

    /// <summary>
    /// The published sample shape. <c>numeric-objective-service-v1</c> is frozen at evaluation identity,
    /// status, best loss so far, attempts, cost and diagnostics: the in-process record carries more than
    /// that -- quality, constraint violations, descriptors and the genome id, which the archive pilot
    /// reads -- and adding those members to the established protocol silently breaks every external
    /// controller that compares the summary against the evidence it recorded itself.
    /// <para><c>suite-numeric-objective-service-v1</c> is a new protocol with no deployed controllers, and
    /// US-02 requires the external service to be provably measurement-identical to the in-process engine,
    /// so it publishes the full record. The frozen protocol is never widened; the new one is never narrowed.</para>
    /// </summary>
    private static object[] Published(List<SampleRecord> samples, bool suite)
        => samples.Select(sample => suite ? (object)sample : new
        {
            sample.EvaluationId,
            sample.Status,
            sample.BestLoss,
            sample.Attempts,
            sample.CostUnits,
            sample.DiagnosticCodes
        }).ToArray();
}
