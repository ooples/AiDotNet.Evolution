using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;

// V1-75: does the engine keep a slow model busy? Proposals take log-normal time (median 100 ms, p95 400 ms: the
// story's 10 s / 40 s scaled down 100x). Usage: EvolutionLatency <proposals> <concurrency> [Auto|Batch|Pipeline]
if (args.Length is < 2 or > 3 || !int.TryParse(args[0], out int proposals) || !int.TryParse(args[1], out int concurrency) ||
    proposals is < 16 or > 100_000 || concurrency is < 1 or > 256)
{
    Console.Error.WriteLine("Usage: EvolutionLatency <proposals 16..100000> <concurrency 1..256> [Auto|Batch|Pipeline]");
    return 2;
}
EvolutionDispatchMode dispatch = args.Length == 3 ? Enum.Parse<EvolutionDispatchMode>(args[2], ignoreCase: false) : EvolutionDispatchMode.Auto;
var builder = new EvolutionSearchSpaceBuilder();
builder.Add(EvolutionParameter.Real("x", -5, 5));
EvolutionSearchSpace space = builder.Build();
var task = new EvolutionSearchTask(space, "latency", "v1", "null-v1", (genome, _, _) =>
    new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-genome.Number("x") * genome.Number("x"),
        new Dictionary<string, double> { ["x"] = genome.Number("x") }, costUnits: 1)));
var model = new SlowModel(space);
var options = new EvolutionEngineOptions
{
    RunId = "latency", Seed = 11, MaxEvaluationAttempts = proposals, MaxProposals = proposals * 2, MaxGenerations = proposals * 2,
    MaxDegreeOfParallelism = concurrency, MigrationInterval = 0, CheckpointInterval = 0, Dispatch = dispatch,
    ExecutionMode = Environment.GetEnvironmentVariable("LATENCY_MODE") == "Opportunistic" ? EvolutionExecutionMode.Opportunistic : EvolutionExecutionMode.Deterministic
};
options.Pipeline.MaxProposalConcurrency = concurrency;
int windowMultiple = int.TryParse(Environment.GetEnvironmentVariable("LATENCY_WINDOW_MULTIPLE"), out int window) ? window : 4;
if (dispatch == EvolutionDispatchMode.Continuous) options.MaxInFlight = concurrency * windowMultiple;
int waveMultiple = int.TryParse(Environment.GetEnvironmentVariable("LATENCY_WAVE_MULTIPLE"), out int multiple) ? multiple : 4;
options.Pipeline.WaveSize = concurrency * waveMultiple;
options.Pipeline.ProposalQueueCapacity = concurrency * waveMultiple;
options.Pipeline.EvaluationQueueCapacity = concurrency * waveMultiple;
var engine = new EvolutionEngine<EvolutionSearchGenome>(task, model,
    _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 64) }), options);
var seeds = new[] { space.Sample(StableRandom.CreateStream(11, 0)) };
var clock = Stopwatch.StartNew();
EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(seeds);
clock.Stop();
double ideal = model.TotalLatencySeconds / concurrency;
Console.WriteLine(JsonSerializer.Serialize(new
{
    Proposals = model.Calls, Concurrency = concurrency, Dispatch = dispatch.ToString(), WaveSize = options.Pipeline.WaveSize, options.MaxInFlight,
    MeanInFlight = model.InFlightSeconds / clock.Elapsed.TotalSeconds,
    InFlightUtilisation = model.InFlightSeconds / clock.Elapsed.TotalSeconds / concurrency,
    WallSeconds = clock.Elapsed.TotalSeconds, IdealSeconds = ideal, WallOverIdeal = clock.Elapsed.TotalSeconds / ideal,
    result.Counters.CompletedEvaluations
}));
return 0;

internal sealed class SlowModel(EvolutionSearchSpace space) : IDeterministicConcurrentVariationOperator<EvolutionSearchGenome>, IEvolutionLatencyProfile
{
    // Uses only its own context's random stream, so overlapping calls cannot change the run.
    public bool SupportsDeterministicConcurrency => true;
    private long _calls, _inFlightTicks, _totalLatencyTicks;
    public string Id => "slow-model";
    public string VersionHash => "slow-model-v1";
    public bool IsLatencyBound => true;
    public long Calls => Interlocked.Read(ref _calls);
    public double InFlightSeconds => (double)Interlocked.Read(ref _inFlightTicks) / Stopwatch.Frequency;
    public double TotalLatencySeconds => (double)Interlocked.Read(ref _totalLatencyTicks) / Stopwatch.Frequency;

    public async ValueTask<EvolutionSearchGenome> ProposeAsync(EvolutionVariationContext<EvolutionSearchGenome> context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        // Log-normal with median 100 ms and p95 400 ms: sigma = ln(4) / 1.645.
        double u1 = Math.Max(1e-12, context.Random.NextDouble()), u2 = context.Random.NextDouble();
        double z = Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
        TimeSpan latency = TimeSpan.FromMilliseconds(Math.Min(2_000, 100 * Math.Exp(z * Math.Log(4) / 1.645)));
        long started = Stopwatch.GetTimestamp();
        await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
        long elapsed = Stopwatch.GetTimestamp() - started;
        Interlocked.Add(ref _inFlightTicks, elapsed);
        Interlocked.Add(ref _totalLatencyTicks, elapsed);
        return space.Sample(context.Random);
    }
}
