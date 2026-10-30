using System.Globalization;
using System.Runtime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

// A resume's no-GC region is process-wide, so these must not overlap a test that watches the GC latency mode.
[CollectionDefinition(nameof(ResumeRestoreCollection), DisableParallelization = true)]
public sealed class ResumeRestoreCollection { }

/// <summary>
/// E-1: a resume rebuilds an island's archive entries in parallel once there are enough of them, and suspends garbage
/// collection while it rebuilds. Neither may change what a resume reports: a corrupt checkpoint still names the first
/// failing entry in document order, the region is closed when the resume ends, and the option turns it off.
/// </summary>
[Collection(nameof(ResumeRestoreCollection))]
public sealed class ResumeRestoreTests
{
    private const string RunId = "resume-restore";
    // Above the engine's parallel rebuild threshold of 256 entries.
    private const int Elites = 320;

    [Fact]
    public async Task A_parallel_rebuild_reports_the_first_failing_entry_in_document_order()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(Options(resume: false, EvolutionCheckpointFormat.Inline), store, new TestGenomeCodec()).RunAsync(Seeds());
        EvolutionCheckpoint checkpoint = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync(RunId));
        JsonArray entries = Assert.IsType<JsonArray>(
            Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(JsonNode.Parse(checkpoint.Payload)!["Islands"])[0])["Entries"]);
        Assert.True(entries.Count >= 256, $"only {entries.Count} entries; the rebuild would not run in parallel");
        string first = entries[0]!["GenomePayload"]!.GetValue<string>();

        // Every entry fails to decode, on whichever thread reaches it first; the one reported must still be the first.
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() =>
            Engine(Options(resume: true, EvolutionCheckpointFormat.Inline), store, new RefusingCodec()).RunAsync(Seeds()));
        Exception innermost = failure;
        while (innermost.InnerException is not null) innermost = innermost.InnerException;
        Assert.Equal("refused " + first, innermost.Message);
    }

    [Fact]
    public async Task A_resume_decodes_its_archive_inside_a_no_gc_region_and_closes_it()
    {
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionCheckpointStore(directory.Path);
        await Engine(Options(resume: false, EvolutionCheckpointFormat.Segmented), store, new PaddedCodec()).RunAsync(Seeds());

        var codec = new PaddedCodec();
        EvolutionRunResult<TestGenome> resumed =
            await Engine(Options(resume: true, EvolutionCheckpointFormat.Segmented), store, codec).RunAsync(Seeds());

        Assert.True(resumed.Islands.Sum(island => island.Count) >= 256);
        Assert.True(codec.DecodedInRegion >= 256, $"{codec.DecodedInRegion} decoded in a region, {codec.DecodedOutside} outside");
        Assert.Equal(0, codec.DecodedOutside);
        Assert.NotEqual(GCLatencyMode.NoGCRegion, GCSettings.LatencyMode);
    }

    [Theory]
    [InlineData(10L << 20, 64L << 30, 80L << 20)] // Small checkpoint: eight times its segments.
    [InlineData(4L << 30, 16L << 30, 4L << 30)] // Large checkpoint: a quarter of the memory the process may use.
    [InlineData(long.MaxValue / 2, 16L << 30, 4L << 30)] // No overflow past the cap.
    [InlineData(10L << 20, 0, 0)] // Nothing known to size against: no region.
    public void A_resume_region_never_commits_more_than_a_quarter_of_available_memory(long segmentBytes, long availableBytes, long expected) =>
        Assert.Equal(expected, EvolutionEngine<TestGenome>.ResumeRegionBudget(segmentBytes, availableBytes));

    [Fact]
    public async Task Turning_the_option_off_keeps_collection_running_during_a_resume()
    {
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionCheckpointStore(directory.Path);
        await Engine(Options(resume: false, EvolutionCheckpointFormat.Segmented), store, new PaddedCodec()).RunAsync(Seeds());

        var codec = new PaddedCodec();
        EvolutionEngineOptions options = Options(resume: true, EvolutionCheckpointFormat.Segmented);
        options.SuspendGarbageCollectionDuringResume = false;
        await Engine(options, store, codec).RunAsync(Seeds());

        Assert.Equal(0, codec.DecodedInRegion);
        Assert.True(codec.DecodedOutside >= 256);
    }

    [Theory]
    [InlineData(EvolutionCheckpointFormat.Inline)]
    [InlineData(EvolutionCheckpointFormat.Segmented)]
    public async Task A_utf8_codec_is_handed_every_archived_payload_as_bytes_and_restores_what_the_string_codec_restores(
        EvolutionCheckpointFormat format)
    {
        // Two identical stores, so neither resume reads a checkpoint the other wrote.
        using var first = new TemporaryDirectory();
        using var second = new TemporaryDirectory();
        var byStringStore = new DirectoryEvolutionCheckpointStore(first.Path);
        var byBytesStore = new DirectoryEvolutionCheckpointStore(second.Path);
        await Engine(Options(resume: false, format), byStringStore, new EscapedCodec()).RunAsync(Seeds());
        await Engine(Options(resume: false, format), byBytesStore, new EscapedCodec()).RunAsync(Seeds());

        EvolutionRunResult<TestGenome> byString =
            await Engine(Options(resume: true, format), byStringStore, new EscapedCodec()).RunAsync(Seeds());
        var codec = new Utf8EscapedCodec();
        EvolutionRunResult<TestGenome> byBytes = await Engine(Options(resume: true, format), byBytesStore, codec).RunAsync(Seeds());

        Assert.True(codec.Utf8Decodes >= 256, $"{codec.Utf8Decodes} payloads decoded from bytes, {codec.StringDecodes} from strings");
        Assert.Equal(Restored(byString), Restored(byBytes));
    }

    [Theory]
    [InlineData("{\"GenomeId\":\"g\",\"GenomePayload\":\"lone \\ud800 surrogate\"}")]
    [InlineData("{\"GenomeId\":\"g\",\"GenomePayload\":1}")]
    public void An_archived_payload_that_is_not_text_is_refused_when_its_segment_is_read(string json) =>
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(Encoding.UTF8.GetBytes(json), EvolutionStateJsonContext.Default.ArchiveEntryDocument));

    [Fact]
    public void An_archived_payload_that_is_not_utf8_is_refused_when_its_segment_is_read()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\"GenomeId\":\"g\",\"GenomePayload\":\"bad X byte\"}");
        json[Array.IndexOf(json, (byte)'X')] = 0xFF;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, EvolutionStateJsonContext.Default.ArchiveEntryDocument));
    }

    private static string[] Restored(EvolutionRunResult<TestGenome> result) =>
        result.Islands.SelectMany(island => island.Entries)
            .Select(entry => $"{entry.Evaluation.GenomeId}={entry.Candidate.CanonicalGenome.Genome.Value}@{entry.Evaluation.Quality}")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToArray();

    // Proposes 1, 2, 3, ... so every evaluation is a new genome; each value v lands in its own cell.
    private sealed class CountingVariation : IVariationOperator<TestGenome>
    {
        private int _next;
        public string Id => "counting";
        public string VersionHash => "counting-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome(Interlocked.Increment(ref _next)));
    }

    private sealed class WideTask : IEvolutionTask<TestGenome>
    {
        public string Id => "wide";
        public string VersionHash => "wide-task-v1";
        public string EvaluatorVersionHash => "wide-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            return new(EvolutionTaskResult.Completed(value, new Dictionary<string, double> { ["x"] = value + 0.5 }));
        }
    }

    // The checkpoint's codec identity (it is part of the compatibility hash), refusing every payload it is asked to decode.
    private sealed class RefusingCodec : IEvolutionGenomeCodec<TestGenome>
    {
        public string Id => "int";
        public string VersionHash => "int-v1";
        public string Serialize(TestGenome genome) => genome.Value.ToString(CultureInfo.InvariantCulture);
        public TestGenome Deserialize(string payload) => throw new FormatException("refused " + payload);
    }

    // 8 KB payloads, so the segments are large enough for the engine to open a region (it skips small resumes), and a
    // record of whether each decode ran inside one.
    private sealed class PaddedCodec : IEvolutionGenomeCodec<TestGenome>
    {
        private static readonly string Padding = new('p', 8 * 1024);
        private int _decodedInRegion;
        private int _decodedOutside;

        public string Id => "padded-int";
        public string VersionHash => "padded-int-v1";
        public int DecodedInRegion => Volatile.Read(ref _decodedInRegion);
        public int DecodedOutside => Volatile.Read(ref _decodedOutside);

        public string Serialize(TestGenome genome) => genome.Value.ToString(CultureInfo.InvariantCulture) + ":" + Padding;

        public TestGenome Deserialize(string payload)
        {
            if (GCSettings.LatencyMode == GCLatencyMode.NoGCRegion) Interlocked.Increment(ref _decodedInRegion);
            else Interlocked.Increment(ref _decodedOutside);
            return new TestGenome(int.Parse(payload.Substring(0, payload.IndexOf(':')), CultureInfo.InvariantCulture));
        }
    }

    // Payloads holding a quote, a backslash, a non-ASCII letter and an astral character, which the checkpoint writes as
    // escapes, so reading them as bytes has to unescape them exactly as reading them as strings does.
    private class EscapedCodec : IEvolutionGenomeCodec<TestGenome>
    {
        private const string Prefix = "\"\\\u00e9\U0001F680<";

        public string Id => "escaped-int";
        public string VersionHash => "escaped-int-v1";
        public string Serialize(TestGenome genome) => Prefix + genome.Value.ToString(CultureInfo.InvariantCulture) + ">";

        public TestGenome Deserialize(string payload)
        {
            if (!payload.StartsWith(Prefix, StringComparison.Ordinal) || !payload.EndsWith(">", StringComparison.Ordinal))
                throw new FormatException("not an escaped payload: " + payload);
            return new TestGenome(int.Parse(payload.Substring(Prefix.Length, payload.Length - Prefix.Length - 1), CultureInfo.InvariantCulture));
        }
    }

    private sealed class Utf8EscapedCodec : EscapedCodec, IEvolutionUtf8GenomeCodec<TestGenome>
    {
        private int _utf8Decodes;
        private int _stringDecodes;

        public int Utf8Decodes => Volatile.Read(ref _utf8Decodes);
        public int StringDecodes => Volatile.Read(ref _stringDecodes);

        TestGenome IEvolutionGenomeCodec<TestGenome>.Deserialize(string payload)
        {
            Interlocked.Increment(ref _stringDecodes);
            return Deserialize(payload);
        }

        public TestGenome Deserialize(ReadOnlySpan<byte> utf8Payload)
        {
            Interlocked.Increment(ref _utf8Decodes);
            return Deserialize(Encoding.UTF8.GetString(utf8Payload.ToArray()));
        }
    }

    private static TestGenome[] Seeds() => new[] { new TestGenome(0) };

    private static EvolutionEngineOptions Options(bool resume, EvolutionCheckpointFormat format) => new()
    {
        RunId = RunId,
        Seed = 5,
        MaxEvaluationAttempts = Elites,
        MaxProposals = Elites * 2,
        MaxGenerations = Elites,
        ProposalBatchSize = 16,
        MaxDegreeOfParallelism = 1,
        MigrationInterval = 0,
        CheckpointInterval = 64,
        CheckpointFormat = format,
        Resume = resume
    };

    private static EvolutionEngine<TestGenome> Engine(EvolutionEngineOptions options, IEvolutionCheckpointStore store,
        IEvolutionGenomeCodec<TestGenome> codec) =>
        new(new WideTask(), new CountingVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 400, 400) }), options,
            checkpointStore: store, genomeCodec: codec);
}
