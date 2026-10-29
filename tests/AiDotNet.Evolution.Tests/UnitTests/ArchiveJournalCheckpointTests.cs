using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// E-1: a segmented checkpoint keeps the island archives in its append-only segment journal as well as the
/// deduplication set and cache. A save appends only the cells that changed, every CheckpointCompactionInterval segments a
/// base restates everything, and a resume replays the base and the tail. None of that may change what a checkpoint
/// means: replaying the journal must rebuild exactly the state an inline checkpoint holds, at every boundary, after any
/// compaction, and after a crash that leaves a segment no checkpoint names.
/// </summary>
public sealed class ArchiveJournalCheckpointTests
{
    private const string RunId = "journal";

    // Fills twenty cells and keeps improving them: generation g proposes (7g mod 100) + 1, so cells gain new elites,
    // replace old ones with better values and see repeated genomes that the cache answers.
    private sealed class SpreadingVariation : IVariationOperator<TestGenome>
    {
        public string Id => "spreading";
        public string VersionHash => "spreading-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome((int)(context.Generation * 7 % 100) + 1));
    }

    [Fact]
    public async Task Replaying_the_journal_rebuilds_exactly_the_archives_an_inline_checkpoint_holds()
    {
        var segmentedStore = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(120, EvolutionCheckpointFormat.Segmented), segmentedStore).RunAsync(Seeds());
        var inlineStore = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(120, EvolutionCheckpointFormat.Inline), inlineStore).RunAsync(Seeds());
        EvolutionCheckpoint segmented = await Latest(segmentedStore);
        EvolutionCheckpoint inline = await Latest(inlineStore);

        // The journal really holds the archives: the payload lists none, and the chain is a base and several deltas.
        JsonObject payload = Payload(segmented);
        JsonObject island = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["Islands"])[0]);
        Assert.True(Value<bool>(island["EntriesInSegments"]));
        Assert.Null(island["Entries"]);
        Assert.True(segmented.SegmentIds.Count > 2, "the run wrote too few segments to have a tail to replay");

        EvolutionCheckpointContents<TestGenome> replayed =
            await EvolutionEngine<TestGenome>.ReadCheckpointAsync(segmented, new TestGenomeCodec(), segmentedStore);
        EvolutionCheckpointContents<TestGenome> whole = EvolutionEngine<TestGenome>.ReadCheckpoint(inline, new TestGenomeCodec());
        Assert.Equal(Describe(whole), Describe(replayed));
        Assert.True(whole.Entries.Count(entry => entry.Source == EvolutionCheckpointEntrySource.IslandArchive) > 10,
            "the archive held too few elites for the comparison to mean anything");
    }

    [Fact]
    public async Task Every_delta_segment_holds_only_the_cells_that_changed()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(120, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        EvolutionCheckpoint latest = await Latest(store);
        int deltas = 0;
        foreach (long id in latest.SegmentIds.Skip(1))
        {
            JsonObject segment = await Segment(store, id);
            Assert.Equal(2, Value<int>(segment["SchemaVersion"]));
            Assert.False(Value<bool>(segment["IsBase"]));
            int written = segment["Islands"] is JsonArray islands
                ? islands.Sum(item => Assert.IsType<JsonArray>(Obj(item)["Entries"]).Count)
                : 0;
            // A checkpoint follows every four evaluations, so a delta can write at most four cells, never the archive.
            Assert.InRange(written, 0, 4);
            deltas++;
        }
        Assert.True(deltas > 0, "the chain had no delta segment");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(32)]
    public async Task Compaction_at_any_interval_resumes_to_the_state_hash_of_the_inline_format(int interval)
    {
        var compacted = new InMemoryEvolutionCheckpointStore();
        EvolutionEngineOptions first = Options(136, EvolutionCheckpointFormat.Segmented);
        first.CheckpointCompactionInterval = interval;
        await Engine(first, compacted).RunAsync(Seeds());
        EvolutionCheckpoint saved = await Latest(compacted);
        Assert.InRange(saved.SegmentIds.Count, 1, interval);
        EvolutionEngineOptions resume = Options(200, EvolutionCheckpointFormat.Segmented);
        resume.CheckpointCompactionInterval = interval;
        resume.Resume = true;
        string resumed = (await Engine(resume, compacted).RunAsync(Seeds())).StateHash;

        var inline = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(136, EvolutionCheckpointFormat.Inline), inline).RunAsync(Seeds());
        EvolutionEngineOptions inlineResume = Options(200, EvolutionCheckpointFormat.Inline);
        inlineResume.Resume = true;
        Assert.Equal((await Engine(inlineResume, inline).RunAsync(Seeds())).StateHash, resumed);
    }

    [Fact]
    public async Task A_crash_after_any_checkpoint_resumes_to_what_the_inline_checkpoint_at_that_boundary_resumes_to()
    {
        // Each saved checkpoint, with exactly the segments it named when it was saved, is what a process killed right
        // after that save leaves behind. Every one of them must resume like the inline checkpoint of the same boundary.
        const int budget = 120;
        var segmented = new RecordingStore();
        await Engine(Options(budget, EvolutionCheckpointFormat.Segmented), segmented).RunAsync(Seeds());
        var inline = new RecordingStore();
        await Engine(Options(budget, EvolutionCheckpointFormat.Inline), inline).RunAsync(Seeds());
        string uninterrupted = (await Engine(Options(budget, EvolutionCheckpointFormat.Inline), new InMemoryEvolutionCheckpointStore())
            .RunAsync(Seeds())).StateHash;

        Dictionary<long, string> inlineResumes = new();
        foreach (Recorded saved in inline.Saved)
            inlineResumes[saved.Checkpoint.Sequence] = await ResumeFrom(saved, EvolutionCheckpointFormat.Inline, budget);
        Assert.True(segmented.Saved.Count > 20, "too few boundaries to call this every boundary");
        int exact = 0;
        foreach (Recorded saved in segmented.Saved)
        {
            string resumed = await ResumeFrom(saved, EvolutionCheckpointFormat.Segmented, budget);
            Assert.True(inlineResumes.TryGetValue(saved.Checkpoint.Sequence, out string? expected),
                $"the inline run saved no checkpoint {saved.Checkpoint.Sequence}");
            Assert.Equal(expected, resumed);
            if (resumed == uninterrupted) exact++;
        }
        Assert.True(exact > 0, "no boundary resumed to the uninterrupted state, so the comparison above proves nothing");
    }

    [Fact]
    public async Task A_segment_written_without_its_checkpoint_is_ignored_and_then_replaced()
    {
        // A process killed after a segment reached the store but before the checkpoint naming it was saved leaves an
        // orphan segment, possibly cut short. Resume must use the last checkpoint, never read the orphan, and the next
        // save must take the orphan's identifier with its own complete segment.
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionCheckpointStore(directory.Path);
        await Engine(Unbroken(60), store).RunAsync(Seeds());
        EvolutionCheckpoint before = await Latest(store);
        long orphan = Value<long>(Payload(before)["NextSegmentId"]);
        byte[] torn = System.Text.Encoding.UTF8.GetBytes("{\"SchemaVersion\":2,\"IsBase\":false,\"Islands\":[{\"Island\":0,\"Entr");
        await store.WriteSegmentAsync(RunId, orphan, (stream, token) => stream.WriteAsync(torn, 0, torn.Length, token));

        EvolutionEngineOptions resume = Unbroken(120);
        resume.Resume = true;
        string resumed = (await Engine(resume, store).RunAsync(Seeds())).StateHash;

        var control = new InMemoryEvolutionCheckpointStore();
        await Engine(Unbroken(60), control).RunAsync(Seeds());
        EvolutionEngineOptions controlResume = Unbroken(120);
        controlResume.Resume = true;
        Assert.Equal((await Engine(controlResume, control).RunAsync(Seeds())).StateHash, resumed);

        EvolutionCheckpoint after = await Latest(store);
        Assert.Contains(orphan, after.SegmentIds);
        JsonObject replaced = await Segment(store, orphan);
        Assert.Equal(2, Value<int>(replaced["SchemaVersion"]));
        EvolutionEngineOptions again = Unbroken(140);
        again.Resume = true;
        await Engine(again, store).RunAsync(Seeds());

        // One chain from the start, so the identifiers the orphan sat between are both named.
        static EvolutionEngineOptions Unbroken(int budget)
        {
            EvolutionEngineOptions options = Options(budget, EvolutionCheckpointFormat.Segmented);
            options.CheckpointCompactionInterval = 128;
            return options;
        }
    }

    [Fact]
    public async Task A_resume_followed_by_a_save_writes_no_unchanged_entry()
    {
        // Restored entries keep the documents they were read from, so the first save after a resume writes no archive
        // entry the journal already holds, instead of rewriting the whole archive.
        var store = new InMemoryEvolutionCheckpointStore();
        EvolutionRunResult<TestGenome> first = await Engine(Options(60, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        Assert.True(first.Islands[0].Count > 8, "the archive held too few elites to tell a delta from a rewrite");
        EvolutionCheckpoint before = await Latest(store);
        // Four more evaluations: one batch, one save, at most four changed cells.
        EvolutionEngineOptions resume = Options(64, EvolutionCheckpointFormat.Segmented);
        resume.Resume = true;
        await Engine(resume, store).RunAsync(Seeds());
        EvolutionCheckpoint after = await Latest(store);
        long[] added = after.SegmentIds.Except(before.SegmentIds).ToArray();
        Assert.NotEmpty(added);
        int written = 0;
        foreach (long id in added)
        {
            JsonObject segment = await Segment(store, id);
            Assert.False(Value<bool>(segment["IsBase"]));
            if (segment["Islands"] is JsonArray islands)
                written += islands.Sum(item => Assert.IsType<JsonArray>(Obj(item)["Entries"]).Count);
        }
        Assert.InRange(written, 0, 4);
    }

    [Fact]
    public async Task A_checkpoint_in_the_previous_segmented_format_resumes_and_moves_to_the_archive_journal()
    {
        // The format before E-1: schema 10 to 13, schema 1 segments with only the deduplication set and cache, the
        // archives inline. It is rebuilt here from a current checkpoint so that both describe the same state.
        var current = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(60, EvolutionCheckpointFormat.Segmented), current).RunAsync(Seeds());
        EvolutionCheckpoint saved = await Latest(current);
        var previous = new InMemoryEvolutionCheckpointStore();
        await previous.SaveAsync(await PreviousFormat(saved, current, previous));

        string Expected(EvolutionRunResult<TestGenome> result) => result.StateHash;
        EvolutionEngineOptions resume = Options(120, EvolutionCheckpointFormat.Segmented);
        resume.Resume = true;
        string fromCurrent = Expected(await Engine(resume, current).RunAsync(Seeds()));
        EvolutionEngineOptions resumePrevious = Options(120, EvolutionCheckpointFormat.Segmented);
        resumePrevious.Resume = true;
        string fromPrevious = Expected(await Engine(resumePrevious, previous).RunAsync(Seeds()));
        Assert.Equal(fromCurrent, fromPrevious);

        // Its first save starts a new chain in the current format.
        JsonObject migrated = Payload(await Latest(previous));
        Assert.True(Value<int>(migrated["SchemaVersion"]) >= 14);
        Assert.True(Value<bool>(Obj(Assert.IsType<JsonArray>(migrated["Islands"])[0])["EntriesInSegments"]));
    }

    [Fact]
    public async Task The_archive_journal_format_is_one_that_earlier_engines_refuse()
    {
        // Earlier engines take 4 off a segmented version and accept feature versions 6 to 9. A payload whose archives
        // are in segments must never read to them as one of those, or they would resume with empty archives.
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(40, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        int version = Value<int>(Payload(await Latest(store))["SchemaVersion"]);
        Assert.InRange(version - 4, 10, int.MaxValue);
    }

    [Theory]
    [InlineData("claims-old-version")]
    [InlineData("drops-the-flag")]
    [InlineData("lists-entries-too")]
    public async Task A_payload_inconsistent_about_where_its_archives_are_is_refused(string tamper)
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(40, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        EvolutionCheckpoint saved = await Latest(store);
        JsonObject payload = Payload(saved);
        JsonObject island = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["Islands"])[0]);
        switch (tamper)
        {
            case "claims-old-version":
                payload["SchemaVersion"] = Value<int>(payload["SchemaVersion"]) - 4;
                break;
            case "drops-the-flag":
                island.Remove("EntriesInSegments");
                island["Entries"] = new JsonArray();
                break;
            default:
                island["Entries"] = new JsonArray();
                break;
        }
        await store.SaveAsync(new EvolutionCheckpoint(saved.RunId, saved.Sequence + 1, saved.CompatibilityHash, payload.ToJsonString(),
            saved.SchemaVersion, saved.Quality, saved.QualityDirection).WithSegmentIds(saved.SegmentIds));

        EvolutionEngineOptions resume = Options(80, EvolutionCheckpointFormat.Segmented);
        resume.Resume = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => Engine(resume, store).RunAsync(Seeds()));
    }

    [Fact]
    public async Task Reading_an_archive_journal_without_its_store_says_what_it_needs()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(40, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        InvalidDataException refused = Assert.Throws<InvalidDataException>(() =>
            EvolutionEngine<TestGenome>.ReadCheckpoint(store.LoadLatestAsync(RunId).GetAwaiter().GetResult()
                ?? throw new InvalidOperationException("no checkpoint"), new TestGenomeCodec()));
        Assert.Contains("ReadCheckpointAsync", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_delta_that_empties_an_unknown_cell_is_refused()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(120, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        EvolutionCheckpoint saved = await Latest(store);
        long last = saved.SegmentIds[saved.SegmentIds.Count - 1];
        JsonObject segment = await Segment(store, last);
        Assert.False(Value<bool>(segment["IsBase"]));
        segment["Islands"] = new JsonArray(new JsonObject
        {
            ["Island"] = 0,
            ["Entries"] = new JsonArray(),
            ["RemovedCells"] = new JsonArray("999")
        });
        JsonObject payload = Payload(saved);
        await RewriteSegment(store, payload, last, segment);
        await store.SaveAsync(new EvolutionCheckpoint(saved.RunId, saved.Sequence + 1, saved.CompatibilityHash, payload.ToJsonString(),
            saved.SchemaVersion, saved.Quality, saved.QualityDirection).WithSegmentIds(saved.SegmentIds));

        EvolutionEngineOptions resume = Options(160, EvolutionCheckpointFormat.Segmented);
        resume.Resume = true;
        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => Engine(resume, store).RunAsync(Seeds()));
        Assert.Contains("unknown archive cell", refused.Message, StringComparison.Ordinal);
    }

    // E-1 reads a segment straight into an array of the length its stream reports. A store's stream may not report one,
    // or may report the wrong one; the resume must still see exactly the bytes the stream holds, which the SHA-256 check
    // then accepts, and reach the same state as reading the store directly.
    [Theory]
    [InlineData(SegmentStreamShape.Unseekable)]
    [InlineData(SegmentStreamShape.ReportsTooFew)]
    [InlineData(SegmentStreamShape.ReportsTooMany)]
    [InlineData(SegmentStreamShape.ReadsOneByteAtATime)]
    public async Task A_segment_stream_of_any_shape_resumes_to_the_same_state(SegmentStreamShape shape)
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(60, EvolutionCheckpointFormat.Segmented), store).RunAsync(Seeds());
        EvolutionCheckpoint saved = await Latest(store);
        var copy = new InMemoryEvolutionCheckpointStore();
        foreach (long id in saved.SegmentIds)
        {
            using Stream source = Assert.IsAssignableFrom<Stream>(await store.OpenSegmentAsync(RunId, id));
            using var bytes = new MemoryStream();
            await source.CopyToAsync(bytes);
            byte[] data = bytes.ToArray();
            await copy.WriteSegmentAsync(RunId, id, (target, token) => target.WriteAsync(data, 0, data.Length, token));
        }
        await copy.SaveAsync(saved);

        EvolutionEngineOptions direct = Options(120, EvolutionCheckpointFormat.Segmented);
        direct.Resume = true;
        string expected = (await Engine(direct, store).RunAsync(Seeds())).StateHash;
        EvolutionEngineOptions shaped = Options(120, EvolutionCheckpointFormat.Segmented);
        shaped.Resume = true;
        string actual = (await Engine(shaped, new ShapedSegmentStore(copy, shape)).RunAsync(Seeds())).StateHash;
        Assert.Equal(expected, actual);
    }

    public enum SegmentStreamShape
    {
        Unseekable,
        ReportsTooFew,
        ReportsTooMany,
        ReadsOneByteAtATime
    }

    // Serves each segment through a stream of the given shape; everything else goes to the inner store.
    private sealed class ShapedSegmentStore(InMemoryEvolutionCheckpointStore inner, SegmentStreamShape shape) : IEvolutionCheckpointSegmentStore
    {
        public Task SaveAsync(EvolutionCheckpoint checkpoint, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(checkpoint, cancellationToken);

        public Task<EvolutionCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default) =>
            inner.LoadLatestAsync(runId, cancellationToken);

        public Task WriteSegmentAsync(string runId, long segmentId, Func<Stream, CancellationToken, Task> write,
            CancellationToken cancellationToken = default) => inner.WriteSegmentAsync(runId, segmentId, write, cancellationToken);

        public async Task<Stream?> OpenSegmentAsync(string runId, long segmentId, CancellationToken cancellationToken = default)
        {
            Stream? stream = await inner.OpenSegmentAsync(runId, segmentId, cancellationToken);
            if (stream is null) return null;
            using (stream)
            {
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes);
                return new ShapedStream(bytes.ToArray(), shape);
            }
        }
    }

    private sealed class ShapedStream(byte[] data, SegmentStreamShape shape) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => shape is SegmentStreamShape.ReportsTooFew or SegmentStreamShape.ReportsTooMany;
        public override bool CanWrite => false;

        public override long Length => shape switch
        {
            SegmentStreamShape.ReportsTooFew => data.Length / 2,
            SegmentStreamShape.ReportsTooMany => data.Length * 2L,
            _ => throw new NotSupportedException()
        };

        public override long Position
        {
            get => CanSeek ? _position : throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(count, data.Length - _position);
            if (shape == SegmentStreamShape.ReadsOneByteAtATime) available = Math.Min(available, 1);
            Array.Copy(data, _position, buffer, offset, available);
            _position += available;
            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void The_compaction_interval_is_bounded()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionEngineOptions { CheckpointCompactionInterval = 0 }.SnapshotAndValidate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new EvolutionEngineOptions { CheckpointCompactionInterval = EvolutionCheckpoint.MaximumSegmentCount }.SnapshotAndValidate());
        Assert.Equal(32, new EvolutionEngineOptions().CheckpointCompactionInterval);
    }

    // The previous format of a current checkpoint: replays its segments here, puts the archives back inline and writes
    // one schema 1 base segment with the deduplication set and cache.
    private static async Task<EvolutionCheckpoint> PreviousFormat(EvolutionCheckpoint saved, InMemoryEvolutionCheckpointStore from,
        InMemoryEvolutionCheckpointStore to)
    {
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var cache = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        var cells = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (long id in saved.SegmentIds)
        {
            JsonObject segment = await Segment(from, id);
            foreach (JsonNode? removed in segment["RemovedGenomeIds"] as JsonArray ?? new JsonArray()) seen.Remove(Value<string>(removed));
            foreach (JsonNode? added in segment["SeenGenomeIds"] as JsonArray ?? new JsonArray()) seen.Add(Value<string>(added));
            foreach (JsonNode? removed in segment["RemovedCacheIds"] as JsonArray ?? new JsonArray()) cache.Remove(Value<string>(removed));
            foreach (JsonNode? entry in segment["Cache"] as JsonArray ?? new JsonArray())
                cache[Value<string>(Obj(entry)["GenomeId"])] = Obj(entry).DeepClone();
            foreach (JsonNode? island in segment["Islands"] as JsonArray ?? new JsonArray())
            {
                foreach (JsonNode? removed in Obj(island)["RemovedCells"] as JsonArray ?? new JsonArray()) cells.Remove(Value<string>(removed));
                foreach (JsonNode? entry in Obj(island)["Entries"] as JsonArray ?? new JsonArray())
                    cells[string.Join(",", Assert.IsType<JsonArray>(Obj(entry)["CellBins"]).Select(bin => Value<int>(bin)))] = Obj(entry).DeepClone();
            }
        }

        var baseSegment = new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["IsBase"] = true,
            ["SeenGenomeIds"] = new JsonArray(seen.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()),
            ["Cache"] = new JsonArray(cache.Values.ToArray())
        };
        JsonObject payload = Payload(saved);
        JsonObject island0 = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(payload["Islands"])[0]);
        island0.Remove("EntriesInSegments");
        island0["Entries"] = new JsonArray(cells.Values.ToArray());
        payload["SchemaVersion"] = Value<int>(payload["SchemaVersion"]) - 4;
        payload["Segments"] = new JsonArray(new JsonObject { ["Id"] = 0L, ["IsBase"] = true, ["Sha256"] = "" });
        payload["NextSegmentId"] = 1L;
        await RewriteSegment(to, payload, 0, baseSegment);
        return new EvolutionCheckpoint(saved.RunId, saved.Sequence, saved.CompatibilityHash, payload.ToJsonString(),
            saved.SchemaVersion, saved.Quality, saved.QualityDirection).WithSegmentIds(new long[] { 0 });
    }

    // Writes a segment and records its SHA-256 in the payload's segment list, as the engine does.
    private static async Task RewriteSegment(InMemoryEvolutionCheckpointStore store, JsonObject payload, long id, JsonObject segment)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(segment.ToJsonString());
        await store.WriteSegmentAsync(RunId, id, (target, token) => target.WriteAsync(bytes, 0, bytes.Length, token));
        string digest;
        using (var sha = System.Security.Cryptography.SHA256.Create())
            digest = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        foreach (JsonNode? reference in Assert.IsType<JsonArray>(payload["Segments"]))
            if (Value<long>(Obj(reference)["Id"]) == id) Obj(reference)["Sha256"] = digest;
    }

    private static async Task<string> ResumeFrom(Recorded saved, EvolutionCheckpointFormat format, int budget)
    {
        var store = new InMemoryEvolutionCheckpointStore();
        foreach (KeyValuePair<long, byte[]> segment in saved.Segments)
            await store.WriteSegmentAsync(RunId, segment.Key, (target, token) => target.WriteAsync(segment.Value, 0, segment.Value.Length, token));
        await store.SaveAsync(saved.Checkpoint);
        EvolutionEngineOptions resume = Options(budget, format);
        resume.Resume = true;
        return (await Engine(resume, store).RunAsync(Seeds())).StateHash;
    }

    private static string Describe(EvolutionCheckpointContents<TestGenome> contents) => string.Join("\n", contents.Entries
        .Select(entry => string.Join("|", entry.Island, entry.Source, entry.Entry.Cell.StableKey, entry.Entry.Evaluation.GenomeId,
            entry.Entry.Evaluation.EvaluationId, entry.Entry.Candidate.CanonicalGenome.Genome.Value,
            entry.Entry.Evaluation.Quality?.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            entry.Entry.Evaluation.Lineage.Generation)));

    private static async Task<EvolutionCheckpoint> Latest(IEvolutionCheckpointStore store) =>
        Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync(RunId));

    private static async Task<JsonObject> Segment(IEvolutionCheckpointSegmentStore store, long id)
    {
        using Stream stream = Assert.IsAssignableFrom<Stream>(await store.OpenSegmentAsync(RunId, id));
        return Assert.IsType<JsonObject>(JsonNode.Parse(stream));
    }

    private static T Value<T>(JsonNode? node) => Assert.IsAssignableFrom<JsonValue>(node).GetValue<T>();

    private static JsonObject Obj(JsonNode? node) => Assert.IsType<JsonObject>(node);

    private static JsonObject Payload(EvolutionCheckpoint checkpoint) => Assert.IsType<JsonObject>(JsonNode.Parse(checkpoint.Payload));

    private static TestGenome[] Seeds() => new[] { new TestGenome(0) };

    private static EvolutionEngineOptions Options(int budget, EvolutionCheckpointFormat format) => new()
    {
        RunId = RunId,
        Seed = 11,
        MaxEvaluationAttempts = budget,
        MaxProposals = 1000,
        MaxGenerations = 1000,
        ProposalBatchSize = 4,
        MaxDegreeOfParallelism = 1,
        MigrationInterval = 0,
        CheckpointInterval = 4,
        CheckpointFormat = format
    };

    private static EvolutionEngine<TestGenome> Engine(EvolutionEngineOptions options, IEvolutionCheckpointStore store) =>
        new(new SyntheticEvolutionTask(), new SpreadingVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 20) }), options,
            checkpointStore: store, genomeCodec: new TestGenomeCodec());

    private sealed record Recorded(EvolutionCheckpoint Checkpoint, Dictionary<long, byte[]> Segments);

    // Records each checkpoint as it is saved, with the bytes of every segment it names at that moment.
    private sealed class RecordingStore : IEvolutionCheckpointSegmentStore
    {
        private readonly InMemoryEvolutionCheckpointStore _inner = new();

        public List<Recorded> Saved { get; } = new();

        public async Task SaveAsync(EvolutionCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            await _inner.SaveAsync(checkpoint, cancellationToken);
            var segments = new Dictionary<long, byte[]>();
            foreach (long id in checkpoint.SegmentIds)
            {
                using Stream stream = Assert.IsAssignableFrom<Stream>(await _inner.OpenSegmentAsync(checkpoint.RunId, id, cancellationToken));
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                segments[id] = copy.ToArray();
            }
            Saved.Add(new Recorded(checkpoint, segments));
        }

        public Task<EvolutionCheckpoint?> LoadLatestAsync(string runId, CancellationToken cancellationToken = default) =>
            _inner.LoadLatestAsync(runId, cancellationToken);

        public Task WriteSegmentAsync(string runId, long segmentId, Func<Stream, CancellationToken, Task> write,
            CancellationToken cancellationToken = default) => _inner.WriteSegmentAsync(runId, segmentId, write, cancellationToken);

        public Task<Stream?> OpenSegmentAsync(string runId, long segmentId, CancellationToken cancellationToken = default) =>
            _inner.OpenSegmentAsync(runId, segmentId, cancellationToken);
    }
}
