using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

int seeds = 2, budget = 64;
if (args.Length != 0 && (args.Length is < 2 or > 3 || !int.TryParse(args[0], out seeds) || !int.TryParse(args[1], out budget)) || seeds is < 1 or > 20 || budget is < 16 or > 256)
{
    Console.Error.WriteLine("Usage: OperatorCreditSearch [seed-count 1..20 cost-cap 16..256 [new-output.json]]"); return 2;
}
using var output = args.Length == 3 ? new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
var builder = new EvolutionSearchSpaceBuilder();
for (int i = 0; i < 4; i++) builder.Add(EvolutionParameter.Real("x" + i, 0, 1));
var space = builder.Build(); var runs = new List<object>(); bool valid = true;
foreach (string taskName in new[] { "ShiftedQuadratic4", "RippledQuadratic4" })
    foreach (string method in new[] { "StaticSmall", "StaticLarge", "ArchiveEvaluation", "ArchiveTotal", "ParentEvaluation", "ParentTotal" })
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
        {
            var ledger = new EvolutionResourceLedger($"{taskName}-{method}-{seed}", Cost(budget), retainedReceiptLimit: 4096);
            var task = new TaskBackend(taskName, space);
            var meteredTask = new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m });
            var small = new ProposalBackend(space, "small", 0.05, 0.1m); var large = new ProposalBackend(space, "large", 0.25, 1m);
            var smallOperator = new ResourceMeteredVariationOperator<EvolutionSearchGenome>(small, ledger, Cost(0.1m), ProposalBackend.Units);
            var largeOperator = new ResourceMeteredVariationOperator<EvolutionSearchGenome>(large, ledger, Cost(1), ProposalBackend.Units);
            AdaptiveVariationPortfolio<EvolutionSearchGenome>? portfolio = method.StartsWith("Static", StringComparison.Ordinal) ? null :
                new(new[] { smallOperator, largeOperator }, new EvolutionOperatorRewardPolicy(
                    method.StartsWith("Parent", StringComparison.Ordinal) ? EvolutionOperatorRewardKind.ParentImprovement : EvolutionOperatorRewardKind.ArchiveSuccess,
                    method.EndsWith("Total", StringComparison.Ordinal) ? EvolutionOperatorCostBasis.ProposalAndEvaluation : EvolutionOperatorCostBasis.Evaluation,
                    ProposalBackend.Units, qualityScale: 0.25), 0.2);
            IOutcomeAwareVariationOperator<EvolutionSearchGenome> variation = portfolio is not null ? portfolio : method == "StaticSmall" ? smallOperator : largeOperator;
            var archive = new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("all", 0, 1, 1) });
            var measurements = new List<object>(); var proposals = new List<object>();
            var initial = await EvolutionResourceWork.RunAsync(ledger, "initial-population", EvolutionResourceStage.Setup, Cost(0.08m), Cost(0.08m), _ =>
            {
                var random = StableRandom.CreateStream(seed, 123);
                return new ValueTask<EvolutionResourceResult<EvolutionSearchGenome[]>>(new EvolutionResourceResult<EvolutionSearchGenome[]>(Enumerable.Range(0, 8).Select(_ => space.Sample(random)).ToArray(), Cost(0.08m)));
            });
            long nextId = 0;
            EvolutionEvaluation Attach(long id, string genomeId, EvolutionLineage lineage, EvolutionTaskResult outcome, int attempts) =>
                new(id, genomeId, outcome.Status, outcome.Quality, outcome.Direction, outcome.Descriptors, outcome.Objectives, outcome.ConstraintViolations,
                    new EvolutionEvaluationCost(TimeSpan.Zero, attempts, outcome.CostUnits), lineage, EvolutionCacheStatus.NotChecked, outcome.Diagnostics,
                    meteredTask.VersionHash, meteredTask.EvaluatorVersionHash, variation.VersionHash);
            async ValueTask<(EvolutionEvaluation Evaluation, EvolutionArchiveInsertionResult? Insertion)> Measure(EvolutionSearchGenome genome, long generation)
            {
                long id = nextId++;
                var lineage = new EvolutionLineage(null, null, generation == 0 ? "seed" : variation.Id, null, generation, 0, (ulong)id);
                var candidate = new EvolutionCandidate<EvolutionSearchGenome>(id, new(genome, genome.Identity), lineage);
                int before = task.Calls;
                var measured = await meteredTask.EvaluateAsync(candidate, new EvolutionEvaluationContext(id, seed, (ulong)(10000 + id), 1));
                var evaluation = Attach(id, genome.Identity, lineage, measured, task.Calls - before);
                EvolutionArchiveInsertionResult? insertion = measured.Status == EvolutionEvaluationStatus.Completed ? archive.TryAdd(candidate, evaluation) : null;
                if (task.Calls > before) measurements.Add(new
                {
                    Id = id,
                    genome.Identity,
                    Generation = generation,
                    Loss = -measured.Quality!.Value,
                    Cost = measured.CostUnits,
                    BestLoss = -archive.Best!.Evaluation.Quality!.Value
                });
                return (evaluation, insertion);
            }
            foreach (var genome in initial) await Measure(genome, 0);
            string stop = "proposal-cap";
            for (int generation = 1; generation <= budget * 2; generation++)
            {
                var context = new EvolutionVariationContext<EvolutionSearchGenome>(archive.Best!, Array.Empty<EvolutionArchiveEntry<EvolutionSearchGenome>>(),
                    StableRandom.CreateStream(seed, (ulong)(1000 + generation)), generation, 0, archive: archive);
                EvolutionSearchGenome proposed;
                try { proposed = await variation.ProposeAsync(context); }
                catch (EvolutionResourceBudgetException)
                {
                    var failed = EvolutionTaskResult.Failed("proposal_budget_denied", "No proposal was dispatched.");
                    var lineage = new EvolutionLineage(null, null, variation.Id, null, generation, 0, (ulong)nextId);
                    variation.Observe(Attach(nextId++, "undispatched-" + generation, lineage, failed, 0), null);
                    proposals.Add(new { Generation = generation, Status = "proposal-denied", Evaluated = false, Credit = portfolio?.LastCredit }); stop = "first-reservation-denial"; break;
                }
                var measured = await Measure(proposed, generation); variation.Observe(measured.Evaluation, measured.Insertion);
                proposals.Add(new
                {
                    Generation = generation,
                    Status = measured.Evaluation.Status.ToString(),
                    Evaluated = measured.Evaluation.Cost.AttemptCount > 0,
                    Proposal = proposed.Identity,
                    ParentQuality = context.Parent.Evaluation.Quality,
                    Credit = portfolio?.LastCredit
                });
                if (measured.Evaluation.Status != EvolutionEvaluationStatus.Completed) { stop = "first-reservation-denial"; break; }
            }
            var resources = ledger.Snapshot();
            bool success = stop == "first-reservation-denial" && resources.Spent["cost_units"] <= budget && resources.Unknown == 0 && !resources.MaximumViolated &&
                resources.Reserved.Values.All(amount => amount == 0) && task.Calls == measurements.Count && task.Calls >= 8 &&
                resources.Receipts.Where(receipt => receipt.Stage == EvolutionResourceStage.Evaluation).Sum(receipt => receipt.Charged["cost_units"]) == task.Calls &&
                resources.Receipts.Where(receipt => receipt.Stage == EvolutionResourceStage.Proposal).Sum(receipt => receipt.Charged["cost_units"]) == small.Calls * 0.1m + large.Calls &&
                small.Outcomes == small.Calls && large.Outcomes == large.Calls;
            valid &= success;
            runs.Add(new
            {
                Task = taskName,
                Method = method,
                Seed = seed,
                Status = success ? "completed" : "failed",
                StopReason = stop,
                InitialPopulationHash = EvolutionHash.Combine(initial.Select(genome => genome.Identity)),
                EvaluatorCalls = task.Calls,
                SmallProposals = small.Calls,
                LargeProposals = large.Calls,
                FinalLoss = -archive.Best!.Evaluation.Quality!.Value,
                Statistics = portfolio?.Statistics,
                Resources = resources,
                Measurements = measurements,
                Proposals = proposals,
                OperatorStateHash = EvolutionHash.Combine(new[] { variation.CaptureState() })
            });
        }
