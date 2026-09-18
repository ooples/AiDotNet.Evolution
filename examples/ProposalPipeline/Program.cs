using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution;
using ProposalPipeline;

if (args.Length == 1 && args[0] == "--verify-replay")
{
    Execution baseline = await Run("Sphere", "ZeroLatency", "PipelineConcurrent", 0, 0, null);
    if (!baseline.Valid) throw new InvalidOperationException("Replay verification baseline failed.");
    var tape = new ResponseTape(baseline.Source.Responses.Values.OrderBy(value => value.Generation).ToArray(),
        baseline.Task.Responses.Values.OrderBy(value => value.EvaluationId).ToArray());
    int rejected = 0;
    for (int corruption = 0; corruption < 3; corruption++)
    {
        var proposals = (ProposalResponse[])tape.Proposals.Clone(); var evaluations = (EvaluationResponse[])tape.Evaluations.Clone();
        if (corruption == 0) proposals[0] = proposals[0] with { RequestIdentity = "wrong-context" };
        else if (corruption == 1) evaluations[0] = evaluations[0] with { RequestIdentity = "wrong-measurement" };
        else evaluations[0] = evaluations[0] with { Quality = 0.01 };
        Execution altered = await Run("Sphere", "ZeroLatency", "PipelineConcurrent", 0, 0, new ResponseTape(proposals, evaluations));
        if (altered.Valid && altered.StateHash == baseline.StateHash && altered.LogicalEvidence == baseline.LogicalEvidence)
            throw new InvalidOperationException("Corrupted external responses incorrectly reproduced the baseline.");
        if (altered.Source.PhysicalCalls != 0 || altered.Task.PhysicalCalls != 0) throw new InvalidOperationException("Replay invoked a physical backend.");
        rejected++;
    }
    Console.WriteLine(JsonSerializer.Serialize(new { RejectedCases = rejected, PhysicalReplayCalls = 0 }));
    return 0;
}

int seeds = 1, repetitions = 1;
if (args.Length > 3 || (args.Length > 0 && !int.TryParse(args[0], out seeds)) || seeds is < 1 or > 16 ||
    (args.Length > 1 && !int.TryParse(args[1], out repetitions)) || repetitions is < 1 or > 5)
{ Console.Error.WriteLine("Usage: ProposalPipeline [seeds 1..16 [timing-repetitions 1..5 [new-output.json]]]"); return 2; }
using var output = args.Length == 3 ? new FileStream(args[2], FileMode.CreateNew, FileAccess.Write, FileShare.Read) : null;
DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
var warmups = new List<object>(); var runs = new List<object>(); bool valid = true;
string[] methods = { "Batch", "Continuous", "PipelineSerial", "PipelineConcurrent" };
foreach (string method in methods)
{
    Execution warmup = await Run("Sphere", "ZeroLatency", method, 1000, 0, null);
    warmups.Add(warmup.Evidence); valid &= warmup.Valid;
}
foreach (string family in new[] { "Sphere", "Rugged" })
    foreach (string profile in new[] { "ZeroLatency", "ProposalBound", "Mixed" })
        for (ulong seed = 0; seed < (ulong)seeds; seed++)
            for (int repetition = 0; repetition < repetitions; repetition++)
                // Balanced rotation spreads process warmup/order effects; it does not make a shared host controlled hardware.
                foreach (string method in methods.Skip((int)(seed + (ulong)repetition) % methods.Length).Concat(methods.Take((int)(seed + (ulong)repetition) % methods.Length)))
                {
                    Execution live = await Run(family, profile, method, seed, repetition, null);
                    var tape = new ResponseTape(live.Source.Responses.Values.OrderBy(value => value.Generation).ToArray(),
                        live.Task.Responses.Values.OrderBy(value => value.EvaluationId).ToArray());
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(tape);
                    ResponseTape restored = JsonSerializer.Deserialize<ResponseTape>(bytes) ?? throw new InvalidDataException("Missing response tape.");
                    Execution replay = await Run(family, profile, method, seed, repetition, restored);
                    bool matches = live.Valid && replay.Valid && live.StateHash == replay.StateHash && live.LogicalEvidence == replay.LogicalEvidence &&
                        replay.Source.PhysicalCalls == 0 && replay.Task.PhysicalCalls == 0 &&
                        replay.Source.ReplayedCalls == live.Source.PhysicalCalls && replay.Task.ReplayedCalls == live.Task.PhysicalCalls;
                    valid &= matches;
                    runs.Add(new
                    {
                        Task = family,
                        Profile = profile,
                        Method = method,
                        Seed = seed,
                        TimingRepetition = repetition,
                        Status = matches ? "completed" : "failed",
                        ResponseTapeSha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        ReplayMatches = matches,
                        Live = live.Evidence,
                        Replay = replay.Evidence
                    });
                }
