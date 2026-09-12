using System.Globalization;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;

if (args.Length == 4 && args[0] == "--campaign")
{
    await ReuseCampaign.RunAsync(int.Parse(args[1], CultureInfo.InvariantCulture), args[2], args[3]);
    return;
}

if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
    throw new ArgumentException("Supply one absolute, new output directory.");
string root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The output directory must not already exist.");
Directory.CreateDirectory(root);
int[] seeds = Enumerable.Range(0, 8).ToArray();
DateTimeOffset observed = DateTimeOffset.UtcNow;
var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
WriteNew(Path.Combine(root, "plan.json"), new
{
    SchemaVersion = 1,
    Fixture = "authored-quadratic-v1",
    Seeds = seeds,
    Phases = new[] { "cold", "warm", "force-fresh" },
    ObservationTime = observed,
    ExpectedEvaluatorCalls = new[] { 8, 0, 8 },
    ExpectedStoreInvocations = new[] { 16, 8, 8 },
    Units = "cost_units counts fixture evaluator calls; cache_store_invocations counts logical store calls; validation_calls counts key-factory invocations",
    Boundary = "Deterministic cache integration demonstration, not a stochastic freshness proof or competitor superiority benchmark."
});
var phases = new List<object>();
string[] originalSampleIds = Array.Empty<string>();
foreach (string phase in new[] { "cold", "warm", "force-fresh" })
{
    bool forceFresh = phase == "force-fresh";
    // Reopen the store for every phase. Nothing carries records through an in-memory result cache.
    var store = new DirectoryEvolutionEvaluationStore(Path.Combine(root, "cache"), maximumEntries: 8);
    var ledger = new EvolutionResourceLedger(phase, new EvolutionResources(new Dictionary<string, decimal>
    {
        ["cost_units"] = 8,
        [EvolutionPersistentEvaluationCache.StoreInvocationResource] = 16,
        ["validation_calls"] = 8
    }));
    var cache = new EvolutionPersistentEvaluationCache(store,
        new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Deterministic, TimeSpan.FromHours(1)), ledger);
    var task = new CachedFixtureTask(cache, ledger, phase, observed.AddMinutes(forceFresh ? 1 : 0), forceFresh,
        Path.Combine(root, "evidence"));
    string tracePath = Path.Combine(root, phase + ".jsonl");
    using var trace = new EvolutionTraceObserver<int>(new EvolutionTraceOptions { Enabled = true, Path = tracePath }, phase);
    var options = new EvolutionEngineOptions
    {
        RunId = phase,
        MaxGenerations = 0,
        MaxEvaluationAttempts = 8,
        MaxProposals = 8,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        EnableEvaluationCache = false // An outer memo must not bypass freshness or force-fresh checks in the task.
    };
    var engine = new EvolutionEngine<int>(task, new FixtureVariation(),
        _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("x", 0, 8, 8) }), options, observer: trace);
    EvolutionRunResult<int> result = await engine.RunAsync(seeds);
    trace.Dispose();
    var records = EvolutionTraceFile.Read(tracePath);
    var snapshot = ledger.Snapshot();
    string[] sampleIds = records.Records.SelectMany(record => record.MeasurementOrigin!.SampleIds).ToArray();
    if (phase == "cold") originalSampleIds = sampleIds;
    if (phase == "warm" && !originalSampleIds.SequenceEqual(sampleIds)) throw new InvalidOperationException("Reuse changed original sample identity.");
    if (forceFresh && originalSampleIds.Intersect(sampleIds, StringComparer.Ordinal).Any()) throw new InvalidOperationException("Fresh work repeated original sample identity.");
    int expectedCalls = phase == "warm" ? 0 : 8;
    int expectedStores = phase == "cold" ? 16 : 8;
    if (task.Calls != expectedCalls || snapshot.Spent["cost_units"] != expectedCalls ||
        snapshot.Spent[EvolutionPersistentEvaluationCache.StoreInvocationResource] != expectedStores ||
        snapshot.Spent["validation_calls"] != 8 || snapshot.Unknown != 0 || result.Best?.Candidate.CanonicalGenome.Genome != 3 ||
        !records.IsComplete || records.Records.Count != 8 || records.Records.Any(record => record.MeasurementOrigin is null) ||
        records.Records.Any(record => record.MeasurementOrigin!.Kind != (phase == "warm" ? EvolutionMeasurementOriginKind.PersistentReuse : EvolutionMeasurementOriginKind.Measured)))
        throw new InvalidOperationException("Persistent evaluation integration or accounting invariant failed.");
    phases.Add(new
    {
        Phase = phase,
        ActualEvaluatorCalls = task.Calls,
        EngineEvaluationAttempts = result.Counters.EvaluationAttempts,
        result.StateHash,
        BestGenome = result.Best.Candidate.CanonicalGenome.Genome,
        BestQuality = result.Best.Evaluation.Quality,
        Ledger = snapshot,
        Decisions = task.Decisions.ToArray(),
        WriteOutcomes = task.Writes.ToArray(),
        OriginalSampleIds = sampleIds
    });
}
WriteNew(Path.Combine(root, "report.json"), new { SchemaVersion = 1, Phases = phases });
Console.WriteLine("cold/warm/force-fresh: evaluator calls 8/0/8; store invocations 16/8/8; validation calls 8/8/8; all checks passed.");

void WriteNew(string path, object value)
{
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    JsonSerializer.Serialize(stream, value, jsonOptions); stream.Flush(true);
}

