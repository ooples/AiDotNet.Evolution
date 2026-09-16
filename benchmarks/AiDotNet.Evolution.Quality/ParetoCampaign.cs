using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiDotNet.Evolution.Quality;

/// <summary>Fixed-budget authored development fixtures; no model calls or competitor-superiority claims.</summary>
internal static class ParetoCampaign
{
    internal readonly record struct Genome(double X, double Y);
    internal sealed record Sample(long Id, string GenomeId, string Status, double? Quality, double[] Objectives,
        double[] Violations, int Attempts, double Cost, string? Insertion, string[] Diagnostics);
    internal sealed record Elite(string GenomeId, double Quality, double[] Objectives);
    internal sealed record Row(int Dimensions, string Method, ulong Seed, int Budget, string Status, string? Error,
        string InitialHash, string? StateHash, bool ReplayMatched, long Calls, long Proposals, double Cost, double Milliseconds,
        double Hypervolume, int RetainedCount, int FeasibleSamples, double[] ObjectiveMinima, double[] ObjectiveMaxima,
        Sample[] Samples, Elite[] Elites);

    internal static EvolutionParetoDefinition Definition(int dimensions) => new(Enumerable.Range(0, dimensions)
        .Select(i => new EvolutionObjectiveDefinition("loss-" + i, EvolutionOptimizationDirection.Minimize, 0, 2)), 64, constraintCount: 1);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("--pareto-study <source-revision> <new-report.json>");
        string coreVersion = typeof(EvolutionEngineOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        string harnessVersion = typeof(ParetoCampaign).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        if (!coreVersion.EndsWith("+" + args[0], StringComparison.Ordinal) || !harnessVersion.EndsWith("+" + args[0], StringComparison.Ordinal))
            throw new ArgumentException("Source revision must match both compiled assemblies.");
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<Row>();
        var replays = new List<Row>();
        foreach (int dimensions in new[] { 2, 3 })
            for (ulong seed = 0; seed < 24; seed++)
                foreach (string method in new[] { "scalar-single", "scalar-map64", "pareto64" })
                {
                    var row = await RunCase(dimensions, method, seed, 128, workers: 1);
                    var replay = await RunCase(dimensions, method, seed, 128, workers: 4);
                    replays.Add(replay);
                    row = row with
                    {
                        ReplayMatched = row.Status == "completed" && replay.Status == "completed" && row.StateHash == replay.StateHash &&
                        JsonSerializer.Serialize(row.Samples) == JsonSerializer.Serialize(replay.Samples)
                    };
                    rows.Add(row);
                    Console.Error.WriteLine($"{dimensions}/{method}/{seed}: {row.Status} HV={row.Hypervolume:R} replay={row.ReplayMatched}");
                }
        await JsonSerializer.SerializeAsync(output, new
        {
            Protocol = "pareto-development-v1",
            Partition = "authored-development",
            SourceRevision = args[0],
            CoreSha256 = Digest(typeof(EvolutionEngineOptions).Assembly.Location),
            HarnessSha256 = Digest(typeof(ParetoCampaign).Assembly.Location),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Seeds = 24,
            Budget = 128,
            PrimaryRuns = 144,
            ReplayRuns = 144,
            Capacity = 64,
            ObjectiveBounds = new[] { 0.0, 2.0 },
            Directions = "all minimize",
            NormalizedReference = "(1,...,1)",
            Tolerance = 0,
            Constraints = "x+y<=1.4, shared evaluator gate; every attempted call costs 1, including rejects",
            ScalarQuality = "negative arithmetic mean of all objective losses",
            Comparability = "Identical 8 seed genomes, mutation operator and call/proposal budgets; scalar-map64 has capacity 64; scalar-single has capacity 1. Deterministic replay uses 4 workers.",
            Limitations = "Authored competing squared-distance fixtures, not real latency/memory measurements or OpenEvolve evidence. Sequential elapsed time is descriptive, not a speedup claim. No tuning or held-out superiority claim.",
            Passed = rows.All(row => row.Status == "completed" && row.ReplayMatched),
            Rows = rows,
            Replays = replays
        }, new JsonSerializerOptions { WriteIndented = true });
        return rows.All(row => row.Status == "completed" && row.ReplayMatched) ? 0 : 1;
    }

