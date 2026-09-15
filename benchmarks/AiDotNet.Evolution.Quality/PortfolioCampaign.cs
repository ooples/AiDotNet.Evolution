using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution.Quality;

internal static class PortfolioCampaign
{
    internal const string Units = "proposal-dispatch-plus-objective-invocation-v1";
    internal static readonly string[] Methods = ["mutation", "crossover", "restart", "refinement", "adaptive-parent", "adaptive-archive"];
    internal sealed record Request(string Partition, int Budget, ulong[] Seeds);
    internal sealed record Row(string Family, string Task, string Method, ulong Seed, string InitialHash,
        string Status, string? Error, double Quality, int ProposalCalls, int ObjectiveCalls, double Seconds,
        EvolutionResourceSnapshot Resources, EvolutionOperatorCredit[] Credits, AblationWorkload.Observation[] Observations, string State);

    internal static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || new FileInfo(args[0]).Length > 65536) throw new ArgumentException("--portfolio-study <request.json> <new-result.json>");
        byte[] bytes = File.ReadAllBytes(args[0]);
        var request = JsonSerializer.Deserialize<Request>(bytes, AblationCampaign.Json) ?? throw new InvalidDataException("Missing request.");
        if (request.Partition is not ("development" or "confirmation") || request.Budget is < 24 or > 256 ||
            request.Seeds is null || request.Seeds.Length is < 2 or > 100 || request.Seeds.Distinct().Count() != request.Seeds.Length)
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
            Protocol = "portfolio-study-v1",
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
        var workload = new AblationWorkload(family, partition, portfolio: true);
        var builder = new EvolutionSearchSpaceBuilder();
        foreach (string name in AblationWorkload.Names) builder.Add(EvolutionParameter.Real(name, -1, 1));
        var space = builder.Build();
        var random = StableRandom.CreateStream(seed, 123);
        var initial = Enumerable.Range(0, 8).Select(_ => workload.Normalize(space, space.Sample(random))).ToArray();
        string initialHash = EvolutionHash.Combine(initial.Select(genome => genome.Identity));
        var ledger = new EvolutionResourceLedger(workload.Id + "/" + method + "/" + seed, EvolutionResources.Of("cost_units", budget), retainedReceiptLimit: 4096);
        var backends = Methods.Take(4).Select(name => new Backend(name, space, workload)).ToArray();
        var operators = backends.Select(source => new ResourceMeteredVariationOperator<EvolutionSearchGenome>(source, ledger,
            EvolutionResources.Of("cost_units", source.Id == "refinement" ? 3 : 1), Units)).ToArray();
        AdaptiveVariationPortfolio<EvolutionSearchGenome>? portfolio = method.StartsWith("adaptive-", StringComparison.Ordinal)
            ? new(operators, new EvolutionOperatorRewardPolicy(method == "adaptive-parent" ? EvolutionOperatorRewardKind.ParentImprovement : EvolutionOperatorRewardKind.ArchiveSuccess,
                EvolutionOperatorCostBasis.ProposalAndEvaluation, Units), 0.2) : null;
        IOutcomeAwareVariationOperator<EvolutionSearchGenome> variation = portfolio is not null ? portfolio : operators[Array.IndexOf(Methods, method)];
        var credits = new List<EvolutionOperatorCredit>();
        if (portfolio is not null) portfolio.CreditCommitted += credits.Add;
        var archive = new MapElitesArchive<EvolutionSearchGenome>(AblationWorkload.Names.Take(2).Select(name => new EvolutionDescriptorDefinition(name, -1, 1, 8)));
        var task = new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(new EvolutionSearchTask(space, workload.Id, "portfolio-study-v1", "portfolio-study-v1",
            (genome, _, _) => new(EvolutionTaskResult.Completed(workload.Measure(genome), AblationCampaign.Descriptors(genome), costUnits: 1))), ledger, new[] { 1m });
        long next = 0;
        var clock = Stopwatch.StartNew();
        string? error = null;
        EvolutionEvaluation Evaluation(long id, long generation, EvolutionSearchGenome genome, EvolutionTaskResult measured, int attempts) =>
            new(id, genome.Identity, measured.Status, measured.Quality, measured.Direction, measured.Descriptors, measured.Objectives, measured.ConstraintViolations,
                new EvolutionEvaluationCost(TimeSpan.Zero, attempts, measured.CostUnits), new EvolutionLineage(null, null, variation.Id, null, generation, 0, (ulong)id),
                EvolutionCacheStatus.Miss, measured.Diagnostics, task.VersionHash, task.EvaluatorVersionHash, variation.VersionHash);
        async Task<(EvolutionEvaluation Evaluation, EvolutionArchiveInsertionResult? Insertion)> Measure(EvolutionSearchGenome genome, long generation)
        {
            long id = next++;
            var lineage = new EvolutionLineage(null, null, variation.Id, null, generation, 0, (ulong)id);
            var candidate = new EvolutionCandidate<EvolutionSearchGenome>(id, new(genome, genome.Identity), lineage);
            int before = workload.Observations.Count;
            var result = await task.EvaluateAsync(candidate, new(id, seed, (ulong)id, 1));
            var evaluation = Evaluation(id, generation, genome, result, workload.Observations.Count - before);
            return (evaluation, result.Status == EvolutionEvaluationStatus.Completed ? archive.TryAdd(candidate, evaluation) : null);
        }
        try
        {
            foreach (var genome in initial) await Measure(genome, 0);
            for (long generation = 1; generation <= budget * 2; generation++)
            {
                var parent = archive.Best ?? throw new InvalidOperationException("No feasible seed.");
                var context = new EvolutionVariationContext<EvolutionSearchGenome>(parent, archive.Entries.Take(3).ToArray(),
                    StableRandom.CreateStream(seed, (ulong)(1000 + generation)), generation, 0, archive: archive);
                EvolutionSearchGenome proposed;
                try { proposed = await variation.ProposeAsync(context); }
                catch (EvolutionResourceBudgetException)
                {
                    variation.Observe(Evaluation(next++, generation, parent.Candidate.CanonicalGenome.Genome,
                        EvolutionTaskResult.Failed("proposal_budget_denied", "No proposal dispatched."), 0), null);
                    break;
                }
                var measured = await Measure(proposed, generation);
                variation.Observe(measured.Evaluation, measured.Insertion);
                if (measured.Evaluation.Status != EvolutionEvaluationStatus.Completed) break;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { error = exception.ToString(); }
        clock.Stop();
        var resources = ledger.Snapshot();
        int proposals = backends.Sum(b => b.Calls), objectives = workload.Observations.Count;
        bool valid = error is null && resources.Spent["cost_units"] == proposals + objectives && resources.Spent["cost_units"] <= budget &&
            resources.Unknown == 0 && !resources.MaximumViolated && resources.Reserved.Values.All(v => v == 0) && backends.All(b => b.Calls == b.Outcomes);
        return new(family, workload.Id, method, seed, initialHash, valid ? "completed" : "failed", error,
            valid ? archive.Best?.Evaluation.Quality ?? 0 : 0, proposals, objectives, clock.Elapsed.TotalSeconds,
            resources, credits.ToArray(), workload.Observations.ToArray(), variation.CaptureState());
    }

    private sealed class Backend(string name, EvolutionSearchSpace space, AblationWorkload workload) : ICostedEvolutionProposalSource<EvolutionSearchGenome>
    {
        public string Id => name;
        public string VersionHash => "portfolio-backend-v1-" + workload.Id + "-" + name;
        public int Calls { get; private set; }
        public int Outcomes { get; private set; }
        public ValueTask<EvolutionResourceResult<EvolutionSearchGenome>> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            var parent = context.Parent.Candidate.CanonicalGenome.Genome;
            EvolutionSearchGenome Sample(double radius) => workload.Normalize(space, space.CreateGenome(AblationWorkload.Names.Select((key, index) =>
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
