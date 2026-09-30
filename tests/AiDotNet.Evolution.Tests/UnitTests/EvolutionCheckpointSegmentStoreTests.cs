using System.Text;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-73 (#178): the store half of segmented checkpoints.</summary>
public sealed class EvolutionCheckpointSegmentStoreTests
{
    public static TheoryData<string> Stores => new() { "memory", "directory" };

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_segment_round_trips_and_rewriting_its_identifier_replaces_it(string kind)
    {
        using var directory = new TemporaryDirectory();
        IEvolutionCheckpointSegmentStore store = Create(kind, directory.Path);

        Assert.False(await Exists(store, "run", 3));
        await store.WriteSegmentAsync("run", 3, Text("first"));
        Assert.Equal("first", await Read(store, "run", 3));
        await store.WriteSegmentAsync("run", 3, Text("second"));
        Assert.Equal("second", await Read(store, "run", 3));
        Assert.False(await Exists(store, "other-run", 3));
    }

    [Theory]
    [MemberData(nameof(Stores))]
    public async Task A_checkpoint_keeps_the_segments_it_names_through_a_save_and_a_load(string kind)
    {
        using var directory = new TemporaryDirectory();
        IEvolutionCheckpointSegmentStore store = Create(kind, directory.Path);
        await store.WriteSegmentAsync("run", 0, Text("base"));
        await store.WriteSegmentAsync("run", 1, Text("delta"));

        await store.SaveAsync(Checkpoint(1, 1.0, 0, 1));
        EvolutionCheckpoint loaded = Assert.IsType<EvolutionCheckpoint>(await store.LoadLatestAsync("run"));

        Assert.Equal(new long[] { 0, 1 }, loaded.SegmentIds);
        Assert.Equal("base", await Read(store, "run", 0));
        Assert.Equal("delta", await Read(store, "run", 1));
    }

    [Fact]
    public async Task The_memory_store_drops_segments_its_latest_checkpoint_no_longer_names()
    {
        var store = new InMemoryEvolutionCheckpointStore();
        for (long id = 0; id < 3; id++) await store.WriteSegmentAsync("run", id, Text("s" + id));
        await store.SaveAsync(Checkpoint(1, 1.0, 0, 1, 2));
        await store.WriteSegmentAsync("run", 3, Text("new base"));
        await store.SaveAsync(Checkpoint(2, 1.0, 3));

        Assert.False(await Exists(store, "run", 0));
        Assert.False(await Exists(store, "run", 2));
        Assert.Equal("new base", await Read(store, "run", 3));
    }

    [Fact]
    public async Task Directory_retention_keeps_every_segment_a_retained_snapshot_names_and_no_other()
    {
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionCheckpointStore(directory.Path,
            new EvolutionCheckpointRetentionOptions { KeepLast = 2, KeepBest = 1 });
        // Snapshot 1 is the best, so it is retained with the two newest; snapshots 2 and 3 are pruned.
        (long Sequence, double Quality, long[] Segments)[] saves =
        {
            (1, 10.0, new long[] { 0 }),
            (2, 1.0, new long[] { 0, 1 }),
            (3, 1.0, new long[] { 2 }),
            (4, 1.0, new long[] { 3 }),
            (5, 1.0, new long[] { 3, 4 })
        };
        foreach ((long sequence, double quality, long[] segments) in saves)
        {
            foreach (long id in segments) await store.WriteSegmentAsync("run", id, Text("s" + id));
            await store.SaveAsync(Checkpoint(sequence, quality, segments));
        }
        // A segment newer than every named one may belong to a save in progress, so it is left alone.
        await store.WriteSegmentAsync("run", 9, Text("in flight"));
        await store.WriteSegmentAsync("run", 5, Text("s5"));
        await store.SaveAsync(Checkpoint(6, 1.0, 3, 4, 5));

        Assert.True(await Exists(store, "run", 0));
        Assert.False(await Exists(store, "run", 1));
        Assert.False(await Exists(store, "run", 2));
        foreach (long kept in new long[] { 3, 4, 5, 9 }) Assert.True(await Exists(store, "run", kept), "segment " + kept);
    }

    [Fact]
    public void Segment_identifiers_must_increase_and_be_bounded()
    {
        EvolutionCheckpoint plain = new("run", 1, "compatibility", "payload");
        Assert.Empty(plain.SegmentIds);
        Assert.Equal(new long[] { 0, 5 }, plain.WithSegmentIds(new long[] { 0, 5 }).SegmentIds);
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.WithSegmentIds(new long[] { 2, 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.WithSegmentIds(new long[] { 3, 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => plain.WithSegmentIds(new long[] { -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            plain.WithSegmentIds(Enumerable.Range(0, EvolutionCheckpoint.MaximumSegmentCount + 1).Select(i => (long)i).ToArray()));
        Assert.Throws<ArgumentNullException>(() => plain.WithSegmentIds(null!));
    }

    [Fact]
    public async Task A_payload_with_every_character_that_needs_escaping_round_trips_exactly()
    {
        // The directory store escapes the payload itself, in 4K chunks; a surrogate pair straddles the first boundary.
        var text = new StringBuilder();
        text.Append('x', 4 * 1024 - 1).Append("\U0001F600");
        for (int i = 0; i < 40_000; i++) text.Append("q\"b\\n\n r\r t\t c\u0001 e\u00e9 z\u4e2d s\U0001F680|");
        string payload = text.ToString();
        using var directory = new TemporaryDirectory();
        await new DirectoryEvolutionCheckpointStore(directory.Path).SaveAsync(new EvolutionCheckpoint("run", 1, "compatibility", payload));

        EvolutionCheckpoint loaded = Assert.IsType<EvolutionCheckpoint>(await new DirectoryEvolutionCheckpointStore(directory.Path).LoadLatestAsync("run"));
        Assert.Equal(payload, loaded.Payload);
        loaded.Validate();
    }

    [Fact]
    public async Task A_snapshot_written_without_segments_keeps_verifying()
    {
        // Segment identifiers join the snapshot checksum only when present, so pre-segment snapshots still load.
        using var directory = new TemporaryDirectory();
        var store = new DirectoryEvolutionCheckpointStore(directory.Path);
        await store.SaveAsync(new EvolutionCheckpoint("run", 1, "compatibility", "payload"));
        EvolutionCheckpoint loaded = Assert.IsType<EvolutionCheckpoint>(await new DirectoryEvolutionCheckpointStore(directory.Path).LoadLatestAsync("run"));
        Assert.Empty(loaded.SegmentIds);
        Assert.Equal("payload", loaded.Payload);
    }

    private static IEvolutionCheckpointSegmentStore Create(string kind, string path) => kind == "memory"
        ? new InMemoryEvolutionCheckpointStore()
        : new DirectoryEvolutionCheckpointStore(path);

    private static EvolutionCheckpoint Checkpoint(long sequence, double quality, params long[] segments) =>
        new EvolutionCheckpoint("run", sequence, "compatibility", "payload-" + sequence,
            EvolutionCheckpoint.CurrentSchemaVersion, quality).WithSegmentIds(segments);

    private static Func<Stream, CancellationToken, Task> Text(string text) =>
        (stream, token) => stream.WriteAsync(Encoding.UTF8.GetBytes(text), 0, Encoding.UTF8.GetByteCount(text), token);

    private static async Task<bool> Exists(IEvolutionCheckpointSegmentStore store, string runId, long id)
    {
        using Stream? stream = await store.OpenSegmentAsync(runId, id);
        return stream is not null;
    }

    private static async Task<string> Read(IEvolutionCheckpointSegmentStore store, string runId, long id)
    {
        using Stream stream = Assert.IsAssignableFrom<Stream>(await store.OpenSegmentAsync(runId, id));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }
}