using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Quality;

// This is an experiment runner, not a new set of library defaults.
internal static class AblationCampaign
{
    internal const string Protocol = "feature-ablation-v2";
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    internal sealed record Configuration(string Name, EvolutionSelectionPolicyKind Selection = EvolutionSelectionPolicyKind.Uniform,
        bool Islands = false, bool Migration = false, bool Novelty = false, bool Calibration = false,
        bool Growth = false, bool Continuous = false);
    internal sealed record Request(string Protocol, string Partition, int Budget, ulong[] Seeds, Configuration[] Configurations);
    internal sealed record Row(string Task, string Family, string Configuration, ulong Seed, string InitialHash,
        string Status, string? Error, double Quality, double Diversity, bool Success, long Calls, long Proposals,
        double Seconds, string? StateHash, EvolutionResourceSnapshot Resources, int MigrationEvents, int NoveltyRejections,
        long InspirationUses, string[] InitialDefinitions, string[] FinalDefinitions, AblationWorkload.Observation[] Observations, string? StopReason,
        double CpuSeconds, long? CallsToTarget, double? SecondsToTarget, FinalElite[] FinalElites);
    internal sealed record FinalElite(string Genome, double Quality, int[] Bins);

    internal static Configuration[] Matrix()
    {
        var baseline = new Configuration("baseline");
        var all = new Configuration("all", Islands: true, Migration: true, Novelty: true, Calibration: true, Growth: true, Continuous: true);
        return [baseline, baseline with { Name = "ratio", Selection = EvolutionSelectionPolicyKind.Ratio },
            baseline with { Name = "curiosity", Selection = EvolutionSelectionPolicyKind.Curiosity },
            baseline with { Name = "double", Selection = EvolutionSelectionPolicyKind.Double },
            baseline with { Name = "islands", Islands = true },
            baseline with { Name = "islands-migration", Islands = true, Migration = true },
            baseline with { Name = "novelty", Novelty = true },
            baseline with { Name = "calibration", Calibration = true },
            baseline with { Name = "growth", Growth = true },
            baseline with { Name = "calibration-growth", Calibration = true, Growth = true },
            baseline with { Name = "continuous", Continuous = true },
            baseline with { Name = "islands-continuous", Islands = true, Continuous = true },
            all, all with { Name = "all-no-islands", Islands = false, Migration = false },
            all with { Name = "all-no-migration", Migration = false }, all with { Name = "all-no-novelty", Novelty = false },
            all with { Name = "all-no-calibration", Calibration = false }, all with { Name = "all-no-growth", Growth = false },
            all with { Name = "all-no-continuous", Continuous = false }];
    }

    internal static void Validate(Request request)
    {
        if (request.Protocol != Protocol || request.Partition is not ("development" or "confirmation") ||
            request.Budget is < 16 or > 1024 || request.Seeds is null || request.Seeds.Length is < 2 or > 1024 ||
            request.Seeds.Distinct().Count() != request.Seeds.Length || request.Configurations is null ||
            request.Configurations.Length is < 2 or > 19 || request.Configurations.Any(c => c is null) ||
            request.Configurations.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count() != request.Configurations.Length ||
            request.Configurations.Any(c => !Matrix().Contains(c)) || !request.Configurations.Contains(Matrix()[0]) ||
            (long)request.Budget * request.Seeds.Length * request.Configurations.Length * 3 > 2_000_000)
            throw new InvalidDataException("Invalid ablation request.");
        if (request.Partition == "development" && !request.Configurations.SequenceEqual(Matrix()))
            throw new InvalidDataException("Development requires the entire fixed matrix.");
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("--ablation <registered-request.json> <new-result.json>");
        if (new FileInfo(args[0]).Length > 65536) throw new InvalidDataException("Request too large.");
        byte[] bytes = File.ReadAllBytes(args[0]);
        using var document = JsonDocument.Parse(bytes);
        RejectDuplicateKeys(document.RootElement);
        var request = JsonSerializer.Deserialize<Request>(bytes, Json) ?? throw new InvalidDataException("Missing request.");
        Validate(request);
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<Row>();
        foreach (string family in new[] { "numeric", "program", "kernel" })
            foreach (ulong seed in request.Seeds)
            {
                // Rotate order by a seed-derived stream, never by observed performance.
                var random = StableRandom.CreateStream(seed, 817);
                foreach (var config in request.Configurations.Select(c => (Config: c, Order: random.NextDouble())).OrderBy(c => c.Order))
                {
                    var row = await RunCase(family, request.Partition, config.Config, seed, request.Budget);
                    rows.Add(row);
                    Console.Error.WriteLine($"{family}/{config.Config.Name}/{seed}: {row.Status}, {row.Calls} calls");
                }
            }
        string binary = typeof(AblationCampaign).Assembly.Location;
        await JsonSerializer.SerializeAsync(output, new
        {
            Request = request,
            RequestHash = Digest(bytes),
            BenchmarkHash = Digest(File.ReadAllBytes(binary)),
            CoreHash = Digest(File.ReadAllBytes(typeof(EvolutionEngineOptions).Assembly.Location)),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Processor = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable",
            Rows = rows
        }, Json);
        return rows.All(r => r.Status == "completed") ? 0 : 1;
    }

