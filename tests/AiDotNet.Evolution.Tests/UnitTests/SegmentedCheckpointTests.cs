using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-73 (#178): storing the deduplication set and cache in store segments changes nothing a checkpoint means. A
/// segmented run, interrupted and resumed, reaches exactly the state an inline one does at every stop point, whatever
/// the store, however many segments it has written, and whether genomes are being forgotten; and where a resume is
/// exact for inline checkpoints it is exact for segmented ones.
/// </summary>
public sealed class SegmentedCheckpointTests
{
    // Proposes 60 distinct genomes and then repeats them, so a run exercises new entries, cache hits and, with a
    // capacity, forgotten genomes that come back.
    private sealed class CyclingVariation : IVariationOperator<TestGenome>
    {
        public string Id => "cycling";
        public string VersionHash => "cycling-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome((int)(context.Generation % 60) + 1));
    }

    public static TheoryData<string, int, int> Interruptions => new()
    {
        { "memory", 16, 0 },
        { "directory", 16, 0 },
        // More than 32 saves, so a later base segment replaces the chain the earlier ones built.
        { "memory", 136, 0 },
        { "directory", 136, 0 },
        // A capacity forgets genomes, so deltas also carry removals.
        { "memory", 72, 6 },
        { "directory", 72, 6 }
    };

    [Theory]
    [MemberData(nameof(Interruptions))]
    public async Task A_segmented_run_resumes_to_exactly_the_state_an_inline_one_does(string kind, int stopAfter, int capacity)
    {
        using var directory = new TemporaryDirectory();
        const int budget = 200;
        async Task<(string Resumed, string Uninterrupted, EvolutionCheckpoint Saved)> Interrupt(EvolutionCheckpointFormat format, string name)
        {
            IEvolutionCheckpointStore store = Store(kind, Path.Combine(directory.Path, name));
            await Engine(Options(stopAfter, format, capacity), store).RunAsync(Seeds());
            EvolutionCheckpoint saved = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented"));
            EvolutionEngineOptions resume = Options(budget, format, capacity);
            resume.Resume = true;
            string resumed = (await Engine(resume, store).RunAsync(Seeds())).StateHash;
            string uninterrupted = (await Engine(Options(budget, format, capacity), Store(kind, Path.Combine(directory.Path, name + "-control")))
                .RunAsync(Seeds())).StateHash;
            return (resumed, uninterrupted, saved);
        }

        var segmented = await Interrupt(EvolutionCheckpointFormat.Segmented, "segmented");
        var inline = await Interrupt(EvolutionCheckpointFormat.Inline, "inline");

        Assert.NotEmpty(segmented.Saved.SegmentIds);
        Assert.Empty(inline.Saved.SegmentIds);
        Assert.Equal(inline.Resumed, segmented.Resumed);
        Assert.Equal(inline.Uninterrupted, segmented.Uninterrupted);
        // At this stop point an inline resume is exact (at 16 and 72 it is not, for inline checkpoints as much as for
        // segmented ones), so a segmented resume must be exact too, across a base-segment rewrite.
        if (stopAfter == 136) Assert.Equal(segmented.Uninterrupted, segmented.Resumed);
    }

    [Fact]
    public async Task Auto_keeps_small_runs_self_contained_and_segments_once_the_run_is_large()
    {
        EvolutionEngineOptions small = Options(8, EvolutionCheckpointFormat.Auto, 0);
        small.CheckpointSegmentThreshold = 20;
        var smallStore = new InMemoryEvolutionCheckpointStore();
        await Engine(small, smallStore).RunAsync(Seeds());
        EvolutionCheckpoint inline = Assert.IsType<EvolutionCheckpoint>(await smallStore.LoadLatestAsync("segmented"));
        Assert.Empty(inline.SegmentIds);
        Assert.NotEmpty(Assert.IsType<JsonArray>(Payload(inline)["SeenGenomeIds"]));

        EvolutionEngineOptions large = Options(80, EvolutionCheckpointFormat.Auto, 0);
        large.CheckpointSegmentThreshold = 20;
        var largeStore = new InMemoryEvolutionCheckpointStore();
        await Engine(large, largeStore).RunAsync(Seeds());
        EvolutionCheckpoint segmented = Assert.IsType<EvolutionCheckpoint>(await largeStore.LoadLatestAsync("segmented"));
        Assert.NotEmpty(segmented.SegmentIds);
        Assert.Null(Payload(segmented)["SeenGenomeIds"]);
        Assert.Null(Payload(segmented)["Cache"]);
    }

    [Fact]
    public async Task A_segmented_payload_leaves_out_exactly_the_deduplication_set_and_cache()
    {
        // The two structures that grow with every distinct genome are the whole difference; everything else, bounded by
        // the archive, is identical apart from the segment list and the version.
        async Task<JsonObject> Saved(EvolutionCheckpointFormat format)
        {
            var store = new InMemoryEvolutionCheckpointStore();
            await Engine(Options(60, format, 0), store).RunAsync(Seeds());
            return Payload(Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented")));
        }

        JsonObject inline = await Saved(EvolutionCheckpointFormat.Inline);
        JsonObject segmented = await Saved(EvolutionCheckpointFormat.Segmented);
        Assert.True(Assert.IsType<JsonArray>(inline["SeenGenomeIds"]).Count > 40);
        Assert.True(Assert.IsType<JsonArray>(inline["Cache"]).Count > 40);
        foreach (KeyValuePair<string, JsonNode?> field in inline)
        {
            // Islands carry each evaluation's elapsed time, which differs between any two runs.
            if (field.Key is "SeenGenomeIds" or "Cache" or "SchemaVersion" or "BudgetOptions" or "Islands") continue;
            Assert.Equal(field.Value?.ToJsonString(), segmented[field.Key]?.ToJsonString());
        }
        Assert.Equal(inline["SchemaVersion"]!.GetValue<int>() + 4, segmented["SchemaVersion"]!.GetValue<int>());
        Assert.NotEmpty(Assert.IsType<JsonArray>(segmented["Segments"]));
        // An inline payload names no segments at all, so it is what engines before segments wrote.
        Assert.False(inline.ContainsKey("Segments"));
        Assert.False(inline.ContainsKey("NextSegmentId"));
    }

    [Fact]
    public async Task A_tampered_segment_is_refused_on_resume()
    {
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionCheckpointStore(directory.Path);
        await Engine(Options(12, EvolutionCheckpointFormat.Segmented, 0), store).RunAsync(Seeds());
        string segment = Directory.EnumerateFiles(Path.Combine(directory.Path, DirectoryEvolutionCheckpointStore.SegmentDirectoryName),
            "segment-*.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal).First();
        // One changed byte: still valid JSON with the same meaning, so only the SHA-256 check can catch it.
        string text = File.ReadAllText(segment);
        File.WriteAllText(segment, text.Replace("\"IsBase\":true", "\"IsBase\": true"));

        EvolutionEngineOptions resume = Options(20, EvolutionCheckpointFormat.Segmented, 0);
        resume.Resume = true;
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => Engine(resume, store).RunAsync(Seeds()));
        Assert.Contains("SHA-256", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_segmented_checkpoint_copied_without_its_segments_says_what_it_needs()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(12, EvolutionCheckpointFormat.Segmented, 0), store).RunAsync(Seeds());
        EvolutionCheckpoint saved = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented"));
        // Copied the way a caller copies a checkpoint object: through its public constructor, so no segment list.
        var elsewhere = new InMemoryEvolutionCheckpointStore();
        await elsewhere.SaveAsync(new EvolutionCheckpoint(saved.RunId, saved.Sequence, saved.CompatibilityHash, saved.Payload,
            saved.SchemaVersion, saved.Quality, saved.QualityDirection));

        EvolutionEngineOptions resume = Options(20, EvolutionCheckpointFormat.Segmented, 0);
        resume.Resume = true;
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => Engine(resume, elsewhere).RunAsync(Seeds()));
        Assert.Contains("EvolutionCheckpointFormat.Inline", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Segmented_needs_a_store_that_keeps_segments()
    {
        using var directory = new TemporaryDirectory();
        var store = new JsonEvolutionCheckpointStore(Path.Combine(directory.Path, "checkpoint.json"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Engine(Options(8, EvolutionCheckpointFormat.Segmented, 0), store).RunAsync(Seeds()));
    }

    [Fact]
    public async Task An_inline_run_resumes_a_segmented_checkpoint_and_continues_inline()
    {
        // Resumed inline from a segmented checkpoint, the run reaches exactly what it reaches resumed from an inline one.
        async Task<(string Hash, EvolutionCheckpoint Latest)> ResumeInlineFrom(EvolutionCheckpointFormat written)
        {
            var store = new InMemoryEvolutionCheckpointStore();
            await Engine(Options(32, written, 0), store).RunAsync(Seeds());
            EvolutionEngineOptions resume = Options(96, EvolutionCheckpointFormat.Inline, 0);
            resume.Resume = true;
            string hash = (await Engine(resume, store).RunAsync(Seeds())).StateHash;
            return (hash, Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented")));
        }

        var fromSegmented = await ResumeInlineFrom(EvolutionCheckpointFormat.Segmented);
        var fromInline = await ResumeInlineFrom(EvolutionCheckpointFormat.Inline);
        Assert.Equal(fromInline.Hash, fromSegmented.Hash);
        Assert.Empty(fromSegmented.Latest.SegmentIds);
        // The saves after the resume are inline again: the seen set is back in the payload and no segments are named.
        Assert.NotEmpty(Assert.IsType<JsonArray>(Payload(fromSegmented.Latest)["SeenGenomeIds"]));
        Assert.False(Payload(fromSegmented.Latest).ContainsKey("Segments"));
    }

    [Fact]
    public async Task A_missing_segment_reference_is_refused_as_invalid_data()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(12, EvolutionCheckpointFormat.Segmented, 0), store).RunAsync(Seeds());
        EvolutionCheckpoint saved = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented"));
        JsonObject payload = Payload(saved);
        Assert.IsType<JsonArray>(payload["Segments"])[0] = null;
        await store.SaveAsync(Rewritten(saved, payload));

        EvolutionEngineOptions resume = Options(20, EvolutionCheckpointFormat.Segmented, 0);
        resume.Resume = true;
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => Engine(resume, store).RunAsync(Seeds()));
        Assert.Contains("segment list is invalid", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_segment_is_held_to_the_same_bounds_as_an_inline_cache()
    {
        // A cache entry without a result, correctly hashed: only the inline cache checks, run on the rebuilt cache, catch it.
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(12, EvolutionCheckpointFormat.Segmented, 0), store).RunAsync(Seeds());
        EvolutionCheckpoint saved = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented"));
        JsonObject payload = Payload(saved);
        JsonObject reference = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["Segments"])[0]);
        long id = Assert.IsAssignableFrom<JsonValue>(reference["Id"]).GetValue<long>();
        JsonObject segment;
        using (Stream stream = Assert.IsAssignableFrom<Stream>(await store.OpenSegmentAsync("segmented", id)))
            segment = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
        Assert.IsType<JsonArray>(segment["Cache"]).Add(new JsonObject { ["GenomeId"] = "crafted", ["Result"] = null });
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(segment.ToJsonString());
        await store.WriteSegmentAsync("segmented", id, (target, token) => target.WriteAsync(bytes, 0, bytes.Length, token));
        string recorded = Assert.IsAssignableFrom<JsonValue>(reference["Sha256"]).GetValue<string>();
        string digest;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        reference["Sha256"] = recorded.Any(char.IsLower) ? digest.ToLowerInvariant() : digest;
        await store.SaveAsync(Rewritten(saved, payload));

        EvolutionEngineOptions resume = Options(20, EvolutionCheckpointFormat.Segmented, 0);
        resume.Resume = true;
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => Engine(resume, store).RunAsync(Seeds()));
        Assert.Contains("cache entry is incomplete", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_segment_lists_its_genomes_in_ordinal_order_whatever_the_history()
    {
        // A capacity makes the run evict genomes, so hash-table slot order stops matching insertion or key order. Ordinal
        // order, as the inline payload uses, makes a segment's bytes depend on the state alone.
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(150, EvolutionCheckpointFormat.Segmented, 6), store).RunAsync(Seeds());
        EvolutionEngineOptions resume = Options(400, EvolutionCheckpointFormat.Segmented, 6);
        resume.Resume = true;
        await Engine(resume, store).RunAsync(Seeds());

        EvolutionCheckpoint latest = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented"));
        Assert.NotEmpty(latest.SegmentIds);
        int listed = 0;
        foreach (long id in latest.SegmentIds)
        {
            JsonObject segment;
            using (Stream stream = Assert.IsAssignableFrom<Stream>(await store.OpenSegmentAsync("segmented", id)))
                segment = Assert.IsType<JsonObject>(JsonNode.Parse(stream));
            foreach (string name in new[] { "SeenGenomeIds", "RemovedGenomeIds", "RemovedCacheIds", "Cache" })
            {
                if (segment[name] is not JsonArray array) continue;
                List<string> keys = array.Select(item => item is JsonObject entry
                    ? Assert.IsAssignableFrom<JsonValue>(entry["GenomeId"]).GetValue<string>()
                    : Assert.IsAssignableFrom<JsonValue>(item).GetValue<string>()).ToList();
                Assert.Equal(keys.OrderBy(key => key, StringComparer.Ordinal), keys);
                listed += keys.Count;
            }
        }
        Assert.True(listed > 6, "the segments listed too few genomes to show an order");
    }
    [Fact]
    public async Task A_segmented_checkpoint_whose_only_measurement_origins_are_cached_resumes()
    {
        // The seed (60, no origin) holds the one cell; every later candidate carries an origin and loses to it, so the
        // origins live only in the evaluation cache, which a segmented payload leaves out.
        var store = new InMemoryEvolutionCheckpointStore();
        EvolutionEngineOptions first = Options(41, EvolutionCheckpointFormat.Segmented, 0);
        first.EnableEvaluationCache = true;
        EvolutionRunResult<TestGenome> before = await OriginEngine(first, store).RunAsync(new[] { new TestGenome(60) });

        EvolutionCheckpoint saved = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented"));
        JsonObject payload = Payload(saved);
        // The run must end on a whole batch: a cut-short one is carried in the payload with its results, origins included.
        Assert.False(payload.ContainsKey("CarriedEvaluations"), "the last batch was cut short, so its results are in the payload");
        Assert.DoesNotContain("MeasurementOriginJson", payload.ToJsonString(), StringComparison.Ordinal);
        bool cachedOrigin = false;
        foreach (long id in saved.SegmentIds)
        {
            using Stream stream = Assert.IsAssignableFrom<Stream>(await store.OpenSegmentAsync("segmented", id));
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            cachedOrigin |= (await reader.ReadToEndAsync()).Contains("MeasurementOriginJson", StringComparison.Ordinal);
        }
        Assert.True(cachedOrigin, "no cached result carried a measurement origin, so this proves nothing");

        EvolutionEngineOptions resume = Options(80, EvolutionCheckpointFormat.Segmented, 0);
        resume.EnableEvaluationCache = true;
        resume.Resume = true;
        EvolutionRunResult<TestGenome> resumed = await OriginEngine(resume, store).RunAsync(new[] { new TestGenome(60) });
        Assert.True(resumed.Counters.Proposals > before.Counters.Proposals, "the resumed run did not continue past the checkpoint");
    }

    [Theory]
    [InlineData(EvolutionCheckpointFormat.Inline)]
    [InlineData(EvolutionCheckpointFormat.Segmented)]
    public async Task A_checkpoint_whose_only_measurement_origins_are_in_a_carried_batch_resumes(EvolutionCheckpointFormat format)
    {
        // No cache, and the seed holds the one cell, so the only origins in the checkpoint are the results of the batch
        // the budget cut short, which the checkpoint carries.
        var store = new InMemoryEvolutionCheckpointStore();
        EvolutionEngineOptions first = Options(40, format, 0);
        first.EnableEvaluationCache = false;
        EvolutionRunResult<TestGenome> before = await OriginEngine(first, store).RunAsync(new[] { new TestGenome(60) });
        JsonObject payload = Payload(Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("segmented")));
        Assert.True(payload.ContainsKey("CarriedEvaluations"), "the last batch was not cut short, so this proves nothing");
        payload.Remove("CarriedEvaluations");
        Assert.DoesNotContain("MeasurementOriginJson", payload.ToJsonString(), StringComparison.Ordinal);

        EvolutionEngineOptions resume = Options(80, format, 0);
        resume.EnableEvaluationCache = false;
        resume.Resume = true;
        EvolutionRunResult<TestGenome> resumed = await OriginEngine(resume, store).RunAsync(new[] { new TestGenome(60) });
        Assert.True(resumed.Counters.Proposals > before.Counters.Proposals, "the resumed run did not continue past the checkpoint");
    }

    private sealed class CachedOriginTask : IEvolutionTask<TestGenome>
    {
        public string Id => "cached-origin";
        public string VersionHash => "cached-origin-v1";
        public string EvaluatorVersionHash => "cached-origin-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            EvolutionTaskResult result = EvolutionTaskResult.Completed(value, new Dictionary<string, double> { ["x"] = 50 });
            return new(value == 60 ? result : result.WithMeasurementOrigin(new EvolutionMeasurementOrigin(new string('a', 64), "run", "1",
                new[] { "sample" }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 1, "calls", "test-v1",
                EvolutionMeasurementOriginKind.Measured)));
        }
    }

    private static EvolutionEngine<TestGenome> OriginEngine(EvolutionEngineOptions options, IEvolutionCheckpointStore store) =>
        new(new CachedOriginTask(), new CyclingVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 4) }), options,
            checkpointStore: store, genomeCodec: new TestGenomeCodec());

    private static EvolutionCheckpoint Rewritten(EvolutionCheckpoint saved, JsonObject payload) =>
        new EvolutionCheckpoint(saved.RunId, saved.Sequence + 1, saved.CompatibilityHash, payload.ToJsonString(),
            saved.SchemaVersion, saved.Quality, saved.QualityDirection).WithSegmentIds(saved.SegmentIds);

    private static TestGenome[] Seeds() => new[] { new TestGenome(0) };

    private static JsonObject Payload(EvolutionCheckpoint checkpoint) => Assert.IsType<JsonObject>(JsonNode.Parse(checkpoint.Payload));

    private static IEvolutionCheckpointStore Store(string kind, string path) => kind == "memory"
        ? new InMemoryEvolutionCheckpointStore()
        : new DirectoryEvolutionCheckpointStore(path);

    private static EvolutionEngineOptions Options(int budget, EvolutionCheckpointFormat format, int capacity) => new()
    {
        RunId = "segmented",
        Seed = 11,
        MaxEvaluationAttempts = budget,
        MaxProposals = 1000,
        MaxGenerations = 1000,
        ProposalBatchSize = 4,
        MaxDegreeOfParallelism = 1,
        MigrationInterval = 0,
        CheckpointInterval = 4,
        CheckpointFormat = format,
        DeduplicationCapacity = capacity
    };

    private static EvolutionEngine<TestGenome> Engine(EvolutionEngineOptions options, IEvolutionCheckpointStore store) =>
        new(new SyntheticEvolutionTask(), new CyclingVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 4) }), options,
            checkpointStore: store, genomeCodec: new TestGenomeCodec());
}