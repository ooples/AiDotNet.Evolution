using System.Diagnostics;
using System.Text.Json;
using AiDotNet.Evolution;

// V1-50 (#163): the ensemble's own per-proposal cost, measured against the same null models called
// directly. A separate mode rather than a separate project, because it measures the same thing this
// harness exists for and needs the same null-evaluator discipline.
if (args.Length == 4 && args[0] == "ensemble")
{
    if (!int.TryParse(args[1], out int ensembleProposals) || !int.TryParse(args[2], out int ensembleModels) ||
        !int.TryParse(args[3], out int ensembleRepeats) || ensembleProposals is < 64 or > 10_000_000 ||
        ensembleModels is < 1 or > 64 || ensembleRepeats is < 1 or > 100)
    {
        Console.Error.WriteLine("Usage: EvolutionOverhead ensemble <proposals 64..1e7> <models 1..64> <repeats 1..100>");
        return 2;
    }

    return EnsembleOverhead.Run(ensembleProposals, ensembleModels, ensembleRepeats);
}

// E-1: AllocationBudgetTests' exact workload outside a test host, at any length. A 500-evaluation warm-up run, then
// one measured run; prints GC.GetTotalAllocatedBytes per completed evaluation of the measured run.
// Usage: EvolutionOverhead allocation <evaluations>
if (args.Length == 2 && args[0] == "allocation")
{
    if (!int.TryParse(args[1], out int measured) || measured is < 8 or > 1_000_000)
    {
        Console.Error.WriteLine("Usage: EvolutionOverhead allocation <evaluations 8..1e6>");
        return 2;
    }

    return await AllocationBudget.RunAsync(measured);
}

// V1-30: one null-evaluator run per process. Usage: EvolutionOverhead <evaluations> <workers> <islands>
if (args.Length != 3 || !int.TryParse(args[0], out int budget) || !int.TryParse(args[1], out int workers) ||
    !int.TryParse(args[2], out int islands) || budget is < 8 or > 1_000_000 || workers is < 1 or > 64 || islands is < 1 or > 64)
{
    Console.Error.WriteLine("Usage: EvolutionOverhead <evaluations 8..1e6> <workers 1..64> <islands 1..64>");
    return 2;
}
var builder = new EvolutionSearchSpaceBuilder();
for (int i = 0; i < 4; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
EvolutionSearchSpace space = builder.Build();
// A null evaluator: no delay, constant-cost arithmetic, so measured time is orchestration.
var task = new EvolutionSearchTask(space, "overhead", "v1", "null-v1", (genome, _, _) =>
    new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-genome.Values.Values.Sum(v => v.Number * v.Number),
        new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1)));
var engine = new EvolutionEngine<EvolutionSearchGenome>(task, new SearchSpaceRestart(space),
    _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 64) }),
    new EvolutionEngineOptions
    {
        RunId = "overhead", Seed = 42, MaxEvaluationAttempts = budget, MaxProposals = budget * 2, MaxGenerations = budget, ProposalBatchSize = 8,
        MaxDegreeOfParallelism = workers, IslandCount = islands, MigrationInterval = 0, CheckpointInterval = 0
    });
EvolutionSearchGenome[] seeds = Enumerable.Range(0, 8).Select(i => space.Sample(StableRandom.CreateStream(42, (ulong)i))).ToArray();
using var types = Environment.GetEnvironmentVariable("EVOLUTION_ALLOCATION_TYPES") == "1" ? new AllocationListener() : null;
long allocated = GC.GetTotalAllocatedBytes(precise: true);
var clock = Stopwatch.StartNew();
EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(seeds);
clock.Stop();
using var process = Process.GetCurrentProcess();
process.Refresh();
Console.WriteLine(JsonSerializer.Serialize(new
{
    System = "aidotnet", Evaluations = result.Counters.CompletedEvaluations, Workers = workers, Islands = islands,
    Seconds = clock.Elapsed.TotalSeconds, PeakWorkingSetBytes = process.PeakWorkingSet64,
    AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated
}));
types?.Print(budget);
return result.Counters.CompletedEvaluations >= budget ? 0 : 1;

/// <summary>Allocation sampling by type from GC AllocationTick events (about every 100 KB).</summary>
internal sealed class AllocationListener : System.Diagnostics.Tracing.EventListener
{
    private readonly Dictionary<string, long> _bytes = new();

    protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)0x1);
    }

    protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
    {
        if (data.EventName is null || !data.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal) ||
            data.Payload is null || data.PayloadNames is not { } names) return;
        int type = names.IndexOf("TypeName"), amount = names.IndexOf("AllocationAmount64");
        if (type < 0 || amount < 0) return;
        lock (_bytes) _bytes[data.Payload[type] as string ?? "?"] = _bytes.GetValueOrDefault(data.Payload[type] as string ?? "?") +
            Convert.ToInt64(data.Payload[amount], System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Print(int evaluations)
    {
        lock (_bytes)
            foreach (var entry in _bytes.OrderByDescending(pair => pair.Value).Take(16))
                Console.Error.WriteLine($"{entry.Value / evaluations,9} B/eval  {entry.Key}");
    }
}