    internal static async Task<Row> RunCase(string family, string partition, Configuration config, ulong seed, int budget)
    {
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        var workload = new AblationWorkload(family, partition, seed);
        var space = new EvolutionSearchSpaceBuilder();
        foreach (string name in AblationWorkload.Names) space.Add(EvolutionParameter.Real(name, -1, 1));
        var domain = space.Build();
        var random = StableRandom.CreateStream(seed, 123);
        var initial = Enumerable.Range(0, 8).Select(_ => workload.Normalize(domain, domain.Sample(random))).ToArray();
        string initialHash = EvolutionHash.Combine(initial.Select(g => g.Identity));
        var ledger = new EvolutionResourceLedger($"{workload.Id}/{config.Name}/{seed}", new EvolutionResources(
            new Dictionary<string, decimal> { ["cost_units"] = budget, ["proposal_calls"] = budget * 8 }),
            retainedReceiptLimit: 16, maximumOperations: budget * 9);
        int calls = 0;
        var task = new EvolutionSearchTask(domain, workload.Id, Protocol, Protocol, (genome, _, token) =>
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref calls);
            // Actual CPU work runs on the engine's worker threads; no artificial sleep.
            return new(Task.Run(() => EvolutionTaskResult.Completed(workload.Measure(genome), Descriptors(genome), costUnits: 1), token));
        });
        var policy = config.Growth ? EvolutionOutOfRangePolicy.Grow : EvolutionOutOfRangePolicy.Clamp;
        var axes = config.Calibration
            ? EvolutionDescriptorCalibration.FromObservations(initial.Select(g => (IReadOnlyDictionary<string, double>)Descriptors(g)).ToArray(),
                options: new EvolutionDescriptorCalibrationOptions { BinCount = 8, Padding = 0, OutOfRangePolicy = policy })
            : AblationWorkload.Names.Take(2).Select(name => new EvolutionDescriptorDefinition(name, -0.5, 0.5, 8, policy)).ToArray();
        int islandCount = config.Islands ? 4 : 1;
        var archives = Enumerable.Range(0, islandCount).Select(_ => new MapElitesArchive<EvolutionSearchGenome>(axes, capacity: 64 / islandCount)).ToArray();
        var initialDefinitions = archives.Select(a => EvolutionHash.Combine(a.Descriptors.Select(d => d.ToCanonicalString()))).ToArray();
        var variation = new Variation(domain, ledger, workload);
        var observer = new Observer(clock);
        EvolutionRunResult<EvolutionSearchGenome>? result = null;
        string? error = null;
        try
        {
            var engine = new EvolutionEngine<EvolutionSearchGenome>(
                new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m }), variation,
                i => archives[i], new EvolutionEngineOptions
                {
                    RunId = $"{workload.Id}/{config.Name}/{seed}",
                    Seed = seed,
                    MaxEvaluationAttempts = budget,
                    MaxProposals = budget * 8,
                    MaxGenerations = budget * 8,
                    ProposalBatchSize = 4,
                    MaxDegreeOfParallelism = 4,
                    MaxInFlight = 4,
                    IslandCount = islandCount,
                    MigrationInterval = config.Migration ? 4 : 0,
                    MigrantsPerIsland = 1,
                    SelectionPolicy = config.Selection,
                    InspirationCount = 2,
                    NoveltyDistanceThreshold = config.Novelty ? 0.12 : 0,
                    Dispatch = config.Continuous ? EvolutionDispatchMode.Continuous : EvolutionDispatchMode.Batch,
                    EnableEvaluationCache = false
                }, observer: observer, genomeDistance: new GenomeDistance());
            result = await engine.RunAsync(initial);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { error = exception.ToString(); }
        var resources = ledger.Snapshot();
        bool valid = error is null && calls <= budget && resources.Spent["cost_units"] == calls &&
            resources.Spent["proposal_calls"] == variation.Calls && resources.Unknown == 0 && !resources.MaximumViolated &&
            resources.Reserved.Values.All(v => v == 0) && observer.Attempts == calls && workload.Observations.Count == calls && !observer.Failed &&
            result is not null && observer.Proposals == result.Counters.Proposals;
        // Proposal exhaustion is an algorithm outcome, not grounds to drop a run or invent unused calls.
        var reference = new MapElitesArchive<EvolutionSearchGenome>(AblationWorkload.Names.Take(2)
            .Select(name => new EvolutionDescriptorDefinition(name, -1, 1, 8)));
        foreach (var elite in archives.SelectMany(a => a.Entries)) reference.TryAdd(elite.Candidate, elite.Evaluation);
        double quality = valid ? reference.Best?.Evaluation.Quality ?? 0 : 0;
        clock.Stop();
        return new(workload.Id, family, config.Name, seed, initialHash, valid ? "completed" : "failed",
            error ?? (valid ? null : "Resource or evaluation invariant failed"), quality, valid ? reference.Count / 64d : 0,
            valid && quality >= 0.8, calls, result?.Counters.Proposals ?? observer.Proposals, clock.Elapsed.TotalSeconds,
            result?.StateHash, resources, observer.Migrations, observer.Rejections, variation.InspirationUses,
            initialDefinitions, archives.Select(a => EvolutionHash.Combine(a.Descriptors.Select(d => d.ToCanonicalString()))).ToArray(), workload.Observations.ToArray(), result?.StopReason.ToString(),
            (process.TotalProcessorTime - cpuStart).TotalSeconds, valid ? observer.CallsToTarget : null,
            valid ? observer.SecondsToTarget : null, valid ? reference.Entries.Select(e =>
                new FinalElite(e.Candidate.CanonicalGenome.Genome.Identity, e.Evaluation.Quality!.Value, e.Cell.Bins.ToArray())).ToArray() : []);
    }

    internal static Dictionary<string, double> Descriptors(EvolutionSearchGenome genome) =>
        AblationWorkload.Names.Take(2).ToDictionary(name => name, genome.Number);
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count())
                throw new InvalidDataException("Duplicate JSON key.");
            foreach (var property in value.EnumerateObject()) RejectDuplicateKeys(property.Value);
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private sealed class Variation(EvolutionSearchSpace space, EvolutionResourceLedger ledger, AblationWorkload workload) : IVariationOperator<EvolutionSearchGenome>
    {
        public string Id => "ablation-inspiration-mutation";
        public string VersionHash => Protocol;
        public long Calls { get; private set; }
        public long InspirationUses { get; private set; }
        public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cost = EvolutionResources.Of("proposal_calls", 1);
            using var reservation = ledger.TryReserve("proposal/" + (Calls++).ToString(CultureInfo.InvariantCulture), EvolutionResourceStage.Proposal, cost, cost)
                ?? throw new EvolutionResourceBudgetException("proposal");
            var parent = context.Parent.Candidate.CanonicalGenome.Genome;
            var inspiration = context.Inspirations.FirstOrDefault()?.Candidate.CanonicalGenome.Genome;
            if (inspiration is not null) InspirationUses++;
            bool restart = context.Random.NextDouble() < 0.15;
            var genome = space.CreateGenome(AblationWorkload.Names.Select(name =>
                new KeyValuePair<string, EvolutionParameterValue>(name, EvolutionParameterValue.Numeric(restart
                    ? 2 * context.Random.NextDouble() - 1
                    : Math.Clamp(0.75 * parent.Number(name) + 0.25 * (inspiration?.Number(name) ?? parent.Number(name)) +
                        0.2 * (2 * context.Random.NextDouble() - 1), -1, 1)))));
            reservation.Complete(cost);
            return new(workload.Normalize(space, genome));
        }
    }
    private sealed class GenomeDistance : IGenomeDistance<EvolutionSearchGenome>
    {
        public string Id => "ablation-rms-distance";
        public string VersionHash => Protocol;
        public double Distance(EvolutionSearchGenome first, EvolutionSearchGenome second) =>
            Math.Sqrt(AblationWorkload.Names.Average(name => Math.Pow(first.Number(name) - second.Number(name), 2)));
    }
    private sealed class Observer(Stopwatch clock) : IEvolutionObserver<EvolutionSearchGenome>
    {
        public long? CallsToTarget { get; private set; }
        public double? SecondsToTarget { get; private set; }
        public long Proposals { get; private set; }
        public long Attempts { get; private set; }
        public int Migrations { get; private set; }
        public int Rejections { get; private set; }
        public bool Failed { get; private set; }
        public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> value, CancellationToken cancellationToken = default)
        {
            if (value.Kind == EvolutionEventKind.Migrated) Migrations++;
            if (value.Kind == EvolutionEventKind.Evaluated && value.Evaluation is { } e)
            {
                Proposals++; Attempts += e.Cost.AttemptCount;
                if (CallsToTarget is null && e.Status == EvolutionEvaluationStatus.Completed && e.Quality >= 0.8)
                {
                    CallsToTarget = Attempts;
                    SecondsToTarget = clock.Elapsed.TotalSeconds;
                }
                if (e.Diagnostics.Any(d => d.Code == "not_novel")) Rejections++;
                Failed |= e.Status is not (EvolutionEvaluationStatus.Completed or EvolutionEvaluationStatus.Duplicate or EvolutionEvaluationStatus.Rejected);
            }
            return default;
        }
    }
}
