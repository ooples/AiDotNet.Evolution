using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution;

if (args.Length != 2 || args[1].Length != 40 || args[1].Any(c => !Uri.IsHexDigit(c)))
    throw new ArgumentException("Usage: ParetoSearch <new-report.json> <40-character-source-commit>");
string reportPath = Path.GetFullPath(args[0]);
if (File.Exists(reportPath)) throw new IOException("Preserve prior campaign evidence: choose a new report path.");
var definition = new EvolutionParetoDefinition(new[]
{
    new EvolutionObjectiveDefinition("f1", EvolutionOptimizationDirection.Minimize, 0, 2),
    new EvolutionObjectiveDefinition("f2", EvolutionOptimizationDirection.Minimize, 0, 2)
}, capacity: 32);
const int seedCount = 30, evaluationBudget = 256;
var rows = new List<RunRow>();
foreach (bool constrained in new[] { false, true })
    for (ulong seed = 1; seed <= seedCount; seed++)
        foreach (string method in new[] { "pareto-32", "scalar-grid-32", "scalar-best-1" })
        {
            var task = new QuadraticTask(constrained);
            IEvolutionArchive<Point> Archive(int _) => method == "pareto-32"
                ? new ParetoArchive<Point>(definition, EvolutionOptimizationDirection.Minimize)
                : new MapElitesArchive<Point>(new[] { new EvolutionDescriptorDefinition("x", 0, 1,
                    method == "scalar-grid-32" ? 32 : 1) }, EvolutionOptimizationDirection.Minimize);
            var engine = new EvolutionEngine<Point>(task, new LocalVariation(), Archive, new EvolutionEngineOptions
            {
                Seed = seed,
                MaxEvaluationAttempts = evaluationBudget,
                MaxProposals = 4096,
                MaxGenerations = 4096,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                MigrationInterval = 0,
                EnableEvaluationCache = false
            });
            var timer = Stopwatch.StartNew();
            var result = await engine.RunAsync(new[] { .1, .3, .5, .7, .9 }.Select(x => new Point(x, .8)));
            timer.Stop();
            if (result.Counters.EvaluationAttempts != evaluationBudget || result.Counters.CompletedEvaluations != evaluationBudget ||
                result.RetainedFailures.Count != 0)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
                var failure = new { Schema = "pareto-campaign-failure-v1", SourceCommit = args[1], Task = task.Id, Seed = seed, Method = method, result.StopReason,
                    result.Counters, result.RetainedFailures, CompletedRuns = rows };
                File.WriteAllText(reportPath, JsonSerializer.Serialize(failure, new JsonSerializerOptions { WriteIndented = true }));
                throw new InvalidOperationException("Campaign budget validation failed: " + JsonSerializer.Serialize(new {
                    task.Id, seed, method, result.StopReason, result.Counters, result.RetainedFailures }));
            }
            var front = result.ParetoFront ?? new EvolutionParetoFront<Point>(definition, result.Islands.SelectMany(island => island.Entries));
            if (front.Entries.Count == 0 || front.Entries.Any(entry => entry.Evaluation.ConstraintViolations.Any(value => value > 0)))
                throw new InvalidOperationException("Campaign produced no valid deployable front.");
            rows.Add(new RunRow(task.Id, seed, method, method == "scalar-best-1" ? 1 : 32,
                result.Counters.EvaluationAttempts, result.Counters.Proposals, timer.Elapsed.TotalMilliseconds,
                result.StateHash, front.Hypervolume(), front.Entries.Min(entry => entry.Evaluation.Objectives[0]),
                front.Entries.Min(entry => entry.Evaluation.Objectives[1]),
                result.Islands.SelectMany(island => island.Entries).Min(entry => entry.Evaluation.Quality!.Value),
                front.Entries.Select(entry => new PointRow(entry.Evaluation.GenomeId, entry.Candidate.CanonicalGenome.Genome,
                    entry.Evaluation.Objectives.ToArray(), entry.Evaluation.Quality!.Value)).ToArray()));
        }
