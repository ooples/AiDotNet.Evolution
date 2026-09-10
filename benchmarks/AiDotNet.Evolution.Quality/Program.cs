using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;

namespace AiDotNet.Evolution.Quality;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--archive-partition") return await ArchivePartitionPilot.RunAsync(args.Skip(1).ToArray());
        int methodCount = Enum.GetValues<QualityMethod>().Length;
        int taskCount = Enum.GetValues<QualityTask>().Length;
        if (args.Length != 4 || !int.TryParse(args[0], out int seeds) || seeds is < 1 or > 1000 ||
            !int.TryParse(args[1], out int budget) || budget is < 8 or > 1_000_000 ||
            (long)seeds * budget * taskCount * methodCount > 2_000_000 ||
            string.IsNullOrWhiteSpace(args[2]) || string.IsNullOrWhiteSpace(args[3]))
        {
            Console.Error.WriteLine("Usage: <seed-count 1..1000> <evaluation-budget 8..1000000> <source-revision> <new-output.json>; at most 2,000,000 total evaluations.");
            return 2;
        }
        // Refuse overwrites before any experiment begins.
        using var output = new FileStream(args[3], FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var records = new List<RunRecord>();
        foreach (QualityTask task in Enum.GetValues<QualityTask>())
            foreach (QualityMethod method in Enum.GetValues<QualityMethod>())
                for (ulong seed = 0; seed < (ulong)seeds; seed++)
                {
                    RunRecord record = await QualityExperiment.RunAsync(task, method, seed, budget);
                    records.Add(record);
                    Console.Error.WriteLine($"{task}/{method}/{seed}: {record.Status}, calls={record.EvaluatorCalls}, loss={record.FinalLoss:R}");
                }
        var report = new
        {
            SchemaVersion = 2,
            Protocol = "numeric-development-v3-diagonal-cma",
            Partition = "development",
            SourceRevision = args[2],
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            Seeds = seeds,
            Budget = budget,
            TaskCount = taskCount,
            Methods = Enum.GetNames<QualityMethod>(),
            Dimensions = QualityExperiment.Dimensions,
            InitialPopulation = QualityExperiment.InitialPopulation,
            Comparability = "Same initial genomes and evaluator-call cap; proposals also capped. No model calls, cascade, retries, hidden refinement or persisted warm starts.",
            Limitations = "Synthetic development fixtures only. Not an OpenEvolve comparison, runtime speedup, confidence interval or held-out superiority claim.",
            Runs = records
        };
        await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        return records.All(record => record.Status == "completed") ? 0 : 1;
    }
}

internal enum QualityTask { Sphere, ShiftedQuadratic, AnisotropicQuadratic, RippledQuadratic }
internal enum QualityMethod { RandomSearch, HillClimb, FixedMapElites, AdaptiveMapElites, UniformPortfolioMapElites, DiagonalCma }

internal sealed record RunRecord(QualityTask Task, QualityMethod Method, ulong Seed, string InitialPopulationHash,
    string Status, string? Error, long EvaluatorCalls, long Proposals, double? FinalLoss, double? MeanBestLoss,
    int OccupiedCells, string? StateHash, IReadOnlyList<SampleRecord> Samples,
    IReadOnlyList<EvolutionOperatorStatistics> Operators, EvolutionResourceSnapshot Resources, CmaState? Cma = null);
internal sealed record CmaState(long Updates, long StalePopulations, long InvalidPopulations, int PendingCount, double StepSize, IReadOnlyList<double> Variances);
internal sealed record SampleRecord(long EvaluationId, EvolutionEvaluationStatus Status, double? BestLoss,
    int Attempts, double CostUnits, IReadOnlyList<string> DiagnosticCodes);

internal static class QualityExperiment
{
    internal const int Dimensions = 8;
    internal const int InitialPopulation = 8;

