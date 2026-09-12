using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;

// Authored controlled ablation, not a competitor or physical timing-noise benchmark.
internal static class ReuseCampaign
{
    private static readonly DateTimeOffset Clock = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private const int Samples = 5;
    private const int Evaluations = 64;
    private static readonly string[] Phases = { "cold", "warm", "force-fresh", "expired" };
    private static readonly string[] Methods = { "AlwaysFresh", "ExistingSamples" };
    private static readonly Dictionary<string, ulong> SeedOffsets = new()
    { ["prior"] = 100000, ["cold"] = 200000, ["warm"] = 300000, ["force-fresh"] = 400000, ["expired"] = 500000 };

    internal static async Task RunAsync(int seedCount, string revision, string directory)
    {
        if (seedCount is not (2 or 32) || !Path.IsPathFullyQualified(directory) || Directory.Exists(directory) || File.Exists(directory))
            throw new ArgumentException("--campaign <2-smoke|32-primary> <full-revision|working-tree-smoke> <absolute-new-directory>");
        string? built = typeof(EvolutionEngine<>).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!(seedCount == 2 && revision == "working-tree-smoke") &&
            (revision.Length != 40 || revision.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) || built?.EndsWith("+" + revision, StringComparison.Ordinal) != true))
            throw new ArgumentException("A primary campaign requires a full built source revision.");
        string root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        Write(Path.Combine(root, "plan.json"), new
        {
            SchemaVersion = 1,
            Protocol = "noisy-reuse-authored-development-v3",
            SourceRevision = revision,
            SeedCount = seedCount,
            Tasks = new[] { "Quadratic", "Rippled" },
            Phases,
            Methods,
            Executions = new[] { "primary", "replay" },
            AggregateEvaluationCap = Evaluations,
            SamplesPerAggregate = Samples,
            PhysicalObservationCap = Evaluations * Samples,
            ProposalCallCap = 256,
            PriorAggregates = 16,
            ImportedSeeds = 8,
            MaximumAgeMinutes = 60,
            WarmAgeMinutes = 1,
            ExpiredAgeMinutes = 121,
            Clock,
            PhaseSeedOffsets = SeedOffsets,
            BootstrapSamples = 10000,
            BootstrapSeed = 20260911,
            ConfirmationAggregates = 5,
            ConfirmationSamples = 25,
            ConfirmationSeedOffset = 1000000,
            PrimaryEndpoint = "Per-seed mean across two tasks of warm AlwaysFresh minus ExistingSamples physical observation count; a positive difference is saved work, not independent replication or quality superiority.",
            QualityEndpoint = "Descriptive independent-stream 25-observation confirmation of each selected winner, charged separately and never returned to search. Authored scalar objectives with procedural random noise, not representative held-out tasks or physical timing noise.",
            PriorPolicy = "Each method gets byte-identical repertoire and prior cache records in every non-cold phase. AlwaysFresh is an explicit no-reuse ablation, not a competitor. Prior acquisition is executed once per task/seed/execution, attributed in full to each hypothetical warm system but not double-counted as physical campaign work. Cold gets no prior.",
            ReplayPolicy = "Phase-specific streams obtain fresh procedural observations; controls within each phase use common streams. Replay executes all acquisition and search work again, with distinct physical sample IDs; identical replay streams test reproducibility, not independent statistical power.",
            AccountingBoundary = "cost_units counts scalar observations (5 per fresh aggregate); cache_store_invocations and validation_calls are logical calls, not CPU/IO/money. Prior cache-copy files and raw evidence read/write invocations are reported separately. Import/export validate seeds and never import fitness. No run-local memo may bypass freshness checks.",
            CoreInformationalVersion = built,
            WorkerSha256 = HashFile(typeof(ReuseCampaign).Assembly.Location),
            CoreSha256 = HashFile(typeof(EvolutionEngine<>).Assembly.Location)
        });
        foreach (string execution in new[] { "primary", "replay" })
            foreach (string objective in new[] { "Quadratic", "Rippled" })
                for (int seed = 0; seed < seedCount; seed++)
                {
                    string group = Path.Combine(root, execution, objective, seed.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(group);
                    string raw = Path.Combine(group, "raw"); Directory.CreateDirectory(raw);
                    var random = StableRandom.CreateStream((ulong)seed, 7301);
                    int[] priorInputs = Enumerable.Range(0, 16).Select(index => index * 4096 + random.NextInt(4096)).ToArray();
                    var prior = await RunCase(group, raw, execution + "/" + objective + "/" + seed + "/prior", objective, seed,
                        "prior", "ExistingSamples", priorInputs, null, 16, Clock, 0, null);
                    if (prior.Result.Islands[0].Count != 8 || prior.Task.Observations != 16 * Samples)
                        throw new InvalidOperationException("Prior construction must measure every stratified input and retain eight cells.");
                    string priorFile = Path.Combine(group, "prior.json");
                    var repertoire = await EvolutionRepertoire.ExportAsync(prior.Result.Islands[0].Entries.Select(e => e.Candidate.CanonicalGenome.Genome),
                        prior.Task.Inner, new Codec(), prior.Task.Inner.Scope,
                        new EvolutionRepertoireProvenance(prior.Task.RunId, prior.Result.StateHash, HashFile(priorFile), Clock, 16 * Samples, "scalar-observations-v1"));
                    string repertoireJson = repertoire.ToJson();
                    WriteText(Path.Combine(group, "repertoire.json"), repertoireJson);
                    int[] coldInputs = Enumerable.Range(0, 8).Select(index => index * 8192 + random.NextInt(8192)).ToArray();
                    foreach (string phase in Phases)
                        foreach (string method in (seed + (execution == "replay" ? 1 : 0)) % 2 == 0 ? Methods : Methods.Reverse())
                        {
                            var currentTask = new NoisyTask(objective, "import-validation", Clock, raw);
                            EvolutionRepertoireImport<int>? imported = phase == "cold" ? null : await EvolutionRepertoire.FromJson(repertoireJson)
                                .ImportAsync(currentTask, new Codec(), currentTask.Scope);
                            if (imported is not null && (!imported.IsExactScopeMatch || imported.RejectedCount != 0 || imported.Seeds.Count != 8 || currentTask.Observations != 0))
                                throw new InvalidOperationException("Import must validate eight equivalent seeds without importing measurements.");
                            int[] initial = imported is null ? coldInputs : imported.Seeds.Select(item => item.Genome).ToArray();
                            await RunCase(group, raw, execution + "/" + objective + "/" + seed + "/" + phase + "/" + method,
                                objective, seed, phase, method, initial, imported is null ? null : prior.CachePath, Evaluations,
                                Clock.AddMinutes(phase == "expired" ? 121 : 1), imported?.SourceProvenance.PriorCostUnits ?? 0,
                                imported is null ? null : EvolutionHash.Compute(repertoireJson));
                        }
                    Console.WriteLine($"{execution}: {objective} seed {seed} retained");
                }
        Console.WriteLine("All scheduled reuse cases retained. Run the independent analysis before interpreting or accepting the campaign.");
    }

    private static async Task<(EvolutionRunResult<int> Result, CachedTask Task, string CachePath)> RunCase(string group, string raw,
        string runId, string objective, int seed, string phase, string method, int[] initial, string? priorCache, int budget,
        DateTimeOffset now, double priorCost, string? repertoireHash)
    {
        string name = phase == "prior" ? "prior" : phase + "-" + method;
        string cachePath = Path.Combine(group, name + "-cache"); Directory.CreateDirectory(cachePath);
        int copied = 0;
        var priorFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (priorCache is not null)
            foreach (string file in Directory.EnumerateFiles(priorCache, "*.json").OrderBy(path => path, StringComparer.Ordinal))
            {
                if (++copied > 16) throw new InvalidDataException("Unexpected prior cache size.");
                File.Copy(file, Path.Combine(cachePath, Path.GetFileName(file)), overwrite: false);
                priorFiles.Add(Path.GetFileName(file), HashFile(Path.Combine(cachePath, Path.GetFileName(file))));
            }
        var ledger = new EvolutionResourceLedger(runId, new EvolutionResources(new Dictionary<string, decimal>
        {
            ["cost_units"] = budget * Samples,
            ["proposal_calls"] = 256,
            ["validation_calls"] = 256,
            [EvolutionPersistentEvaluationCache.StoreInvocationResource] = 512
        }));
        var task = new CachedTask(new NoisyTask(objective, runId, now, raw), ledger,
            new DirectoryEvolutionEvaluationStore(cachePath, maximumEntries: 256), method == "AlwaysFresh", phase == "force-fresh", now, raw);
        var progress = new Progress();
        var engine = new EvolutionEngine<int>(task, new UniformVariation(ledger),
            _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("x", 0, 65536, 8) }),
            new EvolutionEngineOptions
            {
                RunId = runId,
                Seed = (ulong)seed + SeedOffsets[phase],
                MaxGenerations = phase == "prior" ? 0 : 256,
                MaxEvaluationAttempts = budget,
                MaxProposals = phase == "prior" ? 16 : 256,
                ProposalBatchSize = 1,
                MaxDegreeOfParallelism = 1,
                InspirationCount = 0,
                EnableEvaluationCache = false
            }, observer: progress);
        var result = await engine.RunAsync(initial);
        var resources = ledger.Snapshot();
        bool valid = result.Best is not null && resources.Unknown == 0 && !resources.MaximumViolated &&
            resources.Reserved.Values.All(value => value == 0) && resources.Spent["cost_units"] == task.Observations &&
            task.Observations <= budget * Samples && progress.Results.Sum(row => row.CostUnits) == task.Observations;
        object? confirmation = null;
        if (phase != "prior" && result.Best is not null)
        {
            var confirmTask = new NoisyTask(objective, runId + "/confirmation", now.AddSeconds(1), raw);
            var confirmLedger = new EvolutionResourceLedger(runId + "/confirmation", EvolutionResources.Of("cost_units", 25));
            var metered = new ResourceMeteredEvolutionTask<int>(confirmTask, confirmLedger, new[] { (decimal)Samples });
            var rows = new List<object>();
            var values = new List<double>();
            for (int index = 0; index < 5; index++)
            {
                var candidate = new EvolutionCandidate<int>(index, result.Best.Candidate.CanonicalGenome, result.Best.Candidate.Lineage);
                var context = new EvolutionEvaluationContext(index, (ulong)seed + SeedOffsets[phase] + 1000000, (ulong)(990001 + index), 1);
                var measured = await metered.EvaluateAsync(candidate, context);
                if (measured.Status != EvolutionEvaluationStatus.Completed) { valid = false; break; }
                values.AddRange(confirmTask.LastValues);
                rows.Add(new
                {
                    EvaluationId = index,
                    Genome = candidate.CanonicalGenome.Genome,
                    Status = measured.Status,
                    measured.Quality,
                    measured.CostUnits,
                    Origin = measured.MeasurementOrigin?.ToJson(),
                    EvidenceSha256 = confirmTask.LastEvidence
                });
            }
            var confirmResources = confirmLedger.Snapshot();
            valid &= confirmTask.Observations == 25 && confirmResources.Spent["cost_units"] == 25 && confirmResources.Unknown == 0;
            double? mean = values.Count == 25 ? values.Average() : null;
            double? standardError = mean.HasValue ? Math.Sqrt(values.Sum(value => Math.Pow(value - mean.Value, 2)) / (25 * 24)) : null;
            confirmation = new { PhysicalObservations = confirmTask.Observations, Mean = mean, StandardError = standardError, Rows = rows, Resources = confirmResources };
        }
        Write(Path.Combine(group, name + ".json"), new
        {
            SchemaVersion = 1,
            RunId = runId,
            Objective = objective,
            Seed = seed,
            Phase = phase,
            Method = method,
            Valid = valid,
            ScopeKey = task.Inner.Scope.StableKey,
            InitialGenomes = initial,
            InitialHash = EvolutionHash.Combine(initial.Select(x => x.ToString(CultureInfo.InvariantCulture))),
            RepertoireSha256 = repertoireHash,
            PriorCostAttributed = priorCost,
            CopiedPriorRecords = copied,
            PriorCacheFiles = priorFiles,
            CurrentPhysicalObservations = task.Observations,
            FreshAggregates = task.Inner.Calls,
            task.CacheHits,
            OriginalSampleReferences = task.OriginalSampleReferences,
            task.RawReads,
            RawWrites = task.Inner.Calls,
            BestGenome = result.Best?.Candidate.CanonicalGenome.Genome,
            BestObservedQuality = result.Best?.Evaluation.Quality,
            result.Counters,
            result.StateHash,
            Resources = resources,
            Decisions = task.Decisions,
            Trace = progress.Results,
            Confirmation = confirmation
        });
        if (!valid) throw new InvalidOperationException("Reuse campaign accounting failed; retained the failing case.");
        return (result, task, cachePath);
    }

    private sealed class NoisyTask(string objective, string runId, DateTimeOffset now, string raw) : IEvolutionTask<int>
    {
        public string Id => "authored-" + objective;
        public string VersionHash => "scalar-16bit-objective-v1";
        public string EvaluatorVersionHash => "five-procedural-noise-observations-v1";
        internal string RunId => runId;
        internal int Calls { get; private set; }
        internal int Observations => Calls * Samples;
        internal string? LastEvidence { get; private set; }
        internal IReadOnlyList<double> LastValues { get; private set; } = Array.Empty<double>();
        internal EvolutionReuseScope Scope => new(Id, VersionHash, EvaluatorVersionHash, new Codec().Id, new Codec().VersionHash,
            "integer-0-65535-v1", "authored-development-v1", "five-samples-v1", "no-compile-v1", "numeric-v1", "hardware-independent-formula-v1", "finite-range-v1");
        public ValueTask<EvolutionCanonicalGenome<int>> CanonicalizeAsync(int genome, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (genome is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(genome));
            return new(new EvolutionCanonicalGenome<int>(genome, genome.ToString(CultureInfo.InvariantCulture)));
        }
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<int> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            double x = candidate.CanonicalGenome.Genome / 65535d;
            double expected = -Math.Pow(x - 0.63, 2) - (objective == "Rippled" ? 0.025 * (1 - Math.Cos(28 * x)) : 0);
            var random = context.CreateRandom();
            double[] values = Enumerable.Range(0, Samples).Select(_ => expected + 0.04 * (random.NextDouble() - 0.5)).ToArray();
            LastValues = values;
            double mean = values.Average();
            double error = Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2)) / (Samples * (Samples - 1)));
            string[] ids = Enumerable.Range(0, Samples).Select(index => runId + "/" + context.EvaluationId + "/" + index).ToArray();
            var origin = new EvolutionMeasurementOrigin(Scope.StableKey, runId, context.EvaluationId.ToString(CultureInfo.InvariantCulture),
                ids, now, Samples, "scalar-observations-v1", "arithmetic-mean-sample-se-v1", standardError: error);
            string evidence = JsonSerializer.Serialize(new
            {
                Genome = candidate.CanonicalGenome.Genome,
                ScopeKey = Scope.StableKey,
                context.RootSeed,
                context.SeedStream,
                Values = values,
                Origin = origin.ToJson(),
                Mean = mean,
                StandardError = error
            });
            LastEvidence = EvolutionHash.Compute(evidence);
            WriteText(Path.Combine(raw, LastEvidence + ".json"), evidence);
            return new(EvolutionTaskResult.Completed(mean, new Dictionary<string, double> { ["x"] = candidate.CanonicalGenome.Genome }, costUnits: Samples).WithMeasurementOrigin(origin));
        }
    }

    private sealed class CachedTask : IEvolutionTask<int>
    {
        private readonly EvolutionPersistentEvaluationCache _cache;
        private readonly ResourceMeteredEvolutionTask<int> _metered;
        private readonly EvolutionResourceLedger _ledger;
        private readonly bool _disabled, _forceFresh;
        private readonly DateTimeOffset _now;
        private readonly string _raw;
        internal CachedTask(NoisyTask inner, EvolutionResourceLedger ledger, IEvolutionEvaluationStore store, bool disabled, bool forceFresh, DateTimeOffset now, string raw)
        {
            Inner = inner; _ledger = ledger; _disabled = disabled; _forceFresh = forceFresh; _now = now; _raw = raw;
            _metered = new ResourceMeteredEvolutionTask<int>(inner, ledger, new[] { (decimal)Samples });
            _cache = new EvolutionPersistentEvaluationCache(store, new EvolutionEvaluationReusePolicy(disabled ? EvolutionEvaluationReuseMode.Disabled :
                EvolutionEvaluationReuseMode.ExistingSamples, TimeSpan.FromHours(1), Samples), ledger);
        }
        internal NoisyTask Inner { get; }
        internal string RunId => Inner.RunId;
        internal int Observations => Inner.Observations;
        internal int CacheHits { get; private set; }
        internal int RawReads { get; private set; }
        internal int OriginalSampleReferences { get; private set; }
        internal List<object> Decisions { get; } = new();
        public string Id => "reuse-campaign-task";
        public string VersionHash => EvolutionHash.Combine(new[] { Id, Inner.Scope.StableKey, _cache.VersionHash, _forceFresh.ToString() });
        public string EvaluatorVersionHash => VersionHash;
        public ValueTask<EvolutionCanonicalGenome<int>> CanonicalizeAsync(int genome, CancellationToken cancellationToken = default) => Inner.CanonicalizeAsync(genome, cancellationToken);
        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<int> candidate, EvolutionEvaluationContext context, CancellationToken cancellationToken = default)
        {
            string operation = context.EvaluationId.ToString(CultureInfo.InvariantCulture);
            var one = EvolutionResources.Of("validation_calls", 1);
            var key = await EvolutionResourceWork.RunAsync(_ledger, "key/" + operation, EvolutionResourceStage.Screening, one, one,
                async token => new EvolutionResourceResult<EvolutionEvaluationCacheKey>(await EvolutionEvaluationCacheKey.CreateAsync(candidate.CanonicalGenome,
                    Inner, new Codec(), Inner.Scope, "five-mean-se-v1", token), one), cancellationToken: cancellationToken);
            var lookup = await _cache.LookupAsync(key, operation, _now, _forceFresh, cancellationToken);
            if (lookup.ReusedResult is { } reused)
            {
                RawReads++;
                string evidence = File.ReadAllText(Path.Combine(_raw, lookup.EvidenceSha256 + ".json"), Encoding.UTF8);
                using var document = JsonDocument.Parse(evidence);
                var original = EvolutionMeasurementOrigin.FromJson(document.RootElement.GetProperty("Origin").GetString()!);
                if (EvolutionHash.Compute(evidence) != lookup.EvidenceSha256 || document.RootElement.GetProperty("Genome").GetInt32() != candidate.CanonicalGenome.Genome ||
                    original.AsReused(EvolutionMeasurementOriginKind.PersistentReuse).ToJson() != reused.MeasurementOrigin!.ToJson() ||
                    document.RootElement.GetProperty("Mean").GetDouble() != reused.Quality)
                    throw new InvalidDataException("Authored raw evidence mismatch.");
                CacheHits++; OriginalSampleReferences += original.SampleCount;
                Decisions.Add(new { EvaluationId = context.EvaluationId, Decision = lookup.Decision, EvidenceSha256 = lookup.EvidenceSha256, Origin = reused.MeasurementOrigin.ToJson() });
                return reused;
            }
            var measured = await _metered.EvaluateAsync(candidate, context, cancellationToken);
            if (!_disabled && measured.Status == EvolutionEvaluationStatus.Completed)
                await _cache.TryStoreAsync(new EvolutionEvaluationCacheRecord(key, measured, Inner.LastEvidence!), operation, cancellationToken);
            Decisions.Add(new { EvaluationId = context.EvaluationId, Decision = lookup.Decision, EvidenceSha256 = Inner.LastEvidence, Origin = measured.MeasurementOrigin?.ToJson() });
            return measured;
        }
    }

    private sealed class UniformVariation(EvolutionResourceLedger ledger) : IVariationOperator<int>
    {
        private int _calls;
        public string Id => "uniform-16bit";
        public string VersionHash => "v1";
        public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default)
        {
            var one = EvolutionResources.Of("proposal_calls", 1);
            return EvolutionResourceWork.RunAsync<int>(ledger, "proposal/" + _calls++, EvolutionResourceStage.Proposal, one, one,
                _ => new(new EvolutionResourceResult<int>(context.Random.NextInt(65536), one)), cancellationToken: cancellationToken);
        }
    }
    private sealed class Codec : IEvolutionGenomeCodec<int>
    {
        public string Id => "bounded-integer";
        public string VersionHash => "v1";
        public string Serialize(int genome) => genome.ToString(CultureInfo.InvariantCulture);
        public int Deserialize(string payload) => int.Parse(payload, CultureInfo.InvariantCulture);
    }
    private sealed record TraceRow(long EvaluationId, int Genome, EvolutionEvaluationStatus Status, double? Quality, double CostUnits, string? Origin);
    private sealed class Progress : IEvolutionObserver<int>
    {
        internal List<TraceRow> Results { get; } = new();
        public ValueTask OnEventAsync(EvolutionEvent<int> item, CancellationToken cancellationToken = default)
        {
            if (item.Kind == EvolutionEventKind.Evaluated && item.Evaluation is { } result)
                Results.Add(new TraceRow(result.EvaluationId, item.Candidate!.CanonicalGenome.Genome, result.Status, result.Quality, result.Cost.CostUnits, result.MeasurementOrigin?.ToJson()));
            return default;
        }
    }
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void Write(string path, object value) => WriteText(path, JsonSerializer.Serialize(value, Json));
    private static void WriteText(string path, string text)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(text); file.Write(bytes); file.Flush(true);
    }
}