string json = JsonSerializer.Serialize(new
{
    Protocol = "authored-proposal-pipeline-v1",
    Seeds = seeds,
    TimingRepetitions = repetitions,
    StartedUtc = startedUtc,
    FinishedUtc = DateTimeOffset.UtcNow,
    ProposalCap = 32,
    EvaluationAttemptCap = 32,
    CostCap = 40,
    AssemblyVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
    AssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Assembly.GetExecutingAssembly().Location))).ToLowerInvariant(),
    CoreAssemblyVersion = typeof(EvolutionEngineOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
    CoreAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EvolutionEngineOptions).Assembly.Location))).ToLowerInvariant(),
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    OperatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    ProcessorCount = Environment.ProcessorCount,
    Interpretation = "Authored numeric objectives and artificial callback delays, not model calls or representative competitor evidence. " +
        "Live costs are fixed declared work tariffs; offline replay charges simulate original receipts and execute zero physical backend calls. " +
        "Same caps/cohorts; Continuous sees different feedback timing. Timing repetitions are not extra search seeds. " +
        "Warmups are retained separately. Shared-host elapsed times do not establish a production speedup or default-promotion decision.",
    WarmupRuns = warmups,
    Runs = runs
}, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
if (output is null) Console.WriteLine(json);
else { using var writer = new StreamWriter(output); await writer.WriteLineAsync(json); }
return valid ? 0 : 1;