    internal static async Task<RunRecord> RunAsync(QualityTask taskKind, QualityMethod method, ulong seed, int budget)
    {
        var ledger = new EvolutionResourceLedger($"{taskKind}-{method}-{seed}", new EvolutionResources(
            new Dictionary<string, decimal> { ["cost_units"] = budget, ["proposal_calls"] = budget * 4 }),
            retainedReceiptLimit: 64, maximumOperations: Math.Min(1_000_000, budget * 5));
        var initialRandom = StableRandom.CreateStream(seed, 123);
        NumericGenome[] seeds = Enumerable.Range(0, InitialPopulation).Select(_ => RandomGenome(initialRandom)).ToArray();
        string initialHash = EvolutionHash.Combine(seeds.Select(genome => genome.Identity));
        var task = new NumericTask(taskKind);
        var observer = new Progress();
        IVariationOperator<NumericGenome> variation = method switch
        {
            QualityMethod.RandomSearch => new NumericVariation(ledger, 0, restart: true),
            QualityMethod.DiagonalCma => new CmaNumericVariation(ledger),
            QualityMethod.AdaptiveMapElites or QualityMethod.UniformPortfolioMapElites => new AdaptiveVariationPortfolio<NumericGenome>(new IVariationOperator<NumericGenome>[]
            {
                new NumericVariation(ledger, 0.1), new NumericVariation(ledger, 1), new NumericVariation(ledger, 0, restart: true)
            }, method == QualityMethod.UniformPortfolioMapElites ? 1 : 0.1),
            _ => new NumericVariation(ledger, 0.1)
        };
        var options = new EvolutionEngineOptions
        {
            RunId = $"{taskKind}-{method}-{seed}",
            Seed = seed,
            MaxEvaluationAttempts = budget,
            MaxProposals = budget * 4,
            MaxGenerations = budget * 4,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            IslandCount = 1,
            MigrationInterval = 0,
            InspirationCount = 0,
            EnableEvaluationCache = true
        };
        try
        {
            var meteredTask = new ResourceMeteredEvolutionTask<NumericGenome>(task, ledger, new[] { 1m });
            var engine = new EvolutionEngine<NumericGenome>(meteredTask, variation,
                _ => new MapElitesArchive<NumericGenome>(new[]
                {
                    new EvolutionDescriptorDefinition("coordinate-0", -5, 5, 10),
                    new EvolutionDescriptorDefinition("coordinate-1", -5, 5, 10)
                }), options, selection: method == QualityMethod.HillClimb ? new BestSelection() : null, observer: observer);
            EvolutionRunResult<NumericGenome> result = await engine.RunAsync(seeds);
            EvolutionResourceSnapshot resources = ledger.Snapshot();
            bool complete = task.Calls == budget && observer.Samples.Sum(sample => sample.Attempts) == budget &&
                observer.Samples.Sum(sample => sample.CostUnits) == budget && observer.Samples.Count == result.Counters.Proposals &&
                resources.Spent["cost_units"] == budget && resources.Spent["proposal_calls"] == result.Counters.Proposals - InitialPopulation &&
                resources.Reserved.Values.All(amount => amount == 0) && resources.Unknown == 0 && !resources.MaximumViolated &&
                observer.Samples.All(sample => sample.Status is EvolutionEvaluationStatus.Completed or EvolutionEvaluationStatus.Duplicate);
            double[] curve = observer.Samples.Where(sample => sample.Attempts > 0 && sample.BestLoss.HasValue)
                .Select(sample => sample.BestLoss!.Value).ToArray();
            return new RunRecord(taskKind, method, seed, initialHash, complete ? "completed" : "incomplete",
                complete ? null : result.StopReason.ToString(), task.Calls, result.Counters.Proposals,
                observer.BestLoss, curve.Length == 0 ? null : curve.Average(), result.Islands.Sum(island => island.Count),
                result.StateHash, observer.Samples, Statistics(variation), resources, CmaStatistics(variation));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Failed seeds remain in the denominator and output; never silently drop them from comparisons.
            return new RunRecord(taskKind, method, seed, initialHash, "failed", exception.GetType().FullName,
                task.Calls, observer.Samples.Count, observer.BestLoss, null, 0, null, observer.Samples, Statistics(variation), ledger.Snapshot(), CmaStatistics(variation));
        }
    }

