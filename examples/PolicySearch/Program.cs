using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution;

// CPU-only protocol demonstration. These illustrative landscapes and heuristic baselines are
// not a preregistered independent competitor benchmark and must not support superiority claims.
if (args.Length != 1) throw new ArgumentException("Usage: PolicySearch <new-report.json>");
string destination = Path.GetFullPath(args[0]);
if (File.Exists(destination)) throw new IOException("Refusing to overwrite an existing report.");
const string units = "numeric-objective-calls-and-proposals-v1";
var policies = new List<EvolutionSearchPolicy>();
foreach (int localWeight in new[] { 1, 4 })
    foreach (var selection in new[] { EvolutionPolicySelectionSchedule.Uniform, EvolutionPolicySelectionSchedule.Greedy, EvolutionPolicySelectionSchedule.ExploreThenExploit })
        foreach (int restart in new[] { 0, 16 })
            foreach (var context in new[] { EvolutionPolicyContext.ParentOnly, EvolutionPolicyContext.Inspirations, EvolutionPolicyContext.FeedbackAndInspirations })
                policies.Add(new(new Dictionary<string, int> { ["global"] = 1, ["local"] = localWeight }, selection, restart, context));
EvolutionSearchPolicy baselineA = policies.First(value => value.OperatorWeights["local"] == 4 && value.Selection == EvolutionPolicySelectionSchedule.Greedy &&
    value.RestartAfterEvaluations == 0 && value.Context == EvolutionPolicyContext.ParentOnly);
EvolutionSearchPolicy baselineB = policies.First(value => value.OperatorWeights["local"] == 1 && value.Selection == EvolutionPolicySelectionSchedule.Uniform &&
    value.RestartAfterEvaluations == 16 && value.Context == EvolutionPolicyContext.Inspirations);
var baselines = new[]
{
    new EvolutionPolicyBaseline("local-greedy-control", baselineA,
        "Multiscale local search with 20% global proposals; hand-specified exploitation control, not certified strongest.",
        EvolutionHash.Compute("PolicySearch manual controls v1: no prior tuning campaign"), EvolutionResources.Of("cost_units", 0), units),
    new EvolutionPolicyBaseline("restarted-diversity-control", baselineB,
        "Equal global/local mixture, uniform archive selection, bounded restarts and inspiration recombination; hand-specified diversity control.",
        EvolutionHash.Compute("PolicySearch manual controls v1: no prior tuning campaign"), EvolutionResources.Of("cost_units", 0), units)
};
EvolutionPolicyTrial Trial(string name, Func<double, double, double> objective, double worst) =>
    EvolutionPolicyEngineTrial.Create(name, name, "landscape-v1", "objective-v1",
        () => new Landscape(name, objective), (_, _) => new[] { new Point(4, -4) },
        new[] { Definition("global"), Definition("local") },
        new[] { new EvolutionDescriptorDefinition("radius", 0, 8, 8) }, new PointCodec(),
        EvolutionOptimizationDirection.Minimize, worst, 0, 1, units);
EvolutionPolicyEngineOperator<Point> Definition(string id) => new(id, "numeric-proposals-v1", EvolutionResources.Of("cost_units", 1), () => new Proposal(id));
var development = new[]
{
    Trial("sphere", (x, y) => x * x + y * y, 50),
    Trial("rosenbrock", (x, y) => 100 * Math.Pow(y - x * x, 2) + Math.Pow(1 - x, 2), 90000)
};
var holdout = new[]
{
    Trial("rastrigin", (x, y) => 20 + x * x + y * y - 10 * (Math.Cos(2 * Math.PI * x) + Math.Cos(2 * Math.PI * y)), 100),
    Trial("ackley", (x, y) => -20 * Math.Exp(-0.2 * Math.Sqrt((x * x + y * y) / 2)) - Math.Exp((Math.Cos(2 * Math.PI * x) + Math.Cos(2 * Math.PI * y)) / 2) + 20 + Math.E, 25),
    Trial("griewank", (x, y) => (x * x + y * y) / 4000 - Math.Cos(x) * Math.Cos(y / Math.Sqrt(2)) + 1, 3),
    Trial("booth", (x, y) => Math.Pow(x + 2 * y - 7, 2) + Math.Pow(2 * x + y - 5, 2), 1000),
    Trial("beale", (x, y) => Math.Pow(1.5 - x + x * y, 2) + Math.Pow(2.25 - x + x * y * y, 2) + Math.Pow(2.625 - x + x * y * y * y, 2), 500000),
    Trial("absolute-ridge", (x, y) => Math.Abs(x + y) + 0.1 * Math.Abs(x - y), 12)
};
var options = new EvolutionPolicyOptimizationOptions(new EvolutionPolicyTrialBudget(32, 48, 2, TimeSpan.FromSeconds(20),
    EvolutionResources.Of("cost_units", 80)), EvolutionResources.Of("cost_units", 20000),
    maximumDevelopmentPolicies: 8, maximumOuterProposals: 64, replicates: 3, maximumInnerTrials: 256,
    seed: 426, minimumMeanGain: 0.005, maximumWithinTaskRange: 0.4);
