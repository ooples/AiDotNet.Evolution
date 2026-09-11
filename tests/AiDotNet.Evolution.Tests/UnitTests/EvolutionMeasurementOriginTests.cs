using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionMeasurementOriginTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(-4));

    [Fact]
    public void Original_samples_are_owned_order_independent_and_never_reset_by_reuse()
    {
        var ids = new[] { "b", "a" };
        var origin = Origin(ids);
        ids[0] = "mutated";
        Assert.Equal(new[] { "b", "a" }, origin.SampleIds);
        Assert.Equal(2, origin.SampleCount);
        Assert.Equal(Origin(new[] { "a", "b" }).SampleSetHash, origin.SampleSetHash);
        Assert.Equal(TimeSpan.Zero, origin.ObservedAt.Offset);
        foreach (var kind in new[] { EvolutionMeasurementOriginKind.PersistentReuse,
                     EvolutionMeasurementOriginKind.RunLocalReuse, EvolutionMeasurementOriginKind.MigrationCopy })
        {
            var copy = EvolutionMeasurementOrigin.FromJson(origin.AsReused(kind).ToJson());
            Assert.Equal(kind, copy.Kind);
            Assert.Equal(origin.SampleSetHash, copy.SampleSetHash);
            Assert.Equal(origin.SampleIds, copy.SampleIds);
            Assert.Equal(origin.ObservedAt, copy.ObservedAt);
            Assert.Equal(12.5, copy.OriginalCostUnits);
            Assert.Equal(0.1, copy.StandardError);
            Assert.Equal(0.8, copy.LowerConfidenceBound);
            Assert.Equal(1.2, copy.UpperConfidenceBound);
            Assert.Equal(0.95, copy.ConfidenceLevel);
            Assert.Throws<ArgumentException>(() => copy.AsReused(EvolutionMeasurementOriginKind.Measured));
        }
        Assert.Equal(origin.ToJson(), EvolutionMeasurementOrigin.FromJson(origin.ToJson()).ToJson());
        Assert.Throws<ArgumentOutOfRangeException>(() => origin.AsReused((EvolutionMeasurementOriginKind)99));
    }

    [Theory]
    [InlineData("ScopeKey", "bad")]
    [InlineData("SourceRunId", " ")]
    [InlineData("SourceEvaluationId", "")]
    [InlineData("CostUnit", "bad\nlabel")]
    [InlineData("StatisticsVersion", "")]
    public void Invalid_identity_fields_are_rejected(string field, string value)
    {
        var json = JsonNode.Parse(Origin().ToJson())!.AsObject(); json[field] = value;
        Assert.ThrowsAny<ArgumentException>(() => EvolutionMeasurementOrigin.FromJson(json.ToJsonString()));
    }

    [Theory]
    [InlineData("OriginalCostUnits", -1)]
    [InlineData("StandardError", -1)]
    [InlineData("LowerConfidenceBound", 2)]
    [InlineData("ConfidenceLevel", 0)]
    [InlineData("ConfidenceLevel", 1)]
    [InlineData("Kind", 99)]
    public void Invalid_statistics_are_rejected(string field, int value)
    {
        var json = JsonNode.Parse(Origin().ToJson())!.AsObject(); json[field] = value;
        Assert.ThrowsAny<ArgumentException>(() => EvolutionMeasurementOrigin.FromJson(json.ToJsonString()));
    }

    [Theory]
    [InlineData("LowerConfidenceBound")]
    [InlineData("UpperConfidenceBound")]
    [InlineData("ConfidenceLevel")]
    public void Partial_confidence_interval_is_rejected(string field)
    {
        var json = JsonNode.Parse(Origin().ToJson())!.AsObject(); json[field] = null;
        Assert.Throws<ArgumentException>(() => EvolutionMeasurementOrigin.FromJson(json.ToJsonString()));
    }

    [Fact]
    public void Bounds_and_strict_JSON_reject_ambiguous_or_unbounded_metadata()
    {
        Assert.Throws<ArgumentException>(() => Origin(Array.Empty<string>()));
        Assert.Throws<ArgumentException>(() => Origin(new[] { "a", "a" }));
        Assert.Throws<ArgumentException>(() => Origin(new[] { new string((char)0xd800, 1) }));
        Assert.Throws<ArgumentException>(() => Origin(new[] { new string('x', 257) }));
        Assert.Throws<ArgumentException>(() => Origin(Enumerable.Range(0, 257).Select(n => n.ToString(CultureInfo.InvariantCulture))));
        Assert.Equal(256, Origin(Enumerable.Range(0, 256).Select(n => n.ToString(CultureInfo.InvariantCulture))).SampleCount);
        Assert.Throws<ArgumentNullException>(() => EvolutionMeasurementOrigin.FromJson(null!));
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(new string(' ', EvolutionMeasurementOrigin.MaximumJsonBytes + 1)));
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(new string('\u20ac', EvolutionMeasurementOrigin.MaximumJsonBytes / 2)));
        string json = Origin().ToJson();
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(json.Insert(1, "\"SchemaVersion\":1,")));
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(json.Insert(1, "\"unknown\":1,")));
        var node = JsonNode.Parse(json)!.AsObject(); node.Remove("Kind");
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(node.ToJsonString()));
        node = JsonNode.Parse(json)!.AsObject(); node["SchemaVersion"] = 2;
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(node.ToJsonString()));
        node = JsonNode.Parse(json)!.AsObject(); node["ObservedAt"] = default(DateTimeOffset);
        Assert.Throws<ArgumentOutOfRangeException>(() => EvolutionMeasurementOrigin.FromJson(node.ToJsonString()));
        node = JsonNode.Parse(json)!.AsObject(); node["CostUnit"] = null;
        Assert.Throws<InvalidDataException>(() => EvolutionMeasurementOrigin.FromJson(node.ToJsonString()));
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionMeasurementOrigin(EvolutionHash.Compute("scope"),
                "run", "evaluation", new[] { "sample" }, Timestamp, invalid, "calls-v1", "stats-v1"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionMeasurementOrigin(EvolutionHash.Compute("scope"),
                "run", "evaluation", new[] { "sample" }, Timestamp, 1, "calls-v1", "stats-v1", standardError: invalid));
        }
    }

    [Fact]
    public async Task Cache_and_checkpoint_resume_preserve_samples_without_artifacts_or_duplicate_cost()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "origin.jsonl");
        var store = new InMemoryEvolutionCheckpointStore();
        var firstTask = new OriginTask();
        await Engine(firstTask, Options(1), store).RunAsync(new[] { new TestGenome(1), new TestGenome(1), new TestGenome(2) });
        var checkpoint = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("origin-run"));
        Assert.Equal(7, JsonNode.Parse(checkpoint.Payload)!["SchemaVersion"]!.GetValue<int>());
        var recovered = EvolutionEngine<TestGenome>.ReadCheckpoint(checkpoint, new TestGenomeCodec());
        Assert.NotEmpty(recovered.Entries);
        Assert.All(recovered.Entries, entry => Assert.NotNull(entry.Entry.Evaluation.MeasurementOrigin));

        var resumedTask = new OriginTask(); var options = Options(3); options.Resume = true;
        using (var tracer = new EvolutionTraceObserver<TestGenome>(new EvolutionTraceOptions { Enabled = true, Path = path }, "origin-run"))
            await Engine(resumedTask, options, store, tracer).RunAsync(Array.Empty<TestGenome>());
        Assert.Equal(1, firstTask.Calls); Assert.Equal(1, resumedTask.Calls);
        var trace = EvolutionTraceFile.Read(path); Assert.True(trace.IsComplete);
        var hit = Assert.Single(trace.Records, record => record.CacheStatus == EvolutionCacheStatus.Hit);
        Assert.Equal(2, hit.SchemaVersion);
        Assert.Equal(EvolutionMeasurementOriginKind.RunLocalReuse, hit.MeasurementOrigin!.Kind);
        Assert.Equal(new[] { "sample-0" }, hit.MeasurementOrigin.SampleIds);
        Assert.Equal(0, hit.CostUnits); Assert.Equal(12.5, hit.MeasurementOrigin.OriginalCostUnits);
    }

    [Fact]
    public async Task Default_checkpoints_remain_schema_six_and_origin_changes_affect_state_identity()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        var legacy = await Engine(new OriginTask { IncludeOrigin = false }, Options(2), store).RunAsync(new[] { new TestGenome(1) });
        var checkpoint = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("origin-run"));
        Assert.Equal(6, JsonNode.Parse(checkpoint.Payload)!["SchemaVersion"]!.GetValue<int>());
        Assert.DoesNotContain("MeasurementOrigin", checkpoint.Payload);
        Assert.Null(EvolutionEngine<TestGenome>.ReadCheckpoint(checkpoint, new TestGenomeCodec()).Entries[0].Entry.Evaluation.MeasurementOrigin);
        var measured = await Engine(new OriginTask(), Options(2)).RunAsync(new[] { new TestGenome(1) });
        var again = await Engine(new OriginTask(), Options(2)).RunAsync(new[] { new TestGenome(1) });
        Assert.NotEqual(legacy.StateHash, measured.StateHash); Assert.Equal(measured.StateHash, again.StateHash);
        var evaluation = measured.Best!.Evaluation;
        Assert.Same(evaluation.MeasurementOrigin, evaluation.WithDescriptors(evaluation.Descriptors).MeasurementOrigin);
        var bare = EvolutionTaskResult.Completed(1, new Dictionary<string, double>());
        Assert.Null(bare.MeasurementOrigin); Assert.NotNull(bare.WithMeasurementOrigin(Origin()).MeasurementOrigin);
        Assert.Null(bare.MeasurementOrigin);
        Assert.Throws<ArgumentNullException>(() => bare.WithMeasurementOrigin(null!));
        Assert.Throws<ArgumentNullException>(() => evaluation.WithMeasurementOrigin(null!));
    }

    [Theory]
    [InlineData("cache", false)]
    [InlineData("archive", false)]
    [InlineData("history", false)]
    [InlineData("elite", false)]
    [InlineData("archive", true)]
    public async Task Invalid_origin_is_rejected_before_custom_genome_decode(string location, bool downgrade)
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(new OriginTask(), Options(2), store).RunAsync(new[] { new TestGenome(1) });
        var checkpoint = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("origin-run"));
        var node = JsonNode.Parse(checkpoint.Payload)!;
        JsonNode target = location switch
        {
            "cache" => node["Cache"]![0]!["Result"]!,
            "history" => node["IslandHistories"]![0]![0]!["Evaluation"]!,
            "elite" => node["GlobalElites"]![0]!["Entry"]!["Evaluation"]!,
            _ => node["Islands"]![0]!["Entries"]![0]!["Evaluation"]!
        };
        if (downgrade) node["SchemaVersion"] = 6;
        else target["MeasurementOriginJson"] = "{\"broken\":true}";
        var corrupt = new EvolutionCheckpoint(checkpoint.RunId, checkpoint.Sequence, checkpoint.CompatibilityHash, node.ToJsonString());
        var codec = new CountingCodec();
        Assert.Throws<InvalidDataException>(() => EvolutionEngine<TestGenome>.ReadCheckpoint(corrupt, codec));
        Assert.Equal(0, codec.DecodeCalls);
    }

    [Theory]
    [InlineData(EvolutionTraceFormat.JsonLines, false)]
    [InlineData(EvolutionTraceFormat.JsonLines, true)]
    [InlineData(EvolutionTraceFormat.Json, false)]
    [InlineData(EvolutionTraceFormat.Json, true)]
    public void Trace_formats_round_trip_typed_origin(EvolutionTraceFormat format, bool compress)
    {
        using var directory = new TemporaryDirectory(); string path = Path.Combine(directory.Path, "trace");
        var record = new EvolutionTraceRecord(1, 1, "genome", EvolutionEvaluationStatus.Completed,
            EvolutionOptimizationDirection.Maximize, 0, 0, EvolutionCacheStatus.Miss, Timestamp.ToUniversalTime(), "task", "eval", "config")
        { Quality = 1, MeasurementOrigin = Origin().AsReused(EvolutionMeasurementOriginKind.PersistentReuse), CostUnits = 0.25 };
        EvolutionTraceFile.Write(new[] { record }, path, format, compress);
        var read = EvolutionTraceFile.Read(path); Assert.True(read.IsComplete);
        var restored = Assert.Single(read.Records);
        Assert.Equal(2, restored.SchemaVersion); Assert.Equal(record.MeasurementOrigin.ToJson(), restored.MeasurementOrigin!.ToJson());
        Assert.Equal(0.25, restored.CostUnits);
        var json = EvolutionTraceFile.ToJson(record); json["schemaVersion"] = 1;
        Assert.Throws<InvalidDataException>(() => EvolutionTraceFile.FromJson(json));
        json["schemaVersion"] = 99;
        Assert.Throws<InvalidDataException>(() => EvolutionTraceFile.FromJson(json));
        json["schemaVersion"] = 2; json.Remove("measurementOrigin");
        Assert.Throws<InvalidDataException>(() => EvolutionTraceFile.FromJson(json));
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("reused", false)]
    [InlineData("aggregate", false)]
    [InlineData("duplicate", false)]
    [InlineData("scope-change", false)]
    public async Task Replication_never_counts_declared_copies_as_independent_samples(string mode, bool complete)
    {
        var ledger = new EvolutionResourceLedger("replicate-origin", EvolutionResources.Of("cost_units", 10));
        string? firstId = null;
        var runner = new EvolutionReplicateRunner<TestGenome>("origin-evaluator", new EvolutionReplicationPlan(2, 2, 0, 2, 1), ledger,
            (_, context, _) =>
            {
                firstId ??= context.SampleIdentity;
                string[] ids = mode == "aggregate" ? new[] { context.SampleIdentity, "extra" } :
                    new[] { mode == "duplicate" ? firstId : context.SampleIdentity };
                var origin = Origin(ids);
                if (mode == "reused") origin = origin.AsReused(EvolutionMeasurementOriginKind.PersistentReuse);
                if (mode == "scope-change" && context.Index > 0)
                    origin = new EvolutionMeasurementOrigin(EvolutionHash.Compute("changed"), "run", "evaluation", ids, Timestamp, 1, "calls-v1", "stats-v1");
                return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(1, new Dictionary<string, double>(), costUnits: 0.5)
                    .WithMeasurementOrigin(origin));
            });
        var report = await runner.RunAsync(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(1), "1"),
            new EvolutionEvaluationContext(0, 17, 2, 1), "fresh-batch", EvolutionReplicationPurpose.Confirmation);
        Assert.Equal(complete, report.IsComplete);
        Assert.Equal(complete ? EvolutionReplicationStopReason.Completed : EvolutionReplicationStopReason.InvalidMeasurement, report.StopReason);
        Assert.All(report.Samples, sample => Assert.NotNull(sample.MeasurementOrigin));
        Assert.Equal(report.Samples.Count * 0.5m, ledger.Snapshot().Spent["cost_units"]);
        if (!complete) { Assert.Null(report.MeanQuality); Assert.Null(report.StandardError); Assert.Null(report.LowerBound); }
        else Assert.Equal(2, report.Samples.Select(sample => sample.MeasurementOrigin!.SampleIds[0]).Distinct().Count());
    }

    [Fact]
    public async Task Migration_keeps_original_sample_ids_and_marks_copies()
    {
        var options = Options(8); options.MaxGenerations = 20; options.IslandCount = 2; options.MigrationInterval = 1;
        var store = new InMemoryEvolutionCheckpointStore();
        var result = await Engine(new OriginTask(), options, store).RunAsync(new[] { new TestGenome(1), new TestGenome(5) });
        var origins = result.Islands.SelectMany(island => island.Entries).Select(entry => entry.Evaluation.MeasurementOrigin!).ToArray();
        Assert.Contains(origins, origin => origin.Kind == EvolutionMeasurementOriginKind.MigrationCopy);
        Assert.All(origins, origin => Assert.Equal(1, origin.SampleCount));
        var checkpoint = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("origin-run"));
        var read = EvolutionEngine<TestGenome>.ReadCheckpoint(checkpoint, new TestGenomeCodec());
        Assert.Contains(read.Entries, entry => entry.Entry.Evaluation.MeasurementOrigin?.Kind == EvolutionMeasurementOriginKind.MigrationCopy);
    }

    [Fact]
    public async Task Cascade_fails_closed_with_auditable_cost_and_origin_until_stage_provenance_is_supported()
    {
        var options = Options(1); options.Cascade.Enabled = true; options.Cascade.Thresholds = new[] { 0d };
        var observer = new EvaluationRecordingObserver();
        await Engine(new OriginCascadeTask(), options, observer: observer).RunAsync(new[] { new TestGenome(1) });
        var outcome = Assert.Single(observer.Evaluations);
        Assert.Equal(EvolutionEvaluationStatus.Failed, outcome.Status);
        Assert.Contains(outcome.Diagnostics, diagnostic => diagnostic.Code == "cascade_measurement_origin_unsupported");
        Assert.Equal(2, outcome.Cost.CostUnits); Assert.NotNull(outcome.MeasurementOrigin);
    }

    [Fact]
    public async Task Invalid_metering_receipt_preserves_origin_without_charging_original_cost()
    {
        var ledger = new EvolutionResourceLedger("origin-meter", EvolutionResources.Of("cost_units", 20));
        var task = new ResourceMeteredEvolutionTask<TestGenome>(new OriginCascadeTask(), ledger, new[] { 0.5m, 0.5m });
        var observer = new EvaluationRecordingObserver();
        await Engine(task, Options(1), observer: observer).RunAsync(new[] { new TestGenome(1) });
        var outcome = Assert.Single(observer.Evaluations);
        Assert.Equal(EvolutionEvaluationStatus.Failed, outcome.Status);
        Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(12.5, outcome.MeasurementOrigin!.OriginalCostUnits);
    }

    private sealed class OriginCascadeTask : ICascadeEvolutionTask<TestGenome>
    {
        private readonly OriginTask _inner = new();
        public string Id => _inner.Id;
        public string VersionHash => _inner.VersionHash;
        public string EvaluatorVersionHash => _inner.EvaluatorVersionHash;
        public int StageCount => 2;
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            _inner.CanonicalizeAsync(genome, cancellationToken);
        public ValueTask<EvolutionTaskResult> EvaluateStageAsync(int stage, EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default) => EvaluateAsync(candidate, context, cancellationToken);
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default) => new(EvolutionTaskResult.Completed(1, new Dictionary<string, double> { ["x"] = 1 },
                costUnits: 2).WithMeasurementOrigin(Origin()));
    }

    private static EvolutionMeasurementOrigin Origin(IEnumerable<string>? samples = null) => new(EvolutionHash.Compute("scope"),
        "original-run", "original-evaluation", samples ?? new[] { "sample" }, Timestamp, 12.5, "calls-v1", "stats-v1",
        standardError: 0.1, lowerConfidenceBound: 0.8, upperConfidenceBound: 1.2, confidenceLevel: 0.95);

    private static EvolutionEngineOptions Options(int attempts) => new()
    {
        RunId = "origin-run",
        MaxEvaluationAttempts = attempts,
        MaxProposals = 20,
        MaxGenerations = 0,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        MigrationInterval = 0,
        CheckpointInterval = 0,
        HistorySize = 8,
        GlobalEliteCount = 4
    };

    private static EvolutionEngine<TestGenome> Engine(IEvolutionTask<TestGenome> task, EvolutionEngineOptions options,
        IEvolutionCheckpointStore? store = null, IEvolutionObserver<TestGenome>? observer = null) => new(task,
        new IncrementVariation(), _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 100) }),
        options, checkpointStore: store, genomeCodec: store is null ? null : new TestGenomeCodec(), observer: observer);

    private sealed class OriginTask : IEvolutionTask<TestGenome>
    {
        private readonly SyntheticEvolutionTask _inner = new();
        public string Id => _inner.Id;
        public string VersionHash => _inner.VersionHash;
        public string EvaluatorVersionHash => _inner.EvaluatorVersionHash;
        internal bool IncludeOrigin { get; set; } = true;
        internal int Calls => _inner.Calls;
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            _inner.CanonicalizeAsync(genome, cancellationToken);
        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            var result = await _inner.EvaluateAsync(candidate, context, cancellationToken);
            return IncludeOrigin ? result.WithMeasurementOrigin(Origin(new[] { "sample-" + context.EvaluationId.ToString(CultureInfo.InvariantCulture) })) : result;
        }
    }

    private sealed class CountingCodec : IEvolutionGenomeCodec<TestGenome>
    {
        public string Id => "int";
        public string VersionHash => "int-v1";
        internal int DecodeCalls { get; private set; }
        public string Serialize(TestGenome genome) => genome.Value.ToString(CultureInfo.InvariantCulture);
        public TestGenome Deserialize(string payload) { DecodeCalls++; return new(int.Parse(payload, CultureInfo.InvariantCulture)); }
    }
}