    private static IReadOnlyList<EvolutionOperatorStatistics> Statistics(IVariationOperator<NumericGenome> variation) =>
        (variation as AdaptiveVariationPortfolio<NumericGenome>)?.Statistics ?? Array.Empty<EvolutionOperatorStatistics>();

    private static CmaState? CmaStatistics(IVariationOperator<NumericGenome> variation) => variation is CmaNumericVariation cma
        ? new(cma.Emitter.Updates, cma.Emitter.StalePopulations, cma.Emitter.InvalidPopulations, cma.Emitter.PendingCount, cma.Emitter.StepSize, cma.Emitter.Variances)
        : null;

    private static NumericGenome RandomGenome(StableRandom random) =>
        new(Enumerable.Range(0, Dimensions).Select(_ => -5 + 10 * random.NextDouble()));

    private sealed class NumericGenome : IImmutableEvolutionGenome<NumericGenome>
    {
        internal NumericGenome(IEnumerable<double> coordinates)
        {
            Coordinates = Array.AsReadOnly(coordinates.ToArray());
            Identity = EvolutionHash.Combine(Coordinates.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
        }
        internal IReadOnlyList<double> Coordinates { get; }
        internal string Identity { get; }
        public NumericGenome CreateOwnedSnapshot() => new(Coordinates);
    }

