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

    internal static int Run(string[] args)
    {
        if (args.Length != 3 || !Enum.TryParse(args[0], out QualityTask task) || !Enum.IsDefined(task) ||
            task.ToString() != args[0] || !ulong.TryParse(args[1], out ulong seed) || seed > 999 ||
            !int.TryParse(args[2], out int budget) || budget is < 8 or > 1_000_000)
        {
            Console.Error.WriteLine("Usage: --numeric-service <named-task> <seed 0..999> <evaluation-cap 8..1000000>");
            return 2;
        }
        var ledger = new EvolutionResourceLedger($"external-{task}-{seed}", new EvolutionResources(
            new Dictionary<string, decimal> { ["cost_units"] = budget, ["proposal_calls"] = budget - QualityExperiment.InitialPopulation }),
            retainedReceiptLimit: 64, maximumOperations: budget);
        double[][] initial = QualityExperiment.InitialUnits(seed);
        string[] hashes = initial.Select(values => QualityExperiment.GenomeIdentity(QualityExperiment.ToCoordinates(values))).ToArray();
        var assembly = typeof(NumericObjectiveService).Assembly;
        Write(new
        {
            Kind = "manifest",
            Protocol = "numeric-objective-service-v1",
            Task = task,
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
                string line = ReadBoundedLine(Console.In) ?? throw new InvalidDataException("Explicit finish required.");
                // JSON null is the sole terminal request; no success is inferred from a disconnected controller.
                if (line == "null")
                {
                    Write(new { Kind = "summary", EvaluatorCalls = calls, BestLoss = best, Samples = samples, Resources = ledger.Snapshot() });
                    return 0;
                }
                double[] units = JsonSerializer.Deserialize<double[]>(line) ?? throw new InvalidDataException("Missing coordinates.");
                if (units.Length != QualityExperiment.Dimensions || units.Any(value => !double.IsFinite(value) || value < 0 || value > 1))
                    throw new InvalidDataException("Expected eight finite unit coordinates.");
                string genomeHash = QualityExperiment.GenomeIdentity(QualityExperiment.ToCoordinates(units));
                if (calls < hashes.Length && genomeHash != hashes[calls]) throw new InvalidDataException("Initial population differs.");
                var cost = new EvolutionResources(new Dictionary<string, decimal>
                { ["cost_units"] = 1, ["proposal_calls"] = calls < hashes.Length ? 0 : 1 });
                using var reservation = ledger.TryReserve("dispatch-" + calls, EvolutionResourceStage.Evaluation, cost, cost)
                    ?? throw new InvalidDataException("Evaluation cap exceeded.");
                calls++;
                double loss = QualityExperiment.Loss(task, QualityExperiment.ToCoordinates(units));
                reservation.Complete(cost);
                best = Math.Min(best ?? double.MaxValue, loss);
                samples.Add(new(calls - 1, EvolutionEvaluationStatus.Completed, best, 1, 1, Array.Empty<string>()));
                Write(new { Kind = "measurement", EvaluationId = calls - 1, Loss = loss, GenomeHash = genomeHash, CostUnits = 1 });
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Write(new { Kind = "error", Error = exception.GetType().Name, EvaluatorCalls = calls, BestLoss = best, Samples = samples, Resources = ledger.Snapshot() });
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

    private static void Write(object value) { Console.WriteLine(JsonSerializer.Serialize(value, Json)); Console.Out.Flush(); }
}
