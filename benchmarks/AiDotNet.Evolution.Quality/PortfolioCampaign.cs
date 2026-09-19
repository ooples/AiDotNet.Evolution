using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution.Quality;

internal static class PortfolioCampaign
{
    internal const string Units = "proposal-dispatch-plus-objective-invocation-v1";
    internal static readonly string[] Methods = ["mutation", "crossover", "restart", "refinement", "adaptive-parent", "adaptive-archive", "uniform-portfolio"];
    internal sealed record Request(string Partition, int Budget, ulong[] Seeds);
    internal sealed record Row(string Family, string Task, string Method, ulong Seed, string InitialHash,
        string Status, string? Error, double Quality, int ProposalCalls, int ObjectiveCalls, double Seconds,
        EvolutionResourceSnapshot Resources, EvolutionOperatorCredit[] Credits, PortfolioWorkload.Observation[] Observations, string State,
        double CpuSeconds, double Diversity, Terminal[] Terminals, Elite[] Elites);
    internal sealed record Terminal(long EvaluationId, long Generation, string GenomeId, string Status, double? Quality, int Attempts, double CostUnits, string[] Diagnostics);
    internal sealed record Elite(string GenomeId, double Quality, int[] Cell);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || new FileInfo(args[0]).Length > 65536) throw new ArgumentException("--portfolio-study <request.json> <new-result.json>");
        byte[] bytes = File.ReadAllBytes(args[0]);
        using (var document = JsonDocument.Parse(bytes))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().GroupBy(property => property.Name).Any(group => group.Count() != 1))
                throw new InvalidDataException("Duplicate or invalid request fields.");
        }
        var request = JsonSerializer.Deserialize<Request>(bytes, AblationCampaign.Json) ?? throw new InvalidDataException("Missing request.");
        if (request.Partition is not ("development" or "confirmation") || request.Budget is < 24 or > 256 ||
            request.Seeds is null || request.Seeds.Length is < 2 or > 512 || request.Seeds.Distinct().Count() != request.Seeds.Length)
            throw new InvalidDataException("Invalid portfolio study request.");
        using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var rows = new List<Row>();
        foreach (string family in new[] { "numeric", "program", "kernel" })
            foreach (ulong seed in request.Seeds)
            {
                var random = StableRandom.CreateStream(seed, 654);
                foreach (var method in Methods.Select(name => (Name: name, Key: random.NextDouble())).OrderBy(item => item.Key))
                    rows.Add(await RunCase(family, request.Partition, method.Name, seed, request.Budget));
            }
        await JsonSerializer.SerializeAsync(output, new
        {
            Protocol = "portfolio-study-v2",
            Request = request,
            RequestHash = AblationCampaign.Digest(bytes),
            BenchmarkHash = AblationCampaign.Digest(File.ReadAllBytes(typeof(PortfolioCampaign).Assembly.Location)),
            CoreHash = AblationCampaign.Digest(File.ReadAllBytes(typeof(EvolutionEngineOptions).Assembly.Location)),
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Rows = rows
        }, AblationCampaign.Json);
        return rows.All(row => row.Status == "completed") ? 0 : 1;
    }

    internal static async Task<Row> RunCase(string family, string partition, string method, ulong seed, int budget)
    {
        if (!Methods.Contains(method, StringComparer.Ordinal)) throw new ArgumentException("Unknown method.");
        var workload = new PortfolioWorkload(family, partition, seed);
        var builder = new EvolutionSearchSpaceBuilder();
        foreach (string name in PortfolioWorkload.Names) builder.Add(EvolutionParameter.Real(name, -1, 1));
        var space = builder.Build();
        var random = StableRandom.CreateStream(seed, 123);
        var initial = Enumerable.Range(0, 8).Select(_ => workload.Normalize(space, space.Sample(random))).ToArray();
        string initialHash = EvolutionHash.Combine(initial.Select(genome => genome.Identity));
        var ledger = new EvolutionResourceLedger(workload.Id + "/" + method + "/" + seed, EvolutionResources.Of("cost_units", budget), retainedReceiptLimit: 4096);
        var backends = Methods.Take(4).Select(name => new Backend(name, space, workload)).ToArray();
        var operators = backends.Select(source => new ResourceMeteredVariationOperator<EvolutionSearchGenome>(source, ledger,
            EvolutionResources.Of("cost_units", source.Id == "refinement" ? 3 : 1), Units)).ToArray();
        AdaptiveVariationPortfolio<EvolutionSearchGenome>? portfolio = method.StartsWith("adaptive-", StringComparison.Ordinal) || method == "uniform-portfolio"
            ? EvolutionCostedPortfolio.Create(backends.Select(source => new EvolutionCostedPortfolioArm<EvolutionSearchGenome>(source,
                EvolutionResources.Of("cost_units", source.Id == "refinement" ? 3 : 1))), ledger,
                new EvolutionOperatorRewardPolicy(method == "adaptive-parent" ? EvolutionOperatorRewardKind.ParentImprovement : EvolutionOperatorRewardKind.ArchiveSuccess,
                EvolutionOperatorCostBasis.ProposalAndEvaluation, Units), method == "uniform-portfolio" ? 1 : 0.2) : null;
        IOutcomeAwareVariationOperator<EvolutionSearchGenome> variation = portfolio is not null ? portfolio : operators[Array.IndexOf(Methods, method)];
        var credits = new List<EvolutionOperatorCredit>();
        if (portfolio is not null) portfolio.CreditCommitted += credits.Add;
        var archive = new MapElitesArchive<EvolutionSearchGenome>(PortfolioWorkload.Names.Take(2).Select(name => new EvolutionDescriptorDefinition(name, -1, 1, 8)));
        var task = new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(new EvolutionSearchTask(space, workload.Id, "portfolio-study-v2", "portfolio-study-v2",
            (genome, _, _) => new(EvolutionTaskResult.Completed(workload.Measure(genome), AblationCampaign.Descriptors(genome), costUnits: 1))), ledger, new[] { 1m });
        var clock = Stopwatch.StartNew();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var observer = new Observer();
        string? error = null;
        try
        {
            var engine = new EvolutionEngine<EvolutionSearchGenome>(task, variation, _ => archive, new EvolutionEngineOptions
            {
                RunId = workload.Id + "/" + method + "/" + seed,
                Seed = seed,
                MaxEvaluationAttempts = budget,
                MaxProposals = budget * 2,
                MaxGenerations = budget * 2,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                InspirationCount = 3,
                EnableEvaluationCache = false,
                SelectionPolicy = EvolutionSelectionPolicyKind.Uniform,
                FailurePolicy = EvolutionFailurePolicy.FailFast
            }, observer: observer);
            await engine.RunAsync(initial);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { error = exception.ToString(); }
        clock.Stop();
        var resources = ledger.Snapshot();
        int proposals = backends.Sum(b => b.Calls), objectives = workload.Observations.Count;
        bool valid = error is null && resources.Spent["cost_units"] == proposals + objectives && resources.Spent["cost_units"] <= budget &&
            resources.Unknown == 0 && !resources.MaximumViolated && resources.Reserved.Values.All(v => v == 0) && backends.All(b => b.Calls == b.Outcomes) &&
            observer.Terminals.All(t => t.Status is "Completed" or "Duplicate" ||
                t.Diagnostics.Contains("resource_budget_reached") ||
                (t.Diagnostics.Contains("variation_failure") && resources.Spent["cost_units"] > budget - 3));
        return new(family, workload.Id, method, seed, initialHash, valid ? "completed" : "failed", error,
            valid ? archive.Best?.Evaluation.Quality ?? 0 : 0, proposals, objectives, clock.Elapsed.TotalSeconds,
            resources, credits.ToArray(), workload.Observations.ToArray(), variation.CaptureState(),
            (process.TotalProcessorTime - cpuStart).TotalSeconds, archive.Entries.Count / 64.0,
            observer.Terminals.ToArray(), archive.Entries.Select(e => new Elite(e.Candidate.CanonicalGenome.Genome.Identity,
                e.Evaluation.Quality!.Value, e.Cell.Bins.ToArray())).ToArray());
    }

    private sealed class Observer : IEvolutionObserver<EvolutionSearchGenome>
    {
        internal List<Terminal> Terminals { get; } = new();
        public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> value, CancellationToken cancellationToken = default)
        {
            if (value.Kind == EvolutionEventKind.Evaluated && value.Evaluation is { } e)
                Terminals.Add(new(e.EvaluationId, e.Lineage.Generation, e.GenomeId, e.Status.ToString(), e.Quality,
                    e.Cost.AttemptCount, e.Cost.CostUnits, e.Diagnostics.Select(d => d.Code).ToArray()));
            return default;
        }
    }

    private sealed class Backend(string name, EvolutionSearchSpace space, PortfolioWorkload workload) : ICostedEvolutionProposalSource<EvolutionSearchGenome>
    {
        public string Id => name;
        public string VersionHash => "portfolio-backend-v1-" + workload.Id + "-" + name;
        public int Calls { get; private set; }
        public int Outcomes { get; private set; }
        public ValueTask<EvolutionResourceResult<EvolutionSearchGenome>> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            var parent = context.Parent.Candidate.CanonicalGenome.Genome;
            EvolutionSearchGenome Sample(double radius) => workload.Normalize(space, space.CreateGenome(PortfolioWorkload.Names.Select((key, index) =>
            {
                double value = parent.Number(key);
                if (name == "crossover" && context.Inspirations.Count > 0 && index % 2 == 0)
                    value = context.Inspirations[context.Random.NextInt(context.Inspirations.Count)].Candidate.CanonicalGenome.Genome.Number(key);
                value = name == "restart" ? 2 * context.Random.NextDouble() - 1 : Math.Clamp(value + radius * (2 * context.Random.NextDouble() - 1), -1, 1);
                return new KeyValuePair<string, EvolutionParameterValue>(key, EvolutionParameterValue.Numeric(value));
            })));
            var child = Sample(name == "refinement" ? 0.05 : 0.2);
            if (name == "refinement")
            {
                var alternative = Sample(0.05);
                if (workload.Measure(alternative) > workload.Measure(child)) child = alternative;
            }
            return new(new EvolutionResourceResult<EvolutionSearchGenome>(child, EvolutionResources.Of("cost_units", name == "refinement" ? 3 : 1)));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => Outcomes++;
        public string CaptureState() => Calls.ToString(CultureInfo.InvariantCulture) + ":" + Outcomes.ToString(CultureInfo.InvariantCulture);
        public void RestoreState(string state)
        {
            var parts = state.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int calls) || !int.TryParse(parts[1], out int outcomes) || outcomes < 0 || calls < outcomes)
                throw new InvalidDataException("Invalid backend state.");
            Calls = calls; Outcomes = outcomes;
        }
    }
}