string json = JsonSerializer.Serialize(new
{
    Protocol = "synthetic-operator-credit-example-v1",
    Seeds = seeds,
    CostCap = budget,
    AssemblyVersion = typeof(ProposalBackend).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
    AssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(ProposalBackend).Assembly.Location))).ToLowerInvariant(),
    Interpretation = "Development example with predeclared synthetic prices, not measured CPU or real model costs. All methods stop at their first reservation denial, so unused budget can differ. No tuning, uncertainty correction, held-out confirmation or default-promotion claim.",
    Runs = runs
}, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
if (output is null) Console.WriteLine(json); else { using var writer = new StreamWriter(output); await writer.WriteLineAsync(json); }
return valid ? 0 : 1;
static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);

sealed class ProposalBackend(EvolutionSearchSpace space, string name, double scale, decimal cost) : ICostedEvolutionProposalSource<EvolutionSearchGenome>
{
    public const string Units = "synthetic-proposal-evaluation-work-v1";
    public string Id => name;
    public string VersionHash => EvolutionHash.Combine(new[] { "credit-example-proposal-v1", name, scale.ToString("R", CultureInfo.InvariantCulture), cost.ToString(CultureInfo.InvariantCulture), space.VersionHash });
    public int Calls { get; private set; }
    public int Outcomes { get; private set; }
    public ValueTask<EvolutionResourceResult<EvolutionSearchGenome>> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        var genome = space.CreateGenome(space.Parameters.Select(parameter => new KeyValuePair<string, EvolutionParameterValue>(parameter.Name,
            EvolutionParameterValue.Numeric(Math.Clamp(context.Parent.Candidate.CanonicalGenome.Genome.Number(parameter.Name) + scale * (2 * context.Random.NextDouble() - 1), 0, 1)))));
        return new(new EvolutionResourceResult<EvolutionSearchGenome>(genome, EvolutionResources.Of("cost_units", cost)));
    }
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => Outcomes++;
    public string CaptureState() => Calls.ToString(CultureInfo.InvariantCulture) + ":" + Outcomes.ToString(CultureInfo.InvariantCulture);
    public void RestoreState(string state)
    {
        var fields = state.Split(':');
        if (fields.Length != 2 || !int.TryParse(fields[0], out int calls) || !int.TryParse(fields[1], out int outcomes) || outcomes < 0 || calls < outcomes)
            throw new InvalidDataException("Invalid proposal counters.");
        Calls = calls; Outcomes = outcomes;
    }
}

sealed class TaskBackend(string name, EvolutionSearchSpace space) : IEvolutionTask<EvolutionSearchGenome>
{
    public string Id => name;
    public string VersionHash => EvolutionHash.Combine(new[] { "credit-example-task-v1", name, space.VersionHash });
    public string EvaluatorVersionHash => VersionHash;
    public int Calls { get; private set; }
    public ValueTask<EvolutionCanonicalGenome<EvolutionSearchGenome>> CanonicalizeAsync(EvolutionSearchGenome genome, CancellationToken cancellationToken = default) => new(new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, genome.Identity));
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<EvolutionSearchGenome> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++;
        double loss = space.Parameters.Select((parameter, index) =>
        {
            double delta = candidate.CanonicalGenome.Genome.Number(parameter.Name) - new[] { 0.2, 0.6, 0.4, 0.8 }[index];
            return delta * delta + (name == "RippledQuadratic4" ? 0.1 * (1 - Math.Cos(10 * Math.PI * delta)) : 0);
        }).Sum();
        return new(EvolutionTaskResult.Completed(-loss, new Dictionary<string, double> { ["all"] = 0 }, costUnits: 1));
    }
}