var comparisons = new List<object>();
foreach (string task in rows.Select(row => row.Task).Distinct())
    foreach (string baseline in new[] { "scalar-grid-32", "scalar-best-1" })
    {
        var paired = rows.Where(row => row.Task == task && row.Method == "pareto-32").OrderBy(row => row.Seed)
            .Zip(rows.Where(row => row.Task == task && row.Method == baseline).OrderBy(row => row.Seed), (pareto, scalar) => (pareto, scalar)).ToArray();
        if (paired.Length != seedCount || paired.Any(pair => pair.pareto.Seed != pair.scalar.Seed)) throw new InvalidOperationException("Broken seed pairing.");
        comparisons.Add(new
        {
            Task = task,
            Baseline = baseline,
            Pairs = seedCount,
            HypervolumeDelta = Summarize(paired.Select(pair => pair.pareto.Hypervolume - pair.scalar.Hypervolume).ToArray()),
            MinimumF1Delta = Summarize(paired.Select(pair => pair.pareto.MinimumF1 - pair.scalar.MinimumF1).ToArray()),
            MinimumF2Delta = Summarize(paired.Select(pair => pair.pareto.MinimumF2 - pair.scalar.MinimumF2).ToArray()),
            BestScalarDelta = Summarize(paired.Select(pair => pair.pareto.BestScalar - pair.scalar.BestScalar).ToArray())
        });
    }
Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
File.WriteAllText(reportPath, JsonSerializer.Serialize(new
{
    Schema = "pareto-campaign-v1",
    SourceCommit = args[1],
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    Tasks = "Authored bounded quadratic tradeoffs; disconnected variant rejects 0.4 < x < 0.6.",
    Objectives = definition.Objectives,
    definition.DefinitionHash,
    NormalizedReferencePoint = new[] { 1d, 1d },
    EvaluationBudget = evaluationBudget,
    SeedCount = seedCount,
    CostUnitsPerEvaluation = 1,
    SharedControls = "Identical initial genomes, evaluation budget, local-variation code, seed list, worker count and scalar weights (0.5, 0.5). Grid uses 32 x-axis bins; single-best uses one.",
    Interpretation = "Higher hypervolume and lower individual minima/scalar quality are better. Confidence intervals are paired-seed percentile bootstrap (2000 resamples, seed 73), descriptive and not multiplicity adjusted. Wall time is observational, not a controlled performance claim. This does not compare OpenEvolve or establish general superiority.",
    Comparisons = comparisons,
    Runs = rows
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Validated {rows.Count} matched-budget runs; report: {reportPath}");

static object Summarize(double[] differences)
{
    var random = new StableRandom(73, 1); var means = new double[2000];
    for (int repeat = 0; repeat < means.Length; repeat++)
    {
        double sum = 0;
        for (int i = 0; i < differences.Length; i++) sum += differences[random.NextInt(differences.Length)];
        means[repeat] = sum / differences.Length;
    }
    Array.Sort(means);
    return new { Mean = differences.Average(), Lower95 = means[49], Upper95 = means[1949], PositivePairs = differences.Count(value => value > 0), NegativePairs = differences.Count(value => value < 0) };
}

internal sealed record Point(double X, double Y) : IImmutableEvolutionGenome<Point>
{
    public Point CreateOwnedSnapshot() => new(X, Y);
}
internal sealed record PointRow(string GenomeId, Point Genome, double[] Objectives, double ScalarQuality);
internal sealed record RunRow(string Task, ulong Seed, string Method, int Capacity, long Evaluations, long Proposals,
    double WallMilliseconds, string StateHash, double Hypervolume, double MinimumF1, double MinimumF2, double BestScalar, PointRow[] Front);

internal sealed class LocalVariation : IVariationOperator<Point>
{
    public string Id => "bounded-local-step";
    public string VersionHash => "bounded-local-step-v1";
    public ValueTask<Point> ProposeAsync(EvolutionVariationContext<Point> context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var parent = context.Parent.Candidate.CanonicalGenome.Genome;
        return new(new Point(Math.Clamp(parent.X + (context.Random.NextDouble() - .5) * .4, 0, 1),
            Math.Clamp(parent.Y + (context.Random.NextDouble() - .5) * .4, 0, 1)));
    }
}

internal sealed class QuadraticTask(bool constrained) : IEvolutionTask<Point>
{
    public string Id => constrained ? "authored-disconnected-quadratic" : "authored-convex-quadratic";
    public string VersionHash => "bounded-quadratic-v1";
    public string EvaluatorVersionHash => "bounded-quadratic-evaluation-v1";
    public ValueTask<EvolutionCanonicalGenome<Point>> CanonicalizeAsync(Point genome, CancellationToken cancellationToken = default) =>
        new(new EvolutionCanonicalGenome<Point>(genome, string.Join(",", genome.X.ToString("R", CultureInfo.InvariantCulture), genome.Y.ToString("R", CultureInfo.InvariantCulture))));
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<Point> candidate, EvolutionEvaluationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var point = candidate.CanonicalGenome.Genome;
        double first = point.X * point.X + point.Y * point.Y, second = (1 - point.X) * (1 - point.X) + point.Y * point.Y;
        return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, .5 * first + .5 * second,
            EvolutionOptimizationDirection.Minimize, new Dictionary<string, double> { ["x"] = point.X }, new[] { first, second },
            new[] { constrained && point.X > .4 && point.X < .6 ? 1d : 0d }, costUnits: 1));
    }
}
