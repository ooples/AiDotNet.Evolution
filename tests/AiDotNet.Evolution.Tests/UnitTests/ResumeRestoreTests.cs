using System.Globalization;
using System.Runtime;
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
