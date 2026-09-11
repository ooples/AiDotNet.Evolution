using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AiDotNet.Evolution.Quality;

internal static class ArchivePartitionPilot
{
    private const int Dimensions = 12, Capacity = 32, InitialPopulation = 8;

    internal static async Task<int> RunAsync(string[] args, ArchiveCase? single = null)
    {
        if (args.Length != 4 || !int.TryParse(args[0], out int seeds) || seeds is < 1 or > 100 ||
            !int.TryParse(args[1], out int budget) || budget is < InitialPopulation or > 4096 ||
            (long)seeds * budget * 4 > 2_000_000 ||
            !(Regex.IsMatch(args[2], "^[0-9a-f]{40}$") || args[2] == "working-tree-smoke"))
        {
            Console.Error.WriteLine("--archive-partition <seeds 1..100> <budget 8..4096> <full-revision|working-tree-smoke> <new-output.json>");
            return 2;
        }
        using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var builder = new EvolutionSearchSpaceBuilder();
        int dimensions = single?.Dimensions ?? Dimensions;
        string[] names = Enumerable.Range(0, dimensions).Select(i => "x" + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        foreach (string name in names) builder.Add(EvolutionParameter.Real(name, 0, 1));
        EvolutionSearchSpace space = builder.Build();
        var axes = names.Select(name => new EvolutionDescriptorDefinition(name, 0, 1, 2)).ToArray();
        CentroidArchiveDefinition Partition(ulong seed)
        {
            var random = StableRandom.CreateStream(seed, 19);
            return new(axes, Enumerable.Range(0, Capacity).Select(_ => names.Select(_ => random.NextDouble()).ToArray()));
        }
        // The isolated grid case does not allocate unused search sites to hide their memory cost.
        var search = single?.Method == "SparseGrid" ? null : Partition(23117);
        var reference = Partition(91453);
        var rows = new List<ArchiveRun>();
        foreach (string taskName in new[] { "Quadratic" + dimensions, "Rippled" + dimensions })
            foreach (string method in new[] { "SparseGrid", "FixedCentroid" })
                for (ulong seed = 0; seed < (ulong)seeds; seed++)
                    if (single is null || (single.Task == taskName && single.Method == method && single.Seed == seed))
                        rows.Add(await RunCase(taskName, method, seed));

        object? measurement = single?.Probe.Capture();
        if (single?.Probe.Exceeded == true)
            rows = rows.Select(row => row with { Status = "memory-budget-exceeded", Error = "observed_peak_resident_budget", ReferenceUtility = 0 }).ToList();

        await JsonSerializer.SerializeAsync(output, new
        {
            SchemaVersion = single is null ? 1 : 2,
            Protocol = single is null ? "archive-partition-development-v1" : "archive-resource-case-development-v1",
            SourceRevision = args[2],
            Partition = "development",
            Dimensions = dimensions,
            EliteCapacity = Capacity,
            InitialPopulation,
            MaximumGridCells = 10_000_000,
            Seeds = single is null ? seeds : 1,
            SelectedSeed = single?.Seed,
            Budget = budget,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            SearchDefinition = search,
            ReferenceDefinition = reference,
            Measurement = measurement,
            Endpoint = "sum of retained reference-cell utilities 1/(1+loss), divided by 32; empty cells and failed runs have utility zero",
            Limitations = single is null
                ? "Synthetic development only; identical elite-count caps are not equal measured RAM. Uniform frozen Voronoi sites, not fitted CVT. Projection sees only retained elites. No confidence, timing, held-out or superiority claim."
                : "One fresh-process authored case; equal declared observed-peak resident budgets, not equal actual usage or an OS hard limit. Startup/shared pages and instrumentation are included in peak RSS. Uniform frozen Voronoi sites, not fitted CVT. Failed/over-budget cases have zero reference utility. No representative or competitor superiority claim.",
            Runs = rows
        }, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
        Console.WriteLine($"Archive pilot: {rows.Count} runs, {rows.Sum(row => row.EvaluatorCalls)} calls, {rows.Count(row => row.Status != "completed")} incomplete/failed.");
        return rows.All(row => row.Status == "completed") ? 0 : 1;

        async Task<ArchiveRun> RunCase(string taskName, string method, ulong seed)
        {
            var initialRandom = StableRandom.CreateStream(seed, 123);
            var initial = Enumerable.Range(0, InitialPopulation).Select(_ => space.Sample(initialRandom)).ToArray();
            string initialHash = EvolutionHash.Combine(initial.Select(genome => genome.Identity));
            string runId = taskName + "/" + method + "/" + seed;
            var ledger = new EvolutionResourceLedger(runId, new EvolutionResources(new Dictionary<string, decimal>
            { ["cost_units"] = budget, ["proposal_calls"] = budget * 4 }), maximumOperations: budget * 5, retainedReceiptLimit: 4);
            int calls = 0;
            var trace = new Progress(single?.Probe);
            var task = new EvolutionSearchTask(space, taskName, "v1", "v1", (genome, _, token) =>
            {
                token.ThrowIfCancellationRequested(); calls++;
                double loss = names.Select((name, index) =>
                {
                    double x = genome.Number(name) - (index % 2 == 0 ? 0.25 : 0.75);
                    return x * x + (taskName.StartsWith("Rippled", StringComparison.Ordinal) ? 0.05 * (1 - Math.Cos(12 * Math.PI * x)) : 0);
                }).Sum();
                return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-loss,
                    names.ToDictionary(name => name, genome.Number), costUnits: 1));
            });
            IEvolutionArchive<EvolutionSearchGenome>? archive = null;
            try
            {
                archive = method == "SparseGrid"
                    ? new MapElitesArchive<EvolutionSearchGenome>(axes, capacity: Capacity)
                    : new CentroidArchive<EvolutionSearchGenome>(search!);
                var engine = new EvolutionEngine<EvolutionSearchGenome>(
                    new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m }),
                    new MeteredMutation(new SearchSpaceMutation(space), ledger), _ => archive,
                    new EvolutionEngineOptions
                    {
                        RunId = runId,
                        Seed = seed,
                        MaxEvaluationAttempts = budget,
                        MaxProposals = budget * 4,
                        MaxGenerations = budget * 4,
                        ProposalBatchSize = 1,
                        MaxDegreeOfParallelism = 1,
                        IslandCount = 1,
                        MigrationInterval = 0,
                        InspirationCount = 0
                    }, observer: trace);
                if (single is not null) { single.Probe.Stop = engine.RequestStop; single.Probe.Check(); }
                var result = await engine.RunAsync(initial);
                var projection = CentroidArchive<EvolutionSearchGenome>.ProjectWithReport(result.Islands[0], reference);
                var projected = projection.Archive;
                var costs = ledger.Snapshot();
                bool complete = calls == budget && costs.Spent["cost_units"] == budget && costs.Unknown == 0 && !costs.MaximumViolated &&
                    costs.Reserved.Values.All(value => value == 0) && costs.Spent["proposal_calls"] == result.Counters.Proposals - InitialPopulation &&
                    trace.Samples.Count == result.Counters.Proposals && trace.Samples.Sum(sample => sample.Attempts) == calls &&
                    trace.Samples.Sum(sample => sample.CostUnits) == budget &&
                    trace.Samples.All(sample => sample.Status is EvolutionEvaluationStatus.Completed or EvolutionEvaluationStatus.Duplicate);
                double utility = projected.Entries.Sum(entry => 1 / (1 - entry.Evaluation.Quality!.Value)) / Capacity;
                return new(taskName, method, seed, initialHash, complete ? "completed" : "incomplete", complete ? null : result.StopReason.ToString(),
                    calls, result.Counters.Proposals, result.Best?.Evaluation.Quality is double q ? -q : null, archive.Count,
                    projected.Count, complete ? utility : 0, archive.DefinitionHash, result.StateHash, trace.Samples, costs, projection.Report);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                string error = exception.GetType().FullName + ": " + exception.Message;
                return new(taskName, method, seed, initialHash, archive is null ? "configuration-failed" : "failed", error.Substring(0, Math.Min(1024, error.Length)), calls, trace.Samples.Count,
                    null, archive?.Count ?? 0, 0, 0, archive?.DefinitionHash, null, trace.Samples, ledger.Snapshot(), null);
            }
        }
    }

    private sealed record ArchiveRun(string Task, string Method, ulong Seed, string InitialPopulationHash, string Status,
        string? Error, int EvaluatorCalls, long Proposals, double? FinalLoss, int OccupiedSearchCells, int OccupiedReferenceCells,
        double ReferenceUtility, string? ArchiveDefinitionHash, string? StateHash, IReadOnlyList<SampleRecord> Samples, EvolutionResourceSnapshot Resources,
        EvolutionArchiveProjectionReport? Projection);

    private sealed class MeteredMutation(IVariationOperator<EvolutionSearchGenome> inner, EvolutionResourceLedger ledger) : IVariationOperator<EvolutionSearchGenome>
    {
        public string Id => inner.Id;
        public string VersionHash => EvolutionHash.Combine(new[] { "archive-pilot-metered-v1", inner.VersionHash });
        public ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
        {
            var cost = EvolutionResources.Of("proposal_calls", 1);
            return EvolutionResourceWork.RunAsync(ledger, "proposal/" + context.Generation.ToString(CultureInfo.InvariantCulture),
                EvolutionResourceStage.Proposal, cost, cost,
                async token => new EvolutionResourceResult<EvolutionSearchGenome>(await inner.ProposeAsync(context, token), cost), cancellationToken: cancellationToken);
        }
    }

    private sealed class Progress(ArchiveMemoryProbe? probe) : IEvolutionObserver<EvolutionSearchGenome>
    {
        internal List<SampleRecord> Samples { get; } = new();
        private double? _best;
        public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> item, CancellationToken cancellationToken = default)
        {
            probe?.Check();
            if (item.Kind != EvolutionEventKind.Evaluated || item.Evaluation is not { } evaluation) return default;
            if (evaluation.Status == EvolutionEvaluationStatus.Completed && evaluation.Quality.HasValue)
                _best = !_best.HasValue ? -evaluation.Quality.Value : Math.Min(_best.Value, -evaluation.Quality.Value);
            Samples.Add(new SampleRecord(evaluation.EvaluationId, evaluation.Status, _best, evaluation.Cost.AttemptCount,
                evaluation.Cost.CostUnits, evaluation.Diagnostics.Select(d => d.Code).ToArray()));
            return default;
        }
    }
}
