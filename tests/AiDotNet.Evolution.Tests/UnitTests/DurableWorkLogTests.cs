using System.Text;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>The coordinator over the append-only, group-committed work log (#119): replay equivalence, torn and corrupt
/// records, old-store compatibility, per-lease cost independent of retained work, and acknowledgement only after
/// durability.</summary>
public sealed class DurableWorkLogTests
{
    private const int RecordHeaderBytes = 56; // magic 4, sequence 8, length 4, header hash 8, payload hash 32

    [Fact]
    public void ReplayAndCompactionRebuildExactlyTheStateTheWholeStateRewriteStored()
    {
        using var fixture = new Fixture();
        string expected;
        string resources;
        using (var coordinator = fixture.Open(13))
        {
            Drive(fixture, coordinator);
            expected = coordinator.CaptureDurableState();
            resources = Describe(coordinator.Resources);
            Assert.Equal(1, coordinator.Journal.Revision); // every mutation was a record; the snapshot was never rewritten
            Assert.True(coordinator.Journal.LastSequence > 10);
        }
        using (var replayed = fixture.Open(13))
        {
            Assert.True(replayed.WasRecovered);
            Assert.NotEmpty(replayed.Journal.RecoveredRecords);
            Assert.Equal(expected, replayed.CaptureDurableState());
            Assert.Equal(resources, Describe(replayed.Resources));
            replayed.CompactJournal();
            Assert.Equal(expected, replayed.CaptureDurableState());
        }
        using var compacted = fixture.Open(13);
        Assert.Empty(compacted.Journal.RecoveredRecords);
        Assert.Equal(expected, compacted.CaptureDurableState());
        Assert.Equal(resources, Describe(compacted.Resources));
        // The replayed state is live, not just readable: the accepted results and the fence on the stale lease hold.
        Assert.Equal("second", compacted.GetResult(2, 1)?.Payload);
        Assert.Null(compacted.GetResult(3, 1));
    }

    [Fact]
    public void StoreLeftByTheWholeStateRewriteLoadsWithItsStateIntact()
    {
        using var fixture = new Fixture();
        string expected;
        using (var coordinator = fixture.Open(13))
        {
            Drive(fixture, coordinator);
            coordinator.CompactJournal();
            expected = coordinator.CaptureDurableState();
        }
        // Exactly what the previous release left on disk: the same work.current format, an AEVWORK1 owner marker, no log.
        File.Delete(LogPath(fixture));
        File.WriteAllBytes(OwnerPath(fixture), Encoding.ASCII.GetBytes("AEVWORK1"));
        using (var upgraded = fixture.Open(13))
        {
            Assert.True(upgraded.WasRecovered);
            Assert.Equal(expected, upgraded.CaptureDurableState());
            Assert.True(fixture.Enqueue(upgraded, 9));
        }
        // Read once the store is closed: the open coordinator holds work.owner exclusively.
        Assert.Equal("AEVWORK2", Encoding.ASCII.GetString(File.ReadAllBytes(OwnerPath(fixture))));
        using var reopened = fixture.Open(13);
        Assert.False(fixture.Enqueue(reopened, 9)); // the record appended after the upgrade survived
    }

    [Theory]
    [InlineData(1)]
    [InlineData(RecordHeaderBytes - 1)]
    [InlineData(RecordHeaderBytes)]
    [InlineData(RecordHeaderBytes + 1)]
    public void TornLastRecordRecoversThePreviousAcknowledgedState(int keptBytesOfLastRecord)
    {
        using var fixture = new Fixture();
        string beforeLast;
        long complete;
        EvolutionWorkLease lease;
        using (var coordinator = fixture.Open())
        {
            fixture.Enqueue(coordinator); fixture.Enqueue(coordinator, 2);
            lease = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("a")));
            beforeLast = coordinator.CaptureDurableState();
            complete = LogLength(fixture);
            Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(lease.Identity, "a", "result", "receipt", Cost(2)));
            Assert.True(LogLength(fixture) > complete + RecordHeaderBytes + 1);
        }
        // A kill during the commit's write leaves a prefix of its record. It was never flushed, so never acknowledged.
        using (var log = new FileStream(LogPath(fixture), FileMode.Open)) log.SetLength(complete + keptBytesOfLastRecord);
        using (var recovered = fixture.Open())
        {
            Assert.Equal(beforeLast, recovered.CaptureDurableState());
            Assert.Equal(complete, LogLength(fixture));
            Assert.Equal(0, recovered.Resources.Spent["cost_units"]); Assert.Equal(5, recovered.Resources.Reserved["cost_units"]);
            // The worker resends its persisted receipt and the original lease settles exactly once.
            Assert.Equal(EvolutionWorkCommitDisposition.Accepted, recovered.Commit(lease.Identity, "a", "result", "receipt", Cost(2)));
        }
        using var reopened = fixture.Open();
        Assert.Equal("result", reopened.GetResult(1, 1)?.Payload);
        Assert.Equal(2, reopened.Resources.Spent["cost_units"]); Assert.Equal(1, reopened.Resources.Settled);
    }

    [Fact]
    public void CorruptAcknowledgedRecordIsRefusedRatherThanDropped()
    {
        using var fixture = new Fixture();
        using (var coordinator = fixture.Open())
        {
            fixture.Enqueue(coordinator);
            Assert.NotNull(coordinator.Claim(fixture.Worker("a")));
        }
        byte[] bytes = ReadShared(LogPath(fixture));
        bytes[RecordHeaderBytes + 3] ^= 0x40; // inside the first record's payload, with a complete record after it
        File.WriteAllBytes(LogPath(fixture), bytes);
        Assert.Throws<InvalidDataException>(() => fixture.Open());
        Assert.Throws<InvalidDataException>(() => fixture.Open());
    }

    [Fact]
    public void PerLeaseLogCostDoesNotGrowWithRetainedWork()
    {
        // The defect #119 measured: every claim and commit rewrote the whole retained state, so p95 grew with retained
        // work. A lease now appends records about that lease only.
        using var fixture = new Fixture(new EvolutionWorkCoordinatorOptions(256, 3, 16 * 1024 * 1024, 4096, 8, TimeSpan.FromSeconds(10)));
        using var coordinator = fixture.Open(100_000);
        for (long id = 1; id <= 200; id++) fixture.Enqueue(coordinator, id);
        long revision = coordinator.Journal.Revision;
        LeaseBytes(fixture, coordinator); // the first claim also records the new worker's profile
        long early = LeaseBytes(fixture, coordinator);
        for (int i = 0; i < 196; i++) LeaseBytes(fixture, coordinator);
        long late = LeaseBytes(fixture, coordinator);
        Assert.Equal(revision, coordinator.Journal.Revision); // no snapshot was rewritten for any of the 199 leases
        // 197 settled leases later, the bytes one lease appends differ only by the evaluation id's extra digits
        // (six occurrences across the claim and commit records), not by anything retained.
        Assert.InRange(late, early - 32, early + 32);
    }

    [Fact]
    public async Task ConcurrentClaimsShareOneFlushAndNoneIsAcknowledgedBeforeItIsDurable()
    {
        using var fixture = new Fixture();
        using var coordinator = fixture.Open();
        for (long id = 1; id <= 3; id++) fixture.Enqueue(coordinator, id);
        long flushes = coordinator.Journal.LogFlushes;
        using var leaderFlushing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        coordinator.Publishing = published =>
        {
            if (!published && Interlocked.Increment(ref calls) == 1) { leaderFlushing.Set(); release.Wait(); }
        };
        Task<EvolutionWorkLease?> first = Task.Run(() => coordinator.Claim(fixture.Worker("a")));
        Assert.True(leaderFlushing.Wait(TimeSpan.FromSeconds(30)));
        long appended = coordinator.Journal.LastSequence;

        // The first claim is waiting for its flush OUTSIDE the coordinator lock, so these two take effect meanwhile...
        Task<EvolutionWorkLease?> second = Task.Run(() => coordinator.Claim(fixture.Worker("b")));
        Task<EvolutionWorkLease?> third = Task.Run(() => coordinator.Claim(fixture.Worker("c")));
        Assert.True(SpinWait.SpinUntil(() => coordinator.Journal.LastSequence == appended + 2, TimeSpan.FromSeconds(30)));
        // ...but neither returns while its record is only buffered.
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); Assert.False(third.IsCompleted);

        release.Set();
        await Within(Task.WhenAll(first, second, third));
        EvolutionWorkLease?[] leases = { await first, await second, await third };
        Assert.All(leases, lease => Assert.NotNull(lease));
        Assert.Equal(3, leases.Select(lease => lease?.Identity.EvaluationId).Distinct().Count());
        Assert.Equal(flushes + 2, coordinator.Journal.LogFlushes); // the two later claims shared one flush
    }

    [Fact]
    public async Task ProtocolEndpointRepliesOnlyAfterDurabilityButDoesNotHoldItsLockWhileWaiting()
    {
        using var fixture = new Fixture();
        using var coordinator = fixture.Open();
        for (long id = 1; id <= 2; id++) fixture.Enqueue(coordinator, id);
        using var endpoint = new EvolutionWorkProtocol(coordinator);
        using var leaderFlushing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        coordinator.Publishing = published =>
        {
            if (!published && Interlocked.Increment(ref calls) == 1) { leaderFlushing.Set(); release.Wait(); }
        };
        string Claim(int id, string worker) => endpoint.ProcessJson("{\"id\":" + id + ",\"protocol\":1,\"op\":\"claim\",\"worker\":{\"workerId\":\""
            + worker + "\",\"compatibilityHash\":\"" + fixture.Compatibility + "\"}}");
        Task<string> first = Task.Run(() => Claim(1, "a"));
        Assert.True(leaderFlushing.Wait(TimeSpan.FromSeconds(30)));
        long appended = coordinator.Journal.LastSequence;
        Task<string> second = Task.Run(() => Claim(2, "b"));
        // A second connection's command runs while the first reply is still waiting for its flush.
        Assert.True(SpinWait.SpinUntil(() => coordinator.Journal.LastSequence == appended + 1, TimeSpan.FromSeconds(30)));
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        release.Set();
        await Within(Task.WhenAll(first, second));
        Assert.Contains("\"available\":true", await first, StringComparison.Ordinal);
        Assert.Contains("\"available\":true", await second, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolReportsAFailedFlushAsAnErrorReplyNotASuccess()
    {
        using var fixture = new Fixture();
        using var coordinator = fixture.Open();
        fixture.Enqueue(coordinator);
        using var endpoint = new EvolutionWorkProtocol(coordinator);
        coordinator.Publishing = published => { if (!published) throw new IOException("injected flush failure"); };
        string reply = endpoint.ProcessJson("{\"id\":7,\"protocol\":1,\"op\":\"claim\",\"worker\":{\"workerId\":\"a\",\"compatibilityHash\":\""
            + fixture.Compatibility + "\"}}");
        Assert.Contains("\"id\":7", reply, StringComparison.Ordinal);
        Assert.Contains("\"ok\":false", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("\"lease\"", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void StateByteLimitRefusalUndoesTheLedgerAndLeavesTheCoordinatorUsable()
    {
        using var fixture = new Fixture(new EvolutionWorkCoordinatorOptions(16, 3, 4096, 4096, 8, TimeSpan.FromSeconds(10)));
        EvolutionWorkLease lease;
        string before;
        using (var coordinator = fixture.Open())
        {
            fixture.Enqueue(coordinator);
            lease = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("a")));
            before = coordinator.CaptureDurableState();
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() =>
                coordinator.Commit(lease.Identity, "a", new string('r', 4000), "receipt", Cost(2)));
            Assert.Contains("byte limit", refused.Message, StringComparison.Ordinal);
            // The settlement was undone exactly: nothing charged, the reservation still held, nothing logged.
            Assert.Equal(before, coordinator.CaptureDurableState());
            Assert.Equal(0, coordinator.Resources.Spent["cost_units"]); Assert.Equal(5, coordinator.Resources.Reserved["cost_units"]);
            Assert.Equal(0, coordinator.Resources.Settled); Assert.Empty(coordinator.Resources.Receipts);
            Assert.Null(coordinator.GetDeliveryResult(lease.Identity, "a"));
            Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(lease.Identity, "a", "small", "receipt", Cost(2)));
        }
        using var reopened = fixture.Open();
        Assert.Equal("small", reopened.GetResult(1, 1)?.Payload);
        Assert.Equal(1, reopened.Resources.Settled);
    }

    /// <summary>Enqueue, claim, deny, renew, expire, re-lease, stale and accepted receipts, cancellation and an unknown
    /// lease, with a fractional charge so decimal scale has to survive replay.</summary>
    private static void Drive(Fixture fixture, DurableEvolutionWorkCoordinator coordinator)
    {
        for (long id = 1; id <= 3; id++) Assert.True(fixture.Enqueue(coordinator, id));
        EvolutionWorkLease a = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("a")));
        EvolutionWorkLease b = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("b")));
        Assert.Null(coordinator.Claim(fixture.Worker("c"))); // 5 + 5 + 5 > 13: a durable denial
        fixture.Advance(5);
        Assert.Equal(EvolutionWorkHeartbeat.Renewed, coordinator.Heartbeat(a.Identity, "a"));
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(a.Identity, "a", "first", "receipt-a", Cost(2.50m)));
        fixture.Advance(6); // b's lease has expired
        EvolutionWorkLease retry = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("c")));
        Assert.Equal(b.Identity.EvaluationId, retry.Identity.EvaluationId);
        Assert.Equal(EvolutionWorkCommitDisposition.Stale, coordinator.Commit(b.Identity, "b", "late", "receipt-b", Cost(1)));
        Assert.True(coordinator.Cancel(3, 1));
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(retry.Identity, "c", "second", "receipt-c", Cost(0.10m)));
        Assert.Equal(EvolutionWorkCommitDisposition.UnknownLease,
            coordinator.Commit(new EvolutionWorkIdentity(fixture.RunId, 3, 1, Guid.NewGuid().ToString("N")), "c", "x", "x", Cost(1)));
    }

    private static async Task Within(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(task, finished);
        await task;
    }

    private static long LeaseBytes(Fixture fixture, DurableEvolutionWorkCoordinator coordinator)
    {
        long start = LogLength(fixture);
        EvolutionWorkLease lease = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("a")));
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(lease.Identity, "a", "result", "receipt", Cost(1)));
        return LogLength(fixture) - start;
    }

    private static string Describe(EvolutionResourceSnapshot resources) => string.Join("|",
        string.Join(",", resources.Spent.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        string.Join(",", resources.Reserved.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))),
        string.Join(",", resources.Receipts.Select(r => r.OperationId)),
        resources.Admitted, resources.Settled, resources.Denied, resources.Unknown, resources.MaximumViolated);

    // The live store holds the log open for writing, so reads must share write access; a handle-based length is
    // also exact on Windows, where a directory entry's size can lag an open file.
    private static long LogLength(Fixture fixture)
    {
        using var stream = new FileStream(LogPath(fixture), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string LogPath(Fixture fixture) => Path.Combine(fixture.Path, "work.log");
    private static string OwnerPath(Fixture fixture) => Path.Combine(fixture.Path, "work.owner");
    private static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);

    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evolution-work-log-" + Guid.NewGuid().ToString("N"));
        internal string RunId => "durable-log-test";
        internal string Compatibility => "task-evaluator-codec-v1";
        internal DateTimeOffset Now { get; private set; } = new(2026, 10, 13, 0, 0, 0, TimeSpan.Zero);
        internal EvolutionWorkCoordinatorOptions Options { get; }
        internal Fixture(EvolutionWorkCoordinatorOptions? options = null) => Options = options ?? new(16, 3, 256 * 1024, 4096, 8, TimeSpan.FromSeconds(10));
        internal void Advance(int seconds) => Now = Now.AddSeconds(seconds);
        internal DurableEvolutionWorkCoordinator Open(decimal cap = 100) => new(Path, RunId, Compatibility, Cost(cap), Options, () => Now);
        internal EvolutionWorkerProfile Worker(string id) => new(id, Compatibility);
        internal bool Enqueue(DurableEvolutionWorkCoordinator coordinator, long id = 1) =>
            coordinator.Enqueue(id, 1, "genome-" + id, "same display", new(), Cost(1), Cost(5));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
