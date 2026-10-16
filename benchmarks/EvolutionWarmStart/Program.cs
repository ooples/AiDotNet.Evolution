// US-23 (#41): what a warm start saves, measured in evaluations rather than asserted.
//
// The story's metric is "tokens for a warm run to reach cold final quality". Tokens need a model and
// a paid campaign; evaluations do not, and on a numeric objective an evaluation is the unit the
// budget is spent in. So this measures the same shape with no spend:
//
//   prefix  - run half the budget and checkpoint. This is the work a previous run already paid for.
//   cold    - a fresh run with the full budget. Its final best quality is the bar, and the
//             evaluation at which it first reached that bar is what reaching the bar cost it.
//   warm    - a fresh engine resuming the prefix checkpoint, run until it reaches the cold bar. Only
//             the evaluations after the resume are charged: the prefix was paid for already.
//
// The ratio is warm-after-resume over cold-to-bar. Below 1 means the reused state is worth something.
using System.Globalization;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;

internal static class Program
{
    internal const string Protocol = "warm-start-v1";
    private static readonly ulong[] Seeds = { 9101, 9102, 9103, 9104, 9105, 9106, 9107, 9108 };
    private const int Budget = 120;
    private const int Prefix = Budget / 2;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 0 && (args.Length != 2 || args[0] != "--output"))
            throw new ArgumentException("Usage: EvolutionWarmStart [--output new-rows.json]");
        using var stream = args.Length == 2 ? new FileStream(args[1], FileMode.CreateNew, FileAccess.Write) : null;
        string root = Path.Combine(Path.GetTempPath(), "evolution-warm-start", Guid.NewGuid().ToString("N"));
        var rows = new List<Row>();
        try
        {
            foreach (ulong seed in Seeds) rows.Add(await MeasureAsync(seed, root).ConfigureAwait(false));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }

        foreach (Row row in rows)
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "seed={0} cold_bar={1:F6} cold_to_bar={2,-3} warm_after_resume={3,-3} ratio={4:F4} prefix_best={5:F6} met_on_resume={6}",
                row.Seed, row.ColdBar, row.ColdEvaluationsToBar, row.WarmEvaluationsAfterResume,
                row.EvaluationRatio, row.PrefixBest, row.BarMetOnResume));

        var report = new Report(Protocol, Budget, Prefix, Seeds, rows.ToArray());
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (stream is not null)
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
            Console.WriteLine("wrote " + args[1]);
        }
        return 0;
    }

    internal sealed record Row(ulong Seed, double ColdBar, int ColdEvaluationsToBar, double PrefixBest,
        int WarmEvaluationsAfterResume, bool WarmReachedBar, bool BarMetOnResume, double EvaluationRatio,
        double WarmFinalBest);

    internal sealed record Report(string Protocol, int Budget, int Prefix, ulong[] Seeds, Row[] Rows);

    private static EvolutionSearchSpace Space() => new EvolutionSearchSpaceBuilder()
        .Add(EvolutionParameter.Real("x0", -1, 1))
        .Add(EvolutionParameter.Real("x1", -1, 1))
        .Add(EvolutionParameter.Real("x2", -1, 1))
        .Add(EvolutionParameter.Real("x3", -1, 1))
        .Build();

    /// <summary>A rippled quadratic: one broad basin with local structure, so progress is gradual.</summary>
    /// <remarks>A single smooth bowl would be solved in a handful of evaluations and no reuse could show.</remarks>
    private static double Quality(EvolutionSearchGenome genome)
    {
        double total = 0;
        for (int index = 0; index < 4; index++)
        {
            double value = genome.Number("x" + index.ToString(CultureInfo.InvariantCulture)) - 0.3;
            total += value * value + 0.12 * Math.Sin(12 * value);
        }
        return -total;
    }

    /// <summary>Records the best quality after each committed evaluation, which is what "cost to reach" needs.</summary>
    private sealed class Trace : IEvolutionObserver<EvolutionSearchGenome>
    {
        private double _best = double.NegativeInfinity;
        public List<double> Best { get; } = new();

        public ValueTask OnEventAsync(EvolutionEvent<EvolutionSearchGenome> evolutionEvent, CancellationToken cancellationToken = default)
        {
            if (evolutionEvent.Kind == EvolutionEventKind.Evaluated &&
                evolutionEvent.Evaluation is { Status: EvolutionEvaluationStatus.Completed, Quality: { } quality })
            {
                _best = Math.Max(_best, quality);
                Best.Add(_best);
            }
            return default;
        }
    }

    private static async Task<(Trace Trace, double Best, string? StateHash)> RunAsync(
        ulong seed, int budget, string? directory, bool resume, string runId)
    {
        EvolutionSearchSpace space = Space();
        var trace = new Trace();
        var task = new EvolutionSearchTask(space, "warm-start", Protocol, Protocol, (genome, _, token) =>
        {
            token.ThrowIfCancellationRequested();
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(
                Quality(genome), new Dictionary<string, double> { ["x0"] = genome.Number("x0") }, costUnits: 1));
        });
        var ledger = new EvolutionResourceLedger(runId, EvolutionResources.Of("cost_units", budget));
        var options = new EvolutionEngineOptions
        {
            RunId = "warm-start-" + seed.ToString(CultureInfo.InvariantCulture),
            Seed = seed,
            MaxEvaluationAttempts = budget,
            MaxProposals = budget * 10,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            InspirationCount = 2,
            MigrationInterval = 0,
            CheckpointInterval = directory is null ? 0 : 10,
            Resume = resume,
        };
        var engine = new EvolutionEngine<EvolutionSearchGenome>(
            new ResourceMeteredEvolutionTask<EvolutionSearchGenome>(task, ledger, new[] { 1m }),
            EvolutionSearchPresets.CreateAdaptiveMixed(space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x0", -1, 1, 8) }),
            options,
            observer: trace,
            checkpointStore: directory is null ? null : new DirectoryEvolutionCheckpointStore(directory),
            genomeCodec: directory is null ? null : space);
        var initial = new[] { space.Sample(StableRandom.CreateStream(seed, 1UL)), space.Sample(StableRandom.CreateStream(seed, 2UL)) };
        EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(initial).ConfigureAwait(false);
        return (trace, result.Best?.Evaluation.Quality ?? double.NegativeInfinity, result.StateHash);
    }

    private static async Task<Row> MeasureAsync(ulong seed, string root)
    {
        string directory = Path.Combine(root, seed.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        // The work a previous run already paid for.
        (Trace prefixTrace, double prefixBest, _) = await RunAsync(seed, Prefix, directory, resume: false, "prefix-" + seed);

        // The bar, and what reaching it cost a run that started from nothing.
        (Trace coldTrace, double coldBar, _) = await RunAsync(seed, Budget, null, resume: false, "cold-" + seed);
        int coldToBar = coldTrace.Best.FindIndex(best => best >= coldBar - 1e-12) + 1;
        if (coldToBar <= 0) throw new InvalidOperationException("The cold run never reached its own final quality.");

        // The warm run resumes the prefix and is charged only for what it spends after that. The
        // restored state's best is the prefix's best, so the warm run starts from there rather than
        // from nothing: measuring it from negative infinity made a resumed run that was *already* at
        // the bar look like one that never reached it, which is the opposite of the truth.
        (Trace warmTrace, double warmBest, _) = await RunAsync(seed, Budget, directory, resume: true, "warm-" + seed);
        bool metOnResume = prefixBest >= coldBar - 1e-12;
        int warmCost;
        bool reached;
        if (metOnResume)
        {
            // The strongest reuse outcome there is: the state carried over already matches what the
            // cold run needed its whole budget to reach, so reaching the bar cost nothing.
            warmCost = 0;
            reached = true;
        }
        else
        {
            int firstAtBar = warmTrace.Best.FindIndex(best => Math.Max(prefixBest, best) >= coldBar - 1e-12) + 1;
            reached = firstAtBar > 0;
            // A warm run that never reaches the bar is charged everything it spent: it did not get
            // there, and an undefined ratio would hide that rather than report it.
            warmCost = reached ? firstAtBar : Math.Max(1, warmTrace.Best.Count);
        }

        return new Row(seed, coldBar, coldToBar, prefixBest, warmCost, reached, metOnResume,
            warmCost / (double)coldToBar, warmBest);
    }
}