internal sealed class CachedFixtureTask : IEvolutionTask<int>
{
    private readonly FixtureTask _inner = new();
    private readonly FixtureCodec _codec = new();
    private readonly EvolutionReuseScope _scope;
    private readonly EvolutionPersistentEvaluationCache _cache;
    private readonly EvolutionResourceLedger _ledger;
    private readonly ResourceMeteredEvolutionTask<int> _metered;
    private readonly string _runId, _evidenceDirectory;
    private readonly DateTimeOffset _observed;
    private readonly bool _forceFresh;
    internal CachedFixtureTask(EvolutionPersistentEvaluationCache cache, EvolutionResourceLedger ledger, string runId,
        DateTimeOffset observed, bool forceFresh, string evidenceDirectory)
    {
        _cache = cache; _ledger = ledger; _runId = runId; _observed = observed; _forceFresh = forceFresh; _evidenceDirectory = evidenceDirectory;
        _metered = new ResourceMeteredEvolutionTask<int>(_inner, ledger, new[] { 1m });
        _scope = new EvolutionReuseScope(_inner.Id, _inner.VersionHash, _inner.EvaluatorVersionHash, _codec.Id, _codec.VersionHash,
            "integers-0-through-7-v1", "authored-seeds-v1", "full-v1", "fixture-no-compile-v1", "fixture-runtime-v1", "fixture-hardware-independent-v1", "exact-quadratic-v1");
        Directory.CreateDirectory(evidenceDirectory);
    }
    public string Id => "persistent-fixture";
    public string VersionHash => EvolutionHash.Combine(new[] { Id, _scope.StableKey, _cache.VersionHash, _forceFresh.ToString() });
    public string EvaluatorVersionHash => VersionHash;
    internal int Calls => _inner.Calls;
    internal List<EvolutionEvaluationReuseDecision> Decisions { get; } = new();
    internal List<EvolutionEvaluationCacheWriteStatus> Writes { get; } = new();
    public ValueTask<EvolutionCanonicalGenome<int>> CanonicalizeAsync(int genome, CancellationToken cancellationToken = default) => _inner.CanonicalizeAsync(genome, cancellationToken);
    public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<int> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        string operation = _runId + "/" + context.EvaluationId.ToString(CultureInfo.InvariantCulture);
        var validation = EvolutionResources.Of("validation_calls", 1);
        EvolutionEvaluationCacheKey key = await EvolutionResourceWork.RunAsync(_ledger, operation + "/key", EvolutionResourceStage.Screening,
            validation, validation, async token => new EvolutionResourceResult<EvolutionEvaluationCacheKey>(
                await EvolutionEvaluationCacheKey.CreateAsync(candidate.CanonicalGenome, _inner, _codec, _scope, "one-exact-observation-v1", token), validation),
            cancellationToken: cancellationToken);
        EvolutionEvaluationCacheLookup lookup = await _cache.LookupAsync(key, operation, _observed, _forceFresh, cancellationToken);
        Decisions.Add(lookup.Decision);
        if (lookup.ReusedResult is not null) return lookup.ReusedResult;
        EvolutionTaskResult measured = await _metered.EvaluateAsync(candidate, context, cancellationToken);
        if (measured.Status != EvolutionEvaluationStatus.Completed) return measured;
        string evidence = JsonSerializer.Serialize(new
        {
            Fixture = "authored-quadratic-v1",
            Genome = candidate.CanonicalGenome.Genome,
            measured.Quality,
            OriginalOperation = operation,
            ObservedAt = _observed
        });
        string digest = EvolutionHash.Compute(evidence);
        // Write-once raw authored evidence; these local writes are part of the fixture's declared evaluation operation,
        // not a separately measured filesystem-time/byte resource claim.
        using (var file = new FileStream(Path.Combine(_evidenceDirectory, digest + ".json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(evidence); file.Write(bytes, 0, bytes.Length); file.Flush(true);
        }
        measured = measured.WithMeasurementOrigin(new EvolutionMeasurementOrigin(_scope.StableKey, _runId,
            context.EvaluationId.ToString(CultureInfo.InvariantCulture), new[] { operation }, _observed, measured.CostUnits, "fixture-evaluations-v1", "exact-single-v1"));
        Writes.Add(await _cache.TryStoreAsync(new EvolutionEvaluationCacheRecord(key, measured, digest), operation, cancellationToken));
        return measured;
    }
}

internal sealed class FixtureTask : IEvolutionTask<int>
{
    public string Id => "authored-quadratic";
    public string VersionHash => "task-v1";
    public string EvaluatorVersionHash => "exact-v1";
    internal int Calls { get; private set; }
    public ValueTask<EvolutionCanonicalGenome<int>> CanonicalizeAsync(int genome, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (genome < 0 || genome > 7) throw new ArgumentOutOfRangeException(nameof(genome));
        return new(new EvolutionCanonicalGenome<int>(genome, genome.ToString(CultureInfo.InvariantCulture)));
    }
    public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<int> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; int x = candidate.CanonicalGenome.Genome;
        return new(EvolutionTaskResult.Completed(-(x - 3) * (x - 3), new Dictionary<string, double> { ["x"] = x }, costUnits: 1));
    }
}
internal sealed class FixtureCodec : IEvolutionGenomeCodec<int>
{
    public string Id => "integer";
    public string VersionHash => "integer-v1";
    public string Serialize(int genome) => genome.ToString(CultureInfo.InvariantCulture);
    public int Deserialize(string payload) => int.Parse(payload, CultureInfo.InvariantCulture);
}
internal sealed class FixtureVariation : IVariationOperator<int>
{
    public string Id => "unused-seed-only-variation";
    public string VersionHash => "v1";
    public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default) => new(0);
}