    internal static async Task<Row> RunCase(int dimensions, string method, ulong seed, int budget, int workers)
    {
        if (dimensions is not (2 or 3) || method is not ("scalar-single" or "scalar-map64" or "pareto64") || budget < 8)
            throw new ArgumentException("Unknown fixture, method or budget.");
        var random = StableRandom.CreateStream(seed, 991);
        var seeds = Enumerable.Range(0, 8).Select(_ => new Genome(random.NextDouble() * .7, random.NextDouble() * .7)).ToArray();
        string initialHash = EvolutionHash.Combine(seeds.Select(Identity));
        var observer = new Observer(); var definition = Definition(dimensions);
        var watch = Stopwatch.StartNew();
        try
        {
            bool pareto = method == "pareto64";
            var engine = new EvolutionEngine<Genome>(new ObjectiveTask(dimensions), new Mutation(),
                _ => pareto ? new ParetoArchive<Genome>(definition) : new MapElitesArchive<Genome>(
                    new[] { new EvolutionDescriptorDefinition("x", 0, 1, method == "scalar-single" ? 1 : 8),
                        new EvolutionDescriptorDefinition("y", 0, 1, method == "scalar-single" ? 1 : 8) }),
                new EvolutionEngineOptions
                {
                    Seed = seed,
                    RunId = "pareto-development",
                    MaxEvaluationAttempts = budget,
                    MaxProposals = budget * 4,
                    ProposalBatchSize = 4,
                    MaxDegreeOfParallelism = workers,
                    EnableEvaluationCache = false
                },
                selection: pareto ? new ParetoEvolutionSelectionPolicy<Genome>() : new UniformEvolutionSelectionPolicy<Genome>(),
                migration: pareto ? new ParetoEvolutionMigrationPolicy<Genome>() : new RingMigrationPolicy<Genome>(), observer: observer);
            var result = await engine.RunAsync(seeds); watch.Stop();
            var entries = result.Islands.SelectMany(island => island.Entries).ToArray();
            bool passed = entries.Length > 0 && result.Counters.EvaluationAttempts == budget && observer.Samples.Sum(sample => sample.Attempts) == budget &&
                entries.All(entry => definition.Accepts(entry.Evaluation));
            return new(dimensions, method, seed, budget, passed ? "completed" : "failed", passed ? null : "Budget or feasibility contract failed.",
                initialHash, result.StateHash, false, result.Counters.EvaluationAttempts, result.Counters.Proposals,
                observer.Samples.Sum(sample => sample.Cost), watch.Elapsed.TotalMilliseconds,
                new EvolutionParetoFront<Genome>(definition, entries).Hypervolume(), entries.Length,
                observer.Samples.Count(sample => sample.Status == "Completed"),
                entries.Length == 0 ? Array.Empty<double>() : Enumerable.Range(0, dimensions).Select(i => entries.Min(entry => entry.Evaluation.Objectives[i])).ToArray(),
                entries.Length == 0 ? Array.Empty<double>() : Enumerable.Range(0, dimensions).Select(i => entries.Max(entry => entry.Evaluation.Objectives[i])).ToArray(), observer.Samples.ToArray(),
                entries.Select(entry => new Elite(entry.Evaluation.GenomeId, entry.Evaluation.Quality!.Value, entry.Evaluation.Objectives.ToArray())).ToArray());
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(dimensions, method, seed, budget, "failed", exception.ToString(), initialHash, null, false,
                observer.Samples.Sum(sample => sample.Attempts), observer.Samples.Count, observer.Samples.Sum(sample => sample.Cost),
                watch.Elapsed.TotalMilliseconds, 0, 0, 0, Array.Empty<double>(), Array.Empty<double>(), observer.Samples.ToArray(), Array.Empty<Elite>());
        }
    }
    private static string Identity(Genome genome) => EvolutionHash.Combine(new[] { EvolutionHash.EncodeDouble(genome.X), EvolutionHash.EncodeDouble(genome.Y) });
    private static string Digest(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private sealed class ObjectiveTask(int dimensions) : IEvolutionTask<Genome>
    {
        public string Id => "competing-quadratics-" + dimensions;
        public string VersionHash => "competing-quadratics-v1";
        public string EvaluatorVersionHash => "competing-quadratics-eval-v1";
        public ValueTask<EvolutionCanonicalGenome<Genome>> CanonicalizeAsync(Genome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<Genome>(genome, Identity(genome)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<Genome> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            var g = candidate.CanonicalGenome.Genome;
            double[] all = { g.X * g.X + g.Y * g.Y, (1 - g.X) * (1 - g.X) + g.Y * g.Y, g.X * g.X + (1 - g.Y) * (1 - g.Y) };
            double[] objectives = all.Take(dimensions).ToArray(); double violation = Math.Max(0, g.X + g.Y - 1.4);
            return new(new EvolutionTaskResult(violation == 0 ? EvolutionEvaluationStatus.Completed : EvolutionEvaluationStatus.Rejected,
                -objectives.Average(), descriptors: new Dictionary<string, double> { ["x"] = g.X, ["y"] = g.Y },
                objectives: objectives, constraintViolations: new[] { violation }, costUnits: 1));
        }
    }
    private sealed class Mutation : IVariationOperator<Genome>
    {
        public string Id => "bounded-local-mutation";
        public string VersionHash => "bounded-local-mutation-v1";
        public ValueTask<Genome> ProposeAsync(EvolutionVariationContext<Genome> context, CancellationToken cancellationToken = default)
        {
            var g = context.Parent.Candidate.CanonicalGenome.Genome;
            // Reflection at the boundary avoids clipping many proposals to the same endpoint.
            double Reflect(double value) => value < 0 ? -value : value > 1 ? 2 - value : value;
            return new(new Genome(Reflect(g.X + (context.Random.NextDouble() - .5) * .4), Reflect(g.Y + (context.Random.NextDouble() - .5) * .4)));
        }
    }
    private sealed class Observer : IEvolutionObserver<Genome>
    {
        internal List<Sample> Samples { get; } = new();
        public ValueTask OnEventAsync(EvolutionEvent<Genome> evolutionEvent, CancellationToken cancellationToken = default)
        {
            if (evolutionEvent.Kind == EvolutionEventKind.Evaluated && evolutionEvent.Evaluation is EvolutionEvaluation e)
                Samples.Add(new(e.EvaluationId, e.GenomeId, e.Status.ToString(), e.Quality, e.Objectives.ToArray(), e.ConstraintViolations.ToArray(),
                    e.Cost.AttemptCount, e.Cost.CostUnits, evolutionEvent.InsertionResult?.ToString(), e.Diagnostics.Select(d => d.Code + ": " + d.Message).ToArray()));
            return default;
        }
    }
}