var report = await new EvolutionPolicyOptimizer(new EvolutionPolicySpace(policies), options, development, holdout, baselines, baselineA).RunAsync();
if (report.Trials.Count == 0 || report.Trials.Any(value => value.Observation?.EvidenceJson is null) ||
    report.Outcome is not ("NoDevelopmentImprovement" or "GeneralizationRejected" or "GeneralizationPassed"))
    throw new InvalidOperationException("The CPU protocol did not complete: " + report.Outcome + "/" + report.FailureCode);
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write))
using (var writer = new StreamWriter(output)) await writer.WriteAsync(report.ToJson());
Console.WriteLine($"{report.Outcome}; trials={report.Trials.Count}; work={report.Resources.Spent["cost_units"]}; plan={report.PlanHash}");
Console.WriteLine("Illustrative CPU protocol only; no competitor or production-generalization claim.");

internal sealed record Point(double X, double Y) : IImmutableEvolutionGenome<Point>
{
    public Point CreateOwnedSnapshot() => this with { };
}
internal sealed class PointCodec : IEvolutionGenomeCodec<Point>
{
    public string Id => "point";
    public string VersionHash => "point-v1";
    public string Serialize(Point value) => value.X.ToString("R", CultureInfo.InvariantCulture) + "," + value.Y.ToString("R", CultureInfo.InvariantCulture);
    public Point Deserialize(string value)
    {
        string[] parts = value.Split(',');
        if (parts.Length != 2) throw new InvalidDataException();
        return new(double.Parse(parts[0], CultureInfo.InvariantCulture), double.Parse(parts[1], CultureInfo.InvariantCulture));
    }
}
internal sealed class Landscape(string name, Func<double, double, double> objective) : IEvolutionTask<Point>
{
    public string Id => name;
    public string VersionHash => "landscape-v1";
    public string EvaluatorVersionHash => "objective-v1";
    public ValueTask<EvolutionCanonicalGenome<Point>> CanonicalizeAsync(Point value, CancellationToken cancellationToken = default) =>
        new(new EvolutionCanonicalGenome<Point>(value, EvolutionHash.Compute(new PointCodec().Serialize(value))));
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<Point> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        var point = candidate.CanonicalGenome.Genome;
        double quality = objective(point.X, point.Y);
        return new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, quality, EvolutionOptimizationDirection.Minimize,
            new Dictionary<string, double> { ["radius"] = Math.Sqrt(point.X * point.X + point.Y * point.Y) }, costUnits: 1,
            artifacts: new[] { new EvolutionArtifact("objective", JsonSerializer.Serialize(new { quality })) }));
    }
}
internal sealed class Proposal(string id) : ICostedEvolutionProposalSource<Point>
{
    public string Id => id;
    public string VersionHash => "numeric-proposals-v1";
    public ValueTask<EvolutionResourceResult<Point>> ProposeAsync(EvolutionVariationContext<Point> context, CancellationToken cancellationToken = default)
    {
        Point parent = context.Parent.Candidate.CanonicalGenome.Genome;
        if (context.Inspirations.Count > 0 && context.Random.NextDouble() < 0.2)
        {
            Point other = context.Inspirations[0].Candidate.CanonicalGenome.Genome;
            parent = new((parent.X + other.X) / 2, (parent.Y + other.Y) / 2);
        }
        double scale = context.ParentArtifacts.Count > 0 ? 0.25 : context.Random.NextDouble() < 0.5 ? 0.1 : 1;
        Point proposed = id == "global" ? new(10 * context.Random.NextDouble() - 5, 10 * context.Random.NextDouble() - 5) :
            new(Math.Clamp(parent.X + scale * (2 * context.Random.NextDouble() - 1), -5, 5),
                Math.Clamp(parent.Y + scale * (2 * context.Random.NextDouble() - 1), -5, 5));
        return new(new EvolutionResourceResult<Point>(proposed, EvolutionResources.Of("cost_units", 1)));
    }
    public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) { }
    public string CaptureState() => "stateless";
    public void RestoreState(string state) { if (state != "stateless") throw new InvalidDataException(); }
}
