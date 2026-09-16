using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed partial class EvolutionPersistentEvaluationTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record_round_trip_retains_original_evidence_but_reuse_only_charges_new_work()
    {
        var record = Record(); string json = record.ToJson(); var restored = EvolutionEvaluationCacheRecord.FromJson(json);
        Assert.Equal(json, restored.ToJson()); Assert.Equal(record.Key.StableKey, restored.Key.StableKey);
        Assert.Equal("genome", restored.Key.GenomeId); Assert.Equal("measurements-v1", restored.Key.MeasurementVersion);
        Assert.Equal(1, restored.Quality); Assert.Equal(EvolutionOptimizationDirection.Maximize, restored.Direction);
        var reused = restored.AsReused(0.25);
        Assert.Equal(0.25, reused.CostUnits); Assert.Equal(7, reused.MeasurementOrigin!.OriginalCostUnits);
        Assert.Equal(EvolutionMeasurementOriginKind.PersistentReuse, reused.MeasurementOrigin.Kind);
        Assert.Equal(record.Origin.SampleSetHash, reused.MeasurementOrigin.SampleSetHash);
        Assert.Equal(EvolutionHash.Compute("raw-evidence"), restored.EvidenceSha256);
        Assert.Equal(1, reused.Metrics["metric"]); Assert.Equal(new[] { 1d, 2 }, reused.Objectives);
        Assert.Empty(reused.Diagnostics); Assert.Empty(reused.Artifacts);
        Assert.Throws<ArgumentOutOfRangeException>(() => restored.AsReused(-1));
    }

    [Fact]
    public void Exact_key_changes_for_every_applicability_genome_and_measurement_facet()
    {
        var key = Key(); var parts = key.Scope.CopyParts();
        for (int index = 0; index < parts.Length; index++)
        {
            var changed = (string[])parts.Clone(); changed[index] += "-changed";
            Assert.NotEqual(key.StableKey, new EvolutionEvaluationCacheKey(new EvolutionReuseScope(changed), key.GenomeId,
                key.GenomePayloadSha256, key.MeasurementVersion).StableKey);
        }
        Assert.NotEqual(key.StableKey, Key(genome: "other").StableKey);
        Assert.NotEqual(key.StableKey, Key(payload: "different bytes").StableKey);
        Assert.NotEqual(key.StableKey, Key(measurement: "different-replicate-plan").StableKey);
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheKey(key.Scope, "bad\nidentity", key.GenomePayloadSha256, "v1"));
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheKey(key.Scope, "genome", "bad-hash", "v1"));
        Assert.Throws<ArgumentNullException>(() => new EvolutionEvaluationCacheKey(null!, "genome", key.GenomePayloadSha256, "v1"));
    }

    [Fact]
    public void Records_reject_nonfresh_infeasible_missing_or_wrong_scope_evidence()
    {
        var key = Key(); var bare = EvolutionTaskResult.Completed(1, new Dictionary<string, double>());
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheRecord(key, bare, EvolutionHash.Compute("evidence")));
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheRecord(key, Record().AsReused(), EvolutionHash.Compute("evidence")));
        var failed = EvolutionTaskResult.Failed("failed", "failed").WithMeasurementOrigin(Origin(key));
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheRecord(key, failed, EvolutionHash.Compute("evidence")));
        var violated = new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 1, constraintViolations: new[] { 1d }).WithMeasurementOrigin(Origin(key));
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheRecord(key, violated, EvolutionHash.Compute("evidence")));
        var wrongScope = new EvolutionEvaluationCacheKey(new EvolutionReuseScope(Enumerable.Repeat("other-v1", 12).ToArray()),
            key.GenomeId, key.GenomePayloadSha256, key.MeasurementVersion);
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheRecord(wrongScope, bare.WithMeasurementOrigin(Origin(key)), EvolutionHash.Compute("evidence")));
        var badName = EvolutionTaskResult.Completed(1, new Dictionary<string, double> { [new string('x', 257)] = 1 }).WithMeasurementOrigin(Origin(key));
        Assert.Throws<ArgumentException>(() => new EvolutionEvaluationCacheRecord(key, badName, EvolutionHash.Compute("evidence")));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("scope")]
    [InlineData("values")]
    [InlineData("vector")]
    [InlineData("origin")]
    public void Checksummed_payload_still_requires_valid_bounded_schema(string mode)
    {
        Assert.ThrowsAny<Exception>(() => Mutate(Record(), root =>
        {
            switch (mode)
            {
                case "schema": root["SchemaVersion"] = 2; break;
                case "missing": root.Remove("Quality"); break;
                case "extra": root["Extra"] = true; break;
                case "scope": root["Scope"]!.AsArray().Add("extra"); break;
                case "values": root["Metrics"] = new JsonObject(Enumerable.Range(0, 257).Select(i => new KeyValuePair<string, JsonNode?>("m" + i, JsonValue.Create(1)))); break;
                case "vector": root["Objectives"] = new JsonArray(Enumerable.Range(0, 257).Select(_ => (JsonNode?)JsonValue.Create(1)).ToArray()); break;
                case "origin": root["OriginJson"] = "[]"; break;
            }
        }));
    }

    [Fact]
    public void Corruption_duplicate_fields_and_byte_limits_are_rejected()
    {
        string json = Record().ToJson(); var node = JsonNode.Parse(json)!; node["Checksum"] = EvolutionHash.Compute("wrong");
        Assert.Throws<InvalidDataException>(() => EvolutionEvaluationCacheRecord.FromJson(node.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => EvolutionEvaluationCacheRecord.FromJson(json.Insert(1, "\"Checksum\":\"duplicate\",")));
        node = JsonNode.Parse(json)!; node["Payload"] = null;
        Assert.Throws<InvalidDataException>(() => EvolutionEvaluationCacheRecord.FromJson(node.ToJsonString()));
        Assert.Throws<ArgumentNullException>(() => EvolutionEvaluationCacheRecord.FromJson(null!));
        Assert.Throws<InvalidDataException>(() => EvolutionEvaluationCacheRecord.FromJson(new string(' ', EvolutionEvaluationCacheRecord.MaximumJsonBytes + 1)));
        Assert.Throws<InvalidDataException>(() => EvolutionEvaluationCacheRecord.FromJson(new string('\u20ac', EvolutionEvaluationCacheRecord.MaximumJsonBytes / 2)));
        node = JsonNode.Parse(json)!;
        string payload = node["Payload"]!.GetValue<string>().Replace("\"Metrics\":{\"metric\":1}", "\"Metrics\":{\"metric\":1,\"metric\":2}");
        node["Payload"] = payload; node["Checksum"] = EvolutionHash.Compute(payload);
        Assert.Throws<InvalidDataException>(() => EvolutionEvaluationCacheRecord.FromJson(node.ToJsonString()));
    }

    [Fact]
    public async Task Store_survives_reopening_and_refreshes_newer_evidence_at_capacity()
    {
        using var directory = new TemporaryDirectory(); var store = new DirectoryEvolutionEvaluationStore(directory.Path, 1);
        Assert.Equal(Path.GetFullPath(directory.Path), store.DirectoryPath); Assert.Equal(1, store.MaximumEntries);
        var first = Record(); Assert.Null(await store.ReadAsync(first.Key)); Assert.True(await store.TryWriteAsync(first));
        var reopened = new DirectoryEvolutionEvaluationStore(directory.Path, 1);
        Assert.Equal(first.ToJson(), (await reopened.ReadAsync(first.Key))!.ToJson());
        Assert.False(await reopened.TryWriteAsync(first));
        Assert.False(await reopened.TryWriteAsync(Record(key: Key(genome: "other"))));
        var fresh = Record(sample: "new-sample", when: Observed.AddMinutes(1));
        Assert.True(await reopened.TryWriteAsync(fresh)); Assert.False(await store.TryWriteAsync(first));
        Assert.Equal(fresh.ToJson(), (await store.ReadAsync(first.Key))!.ToJson());
        Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Same_samples_cannot_be_overwritten_with_conflicting_measurements()
    {
        using var directory = new TemporaryDirectory(); var store = new DirectoryEvolutionEvaluationStore(directory.Path);
        var first = Record(); Assert.True(await store.TryWriteAsync(first));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.TryWriteAsync(Record(quality: 2)).AsTask());
        Assert.Equal(first.ToJson(), (await store.ReadAsync(first.Key))!.ToJson());
    }

    [Fact]
    public async Task Shared_root_gate_enforces_capacity_under_concurrent_in_process_writers()
    {
        using var directory = new TemporaryDirectory();
        var writes = Enumerable.Range(0, 16).Select(index => Task.Run(async () =>
            await new DirectoryEvolutionEvaluationStore(directory.Path, 3).TryWriteAsync(Record(key: Key(genome: "g" + index))))).ToArray();
        var results = await Task.WhenAll(writes); Assert.Equal(3, results.Count(value => value));
        Assert.Equal(3, Directory.GetFiles(directory.Path).Length);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("wrong-key")]
    [InlineData("oversized")]
    [InlineData("invalid-utf8")]
    public async Task Unsafe_files_fail_closed_and_are_not_silently_replaced(string mode)
    {
        using var directory = new TemporaryDirectory(); var store = new DirectoryEvolutionEvaluationStore(directory.Path);
        var record = Record(); string path = Path.Combine(directory.Path, record.Key.StableKey + ".json");
        if (mode == "invalid-utf8") File.WriteAllBytes(path, new byte[] { 0xff, 0xfe });
        else File.WriteAllText(path, mode == "wrong-key" ? Record(key: Key(genome: "other")).ToJson() :
            mode == "oversized" ? new string(' ', EvolutionEvaluationCacheRecord.MaximumJsonBytes + 1) : "{ broken", new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(path);
        await Assert.ThrowsAnyAsync<Exception>(() => store.ReadAsync(record.Key).AsTask());
        await Assert.ThrowsAnyAsync<Exception>(() => store.TryWriteAsync(record).AsTask());
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Single(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task Cancellation_and_path_validation_do_not_create_records()
    {
        Assert.Throws<ArgumentException>(() => new DirectoryEvolutionEvaluationStore("relative"));
        Assert.Throws<ArgumentException>(() => new DirectoryEvolutionEvaluationStore(Path.GetPathRoot(Path.GetFullPath("."))!));
        if (Path.DirectorySeparatorChar == '\\')
        {
            Assert.Throws<ArgumentException>(() => new DirectoryEvolutionEvaluationStore("C:relative"));
            Assert.Throws<ArgumentException>(() => new DirectoryEvolutionEvaluationStore("\\root-relative"));
        }
        using var directory = new TemporaryDirectory();
        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectoryEvolutionEvaluationStore(directory.Path, 0));
        var store = new DirectoryEvolutionEvaluationStore(directory.Path); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(Key(), cancellation.Token).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.TryWriteAsync(Record(), cancellation.Token).AsTask());
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void Freshness_policy_uses_original_time_and_never_turns_copies_into_new_observations()
    {
        var key = Key(); var record = Record();
        var policy = new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Deterministic, TimeSpan.FromHours(1));
        Assert.Equal(EvolutionEvaluationReuseMode.Deterministic, policy.Mode); Assert.Equal(TimeSpan.FromHours(1), policy.MaximumAge);
        Assert.Equal(1, policy.MinimumSamples); Assert.Null(policy.MaximumStandardError);
        Assert.Equal(EvolutionEvaluationReuseDecision.Eligible, policy.Check(key, record, Observed.AddHours(1)));
        Assert.Equal(EvolutionEvaluationReuseDecision.Expired, policy.Check(key, record, Observed.AddHours(1).AddTicks(1)));
        Assert.Equal(EvolutionEvaluationReuseDecision.FutureObservation, policy.Check(key, record, Observed.AddTicks(-1)));
        Assert.Equal(EvolutionEvaluationReuseDecision.ForceFresh, policy.Check(key, record, Observed, true));
        Assert.Equal(EvolutionEvaluationReuseDecision.KeyMismatch, policy.Check(Key(payload: "different"), record, Observed));
        Assert.Equal(EvolutionEvaluationReuseDecision.Missing, policy.Check(key, null, Observed));
        Assert.Equal(EvolutionEvaluationReuseDecision.Disabled, new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Disabled,
            TimeSpan.FromHours(1)).Check(key, record, Observed));
        var samples = new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.ExistingSamples, TimeSpan.FromHours(1), maximumStandardError: 0.1);
        Assert.Equal(EvolutionEvaluationReuseDecision.UncertaintyUnavailable, samples.Check(key, record, Observed));
        Assert.Equal(EvolutionEvaluationReuseDecision.UncertaintyTooHigh, samples.Check(key, Record(error: 0.2), Observed));
        Assert.Equal(EvolutionEvaluationReuseDecision.Eligible, samples.Check(key, Record(error: 0.1), Observed));
        Assert.NotEqual(policy.VersionHash, samples.VersionHash);
        Assert.Equal(EvolutionEvaluationReuseDecision.InsufficientSamples, new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.ExistingSamples,
            TimeSpan.FromHours(1), minimumSamples: 2).Check(key, record, Observed));
        Assert.Throws<ArgumentOutOfRangeException>(() => policy.Check(key, record, default));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionEvaluationReusePolicy((EvolutionEvaluationReuseMode)99, TimeSpan.FromHours(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Disabled, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Disabled, TimeSpan.FromHours(1), 257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionEvaluationReusePolicy(EvolutionEvaluationReuseMode.Disabled, TimeSpan.FromHours(1), maximumStandardError: double.NaN));
    }

    private static EvolutionEvaluationCacheRecord Mutate(EvolutionEvaluationCacheRecord record, Action<JsonObject> mutation)
    {
        var envelope = JsonNode.Parse(record.ToJson())!; var root = JsonNode.Parse(envelope["Payload"]!.GetValue<string>())!.AsObject();
        mutation(root); string payload = root.ToJsonString(); envelope["Payload"] = payload; envelope["Checksum"] = EvolutionHash.Compute(payload);
        return EvolutionEvaluationCacheRecord.FromJson(envelope.ToJsonString());
    }

    private static EvolutionEvaluationCacheKey Key(string genome = "genome", string payload = "exact-genome-bytes", string measurement = "measurements-v1") =>
        new(new EvolutionReuseScope("task", "task-v1", "evaluator-v1", "codec", "codec-v1", "constraints-v1", "data-v1", "full-v1",
            "compiler-v1", "runtime-v1", "hardware-v1", "correctness-v1"), genome, EvolutionHash.Compute(payload), measurement);

    private static EvolutionMeasurementOrigin Origin(EvolutionEvaluationCacheKey key, string sample = "sample", DateTimeOffset? when = null, double? error = null) =>
        new(key.Scope.StableKey, "run", "evaluation", new[] { sample }, when ?? Observed, 7, "calls-v1", "stats-v1", standardError: error);

    private static EvolutionEvaluationCacheRecord Record(EvolutionEvaluationCacheKey? key = null, string sample = "sample",
        DateTimeOffset? when = null, double quality = 1, double? error = null)
    {
        key ??= Key();
        var measurement = new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, quality, descriptors: new Dictionary<string, double> { ["x"] = 1 },
            objectives: new[] { 1d, 2 }, constraintViolations: new[] { 0d }, costUnits: 7, metrics: new Dictionary<string, double> { ["metric"] = 1 },
            diagnostics: new[] { new EvolutionDiagnostic("test", "not persisted") }).WithMeasurementOrigin(Origin(key, sample, when, error));
        return new(key, measurement, EvolutionHash.Compute("raw-evidence"));
    }
}