    private sealed class NumericTask(QualityTask kind) : IEvolutionTask<NumericGenome>
    {
        public string Id => kind.ToString();
        public string VersionHash => "numeric-development-v1";
        public string EvaluatorVersionHash => VersionHash;
        internal int Calls { get; private set; }
        public ValueTask<EvolutionCanonicalGenome<NumericGenome>> CanonicalizeAsync(NumericGenome genome,
            CancellationToken cancellationToken = default) => new(new EvolutionCanonicalGenome<NumericGenome>(genome, genome.Identity));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<NumericGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            IReadOnlyList<double> x = candidate.CanonicalGenome.Genome.Coordinates;
            double loss = x.Select((value, index) => kind switch
            {
                QualityTask.Sphere => value * value,
                QualityTask.ShiftedQuadratic => Math.Pow(value - (index % 2 == 0 ? 0.75 : -0.25), 2),
                QualityTask.AnisotropicQuadratic => (index + 1) * (index + 1) * value * value,
                QualityTask.RippledQuadratic => value * value + 10 * (1 - Math.Cos(2 * Math.PI * value)),
                _ => throw new ArgumentOutOfRangeException(nameof(kind))
            }).Sum();
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-loss,
                new Dictionary<string, double> { ["coordinate-0"] = x[0], ["coordinate-1"] = x[1] }, costUnits: 1));
        }
    }

    private sealed class NumericVariation(EvolutionResourceLedger ledger, double radius, bool restart = false) : IVariationOperator<NumericGenome>
    {
        public string Id => restart ? "uniform-restart" : "uniform-mutation-" + radius.ToString("R", CultureInfo.InvariantCulture);
        public string VersionHash => EvolutionHash.Combine(new[] { "numeric-variation-v2-metered", Id });
        public ValueTask<NumericGenome> ProposeAsync(EvolutionVariationContext<NumericGenome> context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cost = EvolutionResources.Of("proposal_calls", 1);
            using EvolutionResourceReservation reservation = ledger.TryReserve("proposal/" + context.Generation.ToString(CultureInfo.InvariantCulture),
                EvolutionResourceStage.Proposal, cost, cost) ?? throw new EvolutionResourceBudgetException("proposal");
            var genome = restart ? RandomGenome(context.Random) : new NumericGenome(
                context.Parent.Candidate.CanonicalGenome.Genome.Coordinates.Select(value =>
                    Math.Clamp(value + radius * (2 * context.Random.NextDouble() - 1), -5, 5)));
            reservation.Complete(cost);
            return new ValueTask<NumericGenome>(genome);
        }
    }

    private sealed class CmaNumericVariation : IOutcomeAwareVariationOperator<NumericGenome>
    {
        private readonly EvolutionResourceLedger _ledger;
        private readonly EvolutionSearchSpace _space;
        internal DiagonalCmaEmitter Emitter { get; }
        internal CmaNumericVariation(EvolutionResourceLedger ledger)
        {
            _ledger = ledger;
            var builder = new EvolutionSearchSpaceBuilder();
            for (int i = 0; i < Dimensions; i++) builder.Add(EvolutionParameter.Real("x" + i.ToString(CultureInfo.InvariantCulture), -5, 5));
            _space = builder.Build(); Emitter = new DiagonalCmaEmitter(_space);
        }
        public string Id => "numeric-diagonal-cma";
        public string VersionHash => EvolutionHash.Combine(new[] { "numeric-cma-adapter-v1", Emitter.VersionHash });
        public async ValueTask<NumericGenome> ProposeAsync(EvolutionVariationContext<NumericGenome> context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cost = EvolutionResources.Of("proposal_calls", 1);
            using EvolutionResourceReservation reservation = _ledger.TryReserve("proposal/" + context.Generation.ToString(CultureInfo.InvariantCulture),
                EvolutionResourceStage.Proposal, cost, cost) ?? throw new EvolutionResourceBudgetException("proposal");
            EvolutionArchiveEntry<NumericGenome> source = context.Parent;
            EvolutionSearchGenome genome = _space.CreateGenome(_space.Parameters.Select((parameter, index) =>
                new KeyValuePair<string, EvolutionParameterValue>(parameter.Name, EvolutionParameterValue.Numeric(source.Candidate.CanonicalGenome.Genome.Coordinates[index]))));
            var parent = new EvolutionArchiveEntry<EvolutionSearchGenome>(source.Cell,
                new EvolutionCandidate<EvolutionSearchGenome>(source.Candidate.EvaluationId,
                    new EvolutionCanonicalGenome<EvolutionSearchGenome>(genome, source.Evaluation.GenomeId), source.Candidate.Lineage), source.Evaluation);
            var typedContext = new EvolutionVariationContext<EvolutionSearchGenome>(parent, Array.Empty<EvolutionArchiveEntry<EvolutionSearchGenome>>(),
                context.Random, context.Generation, context.Island, context.ParentArtifacts);
            EvolutionSearchGenome proposed = await Emitter.ProposeAsync(typedContext, cancellationToken);
            reservation.Complete(cost);
            return new NumericGenome(_space.Parameters.Select(parameter => proposed.Number(parameter.Name)));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => Emitter.Observe(evaluation, insertionResult);
        public string CaptureState() => Emitter.CaptureState();
        public void RestoreState(string state) => Emitter.RestoreState(state);
    }

    private sealed class BestSelection : ISelectionPolicy<NumericGenome>
    {
        public string Id => "best-parent";
        public string VersionHash => "best-parent-v1";
        public EvolutionSelection<NumericGenome>? Select(IEvolutionArchive<NumericGenome> archive, StableRandom random, int inspirationCount) =>
            archive.Best is { } best ? new EvolutionSelection<NumericGenome>(best, Array.Empty<EvolutionArchiveEntry<NumericGenome>>()) : null;
    }

    private sealed class Progress : IEvolutionObserver<NumericGenome>
    {
        internal List<SampleRecord> Samples { get; } = new();
        internal double? BestLoss { get; private set; }
        public ValueTask OnEventAsync(EvolutionEvent<NumericGenome> evolutionEvent, CancellationToken cancellationToken = default)
        {
            if (evolutionEvent.Kind != EvolutionEventKind.Evaluated || evolutionEvent.Evaluation is not { } evaluation) return default;
            if (evaluation.Status == EvolutionEvaluationStatus.Completed && evaluation.Quality is { } quality)
                BestLoss = Math.Min(BestLoss ?? double.MaxValue, -quality);
            Samples.Add(new SampleRecord(evaluation.EvaluationId, evaluation.Status, BestLoss, evaluation.Cost.AttemptCount,
                evaluation.Cost.CostUnits, evaluation.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray()));
            return default;
        }
    }
}