static async Task<Execution> Run(string family, string profile, string method, ulong seed, int repetition, ResponseTape? tape)
{
    string runId = "pipeline-fixture-" + family + "-" + profile + "-" + method + "-" + seed + "-" + repetition;
    var ledger = new EvolutionResourceLedger(runId, EvolutionResources.Of("cost_units", 40), retainedReceiptLimit: 128);
    using EvolutionResourceReservation setup = ledger.TryReserve("initial-cohort", EvolutionResourceStage.Setup,
        EvolutionResources.Of("cost_units", 0.08m), EvolutionResources.Of("cost_units", 0.08m))!;
    var random = StableRandom.CreateStream(seed, 999);
    Point[] initial = Enumerable.Range(0, 8).Select(_ => new Point(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1)).ToArray();
    setup.Complete(EvolutionResources.Of("cost_units", 0.08m));
    var source = new FixtureProposalSource(profile, tape?.Proposals.ToDictionary(value => value.Generation));
    var task = new FixtureTask(family, profile, tape?.Evaluations.ToDictionary(value => value.EvaluationId));
    var options = new EvolutionEngineOptions
    {
        RunId = runId,
        Seed = seed,
        MaxProposals = 32,
        MaxEvaluationAttempts = 32,
        MaxGenerations = 32,
        ProposalBatchSize = 8,
        MaxDegreeOfParallelism = 4,
        CheckpointInterval = 0,
        MigrationInterval = 0,
        Dispatch = method == "Batch" ? EvolutionDispatchMode.Batch : method == "Continuous" ? EvolutionDispatchMode.Continuous : EvolutionDispatchMode.Pipeline,
        MaxInFlight = method == "Continuous" ? 8 : 0,
        Pipeline = new EvolutionPipelineOptions
        {
            WaveSize = 8,
            MaxProposalConcurrency = method == "PipelineConcurrent" ? 4 : 1,
            ProposalQueueCapacity = 2,
            EvaluationQueueCapacity = 2,
            MaximumScheduleRecords = 128
        }
    };
    var engine = new EvolutionEngine<Point>(new ResourceMeteredEvolutionTask<Point>(task, ledger, new[] { 1m }),
        new ResourceMeteredVariationOperator<Point>(source, ledger, EvolutionResources.Of("cost_units", 0.25m), "fixture-work-v1"),
        _ => new MapElitesArchive<Point>(new[] { new EvolutionDescriptorDefinition("x", -1, 1, 8), new EvolutionDescriptorDefinition("y", -1, 1, 8) }), options);
    using var process = Process.GetCurrentProcess();
    TimeSpan cpuStart = process.TotalProcessorTime; long allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
    var timer = Stopwatch.StartNew(); EvolutionRunResult<Point>? result = null; string? error = null;
    try { result = await engine.RunAsync(initial); }
    catch (Exception exception) { error = exception.GetType().Name; }
    timer.Stop();
    double cpuSeconds = (process.TotalProcessorTime - cpuStart).TotalSeconds;
    long allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedStart;
    EvolutionResourceSnapshot resources = ledger.Snapshot();
    var receipts = resources.Receipts.OrderBy(receipt => receipt.OperationId, StringComparer.Ordinal).ToArray();
    var proposals = source.Responses.Values.OrderBy(value => value.Generation).ToArray();
    var evaluations = task.Responses.Values.OrderBy(value => value.EvaluationId).ToArray();
    bool valid = error is null && result is not null && result.Counters.Proposals == 32 &&
        result.Counters.EvaluationAttempts == evaluations.Length && result.Counters.CompletedEvaluations == evaluations.Length &&
        proposals.Length == 24 && source.Observed.Count == 24 &&
        resources.Spent["cost_units"] == 0.08m + proposals.Sum(value => value.CostUnits) + evaluations.Sum(value => (decimal)value.CostUnits) &&
        resources.Spent["cost_units"] <= 40 && resources.Reserved["cost_units"] == 0 && resources.Unknown == 0 &&
        resources.Admitted == resources.Settled && resources.DroppedReceipts == 0 && !resources.MaximumViolated &&
        (engine.PipelineReport is null || engine.PipelineReport.IsScheduleComplete);
    string logical = JsonSerializer.Serialize(new
    {
        StateHash = result?.StateHash,
        Counters = result?.Counters,
        InitialPopulationHash = EvolutionHash.Combine(initial.Select(point => point.Identity)),
        Elites = result?.Islands.SelectMany(island => island.Entries).Select(entry => new { entry.Cell.StableKey, entry.Evaluation.GenomeId, entry.Evaluation.Quality }).ToArray(),
        Responses = new ResponseTape(proposals, evaluations),
        Observed = source.Observed,
        Receipts = receipts,
        Schedule = engine.PipelineReport?.Schedule
    });
    object evidence = new
    {
        RunId = runId,
        EvidenceKind = tape is null ? "LiveAuthoredFixture" : "OfflineRecordedResponseReplay",
        Status = valid ? "completed" : "failed",
        Error = error,
        InitialPopulationHash = EvolutionHash.Combine(initial.Select(point => point.Identity)),
        ElapsedSeconds = timer.Elapsed.TotalSeconds,
        ProcessCpuSeconds = cpuSeconds,
        ProcessAllocatedBytes = allocatedBytes,
        StateHash = result?.StateHash,
        Counters = result?.Counters,
        StopReason = result?.StopReason,
        PhysicalProposalCalls = source.PhysicalCalls,
        PhysicalEvaluationCalls = task.PhysicalCalls,
        ReplayedProposalCalls = source.ReplayedCalls,
        ReplayedEvaluationCalls = task.ReplayedCalls,
        BestQuality = result?.Islands.SelectMany(island => island.Entries).Select(entry => entry.Evaluation.Quality).DefaultIfEmpty(0).Max(),
        LogicalEvidenceSha256 = EvolutionHash.Compute(logical),
        ProposalResponses = proposals,
        EvaluationResponses = evaluations,
        ObservedGenerations = source.Observed,
        Resources = resources,
        Pipeline = engine.PipelineReport
    };
    return new Execution(evidence, result?.StateHash, logical, source, task, valid);
}

internal sealed record ResponseTape(ProposalResponse[] Proposals, EvaluationResponse[] Evaluations);
internal sealed record Execution(object Evidence, string? StateHash, string LogicalEvidence, FixtureProposalSource Source, FixtureTask Task, bool Valid);
