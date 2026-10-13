using System.Text;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionWorkJournalTests
{
    [Fact]
    public void ReopenLoadsOnlyPublishedStateAndPreservesUnicode()
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "initial", 1024))
        {
            Assert.False(journal.WasRecovered); Assert.Equal(1, journal.Revision);
            journal.Commit("lease-1: é😀"); Assert.Equal(2, journal.Revision);
        }
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "work-orphan.tmp"), "not committed");
        using var recovered = new EvolutionWorkJournal(directory.Path, "must not overwrite", 1024);
        Assert.True(recovered.WasRecovered); Assert.Equal(2, recovered.Revision);
        Assert.Equal("lease-1: é😀", recovered.Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedAcknowledgementRequiresReopenAndRetainsCorrectPublication(bool afterPublish)
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "before", 1024))
        {
            journal.Publishing = published => { if (published == afterPublish) throw new IOException("injected crash boundary"); };
            Assert.Throws<IOException>(() => journal.Commit("after"));
            Assert.Throws<InvalidOperationException>(() => journal.Commit("must not continue"));
        }
        using var recovered = new EvolutionWorkJournal(directory.Path, "unused", 1024);
        Assert.Equal(afterPublish ? "after" : "before", recovered.Payload);
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
    }

    [Theory]
    [InlineData(0)] // magic
    [InlineData(8)] // revision is covered by the checksum
    [InlineData(16)] // declared length
    [InlineData(20)] // checksum
    [InlineData(52)] // payload
    public void CorruptLatestStateNeverFallsBack(int offset)
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "initial", 1024)) journal.Commit("reserved-work");
        string path = System.IO.Path.Combine(directory.Path, "work.current");
        byte[] bytes = File.ReadAllBytes(path); bytes[offset] ^= 0x40; File.WriteAllBytes(path, bytes);
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "work-older.tmp"), "initial");
        Assert.Throws<InvalidDataException>(() => new EvolutionWorkJournal(directory.Path, "fresh", 1024));
        // The failed constructor releases its lock; it still cannot silently overwrite damaged data.
        Assert.Throws<InvalidDataException>(() => new EvolutionWorkJournal(directory.Path, "fresh", 1024));
    }

    [Fact]
    public void TemporaryCleanupFailureIsObservableWithoutReplacingPublicationFailure()
    {
        using var directory = new WorkDirectory();
        var failure = new IOException("original publication failure");
        using (var journal = new EvolutionWorkJournal(directory.Path, "before", 1024))
        {
            Assert.False(journal.HasTemporaryCleanupFailure);
            journal.Publishing = published =>
            {
                Assert.False(published);
                string temporary = Assert.Single(Directory.GetFiles(directory.Path, "work-*.tmp"));
                // The exact file created by this commit only. A directory at that name makes
                // File.Delete fail on Windows and Unix without relying on platform file locks.
                File.Delete(temporary);
                Directory.CreateDirectory(temporary);
                throw failure;
            };
            Assert.Same(failure, Assert.Throws<IOException>(() => journal.Commit("after")));
            Assert.True(journal.HasTemporaryCleanupFailure);
            Assert.Throws<InvalidOperationException>(() => journal.Commit("cannot continue"));
        }
        using var recovered = new EvolutionWorkJournal(directory.Path, "unused", 1024);
        Assert.Equal("before", recovered.Payload);
        Assert.False(recovered.HasTemporaryCleanupFailure);
        Assert.Single(Directory.GetDirectories(directory.Path, "work-*.tmp"));
    }

    [Fact]
    public void MissingPublishedStateCannotBeMistakenForANewRun()
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "reserved-work", 1024)) { }
        File.Delete(System.IO.Path.Combine(directory.Path, "work.current"));
        Assert.Throws<FileNotFoundException>(() => new EvolutionWorkJournal(directory.Path, "fresh", 1024));
    }

    [Fact]
    public void ExclusiveOwnershipAndUtf8ByteLimitAreEnforced()
    {
        using var directory = new WorkDirectory();
        using var journal = new EvolutionWorkJournal(directory.Path, "initial", 1024);
        Assert.Throws<IOException>(() => new EvolutionWorkJournal(directory.Path, "other owner", 1024));
        Assert.Throws<InvalidOperationException>(() => journal.Commit(new string('é', 513)));
        journal.Commit(new string('é', 512));
        journal.Dispose();
        Assert.Throws<ObjectDisposedException>(() => journal.Commit("disposed"));
    }

    // ---- Append-only log and group commit (#119) ----

    private const int RecordHeaderBytes = 48;

    [Fact]
    public void AppendedRecordsBecomeDurableInOrderAndReplayAfterReopen()
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "snapshot", 4096))
        {
            long first = journal.Append("first");
            long second = journal.Append("second é😀");
            Assert.Equal(2, first); Assert.Equal(3, second);
            // Appending only buffers: nothing is durable, so nothing may be acknowledged yet.
            Assert.Equal(1, journal.DurableSequence);
            Assert.Equal(0, LogLength(directory));
            journal.WaitDurable(second);
            Assert.Equal(3, journal.DurableSequence);
            Assert.Equal(1, journal.LogFlushes);
            journal.WaitDurable(first); // already covered; no second flush
            Assert.Equal(1, journal.LogFlushes);
            // The snapshot is not rewritten by a record; that rewrite is what made every lease cost grow.
            Assert.Equal(1, journal.Revision);
        }
        using var recovered = new EvolutionWorkJournal(directory.Path, "unused", 4096);
        Assert.True(recovered.WasRecovered);
        Assert.Equal("snapshot", recovered.Payload); Assert.Equal(1, recovered.Revision);
        Assert.Equal(new[] { "first", "second é😀" }, recovered.RecoveredRecords);
        Assert.Equal(3, recovered.LastSequence);
        Assert.Equal(4, recovered.Append("third"));
    }

    [Theory]
    [InlineData(1)] // one byte of the header
    [InlineData(RecordHeaderBytes - 1)] // all but one byte of the header
    [InlineData(RecordHeaderBytes)] // a complete header and no payload
    [InlineData(RecordHeaderBytes + 5)] // all but one payload byte ("second" is six bytes)
    public void TornLastRecordIsTruncatedAndTheLogStaysAppendable(int keptBytesOfLastRecord)
    {
        using var directory = new WorkDirectory();
        long complete;
        using (var journal = new EvolutionWorkJournal(directory.Path, "snapshot", 4096))
        {
            journal.WaitDurable(journal.Append("first"));
            complete = LogLength(directory);
            journal.WaitDurable(journal.Append("second"));
        }
        Assert.Equal(complete + RecordHeaderBytes + 6, LogLength(directory));
        // A process killed mid-write leaves a strict prefix of the batch; that record was never flushed, so never acknowledged.
        using (var log = new FileStream(LogPath(directory), FileMode.Open)) log.SetLength(complete + keptBytesOfLastRecord);

        using (var recovered = new EvolutionWorkJournal(directory.Path, "unused", 4096))
        {
            Assert.Equal(new[] { "first" }, recovered.RecoveredRecords);
            Assert.Equal(2, recovered.LastSequence);
            Assert.Equal(complete, LogLength(directory));
            recovered.WaitDurable(recovered.Append("again"));
        }
        using var reopened = new EvolutionWorkJournal(directory.Path, "unused", 4096);
        Assert.Equal(new[] { "first", "again" }, reopened.RecoveredRecords);
    }

    [Theory]
    [InlineData(0, 0)] // magic of the first record
    [InlineData(0, 4)] // sequence
    [InlineData(0, 12)] // declared length
    [InlineData(0, 16)] // header hash
    [InlineData(0, 24)] // payload hash
    [InlineData(0, RecordHeaderBytes)] // payload
    [InlineData(1, 12)] // the last record's length: corruption, not a torn tail
    [InlineData(1, RecordHeaderBytes + 2)] // the last record's payload: complete, so it was flushed and may be acknowledged
    public void CorruptCompleteRecordNeverFallsBack(int record, int offset)
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "snapshot", 4096))
        {
            journal.Append("first-record");
            journal.WaitDurable(journal.Append("last-record"));
        }
        byte[] bytes = ReadShared(LogPath(directory));
        int start = record == 0 ? 0 : RecordHeaderBytes + "first-record".Length;
        bytes[start + offset] ^= 0x40;
        File.WriteAllBytes(LogPath(directory), bytes);
        Assert.Throws<InvalidDataException>(() => new EvolutionWorkJournal(directory.Path, "fresh", 4096));
        // The refusal truncates nothing: a second open is refused the same way.
        Assert.Throws<InvalidDataException>(() => new EvolutionWorkJournal(directory.Path, "fresh", 4096));
        Assert.Equal(bytes.Length, LogLength(directory));
    }

    [Fact]
    public async Task ConcurrentWaitersShareOneFlushAndNoneReturnsBeforeItsRecordIsDurable()
    {
        using var directory = new WorkDirectory();
        using var journal = new EvolutionWorkJournal(directory.Path, "snapshot", 4096);
        using var leaderFlushing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int flushes = 0;
        journal.Publishing = published =>
        {
            if (!published && Interlocked.Increment(ref flushes) == 1) { leaderFlushing.Set(); release.Wait(); }
        };
        long first = journal.Append("r1");
        Task leader = Task.Run(() => journal.WaitDurable(first));
        Assert.True(leaderFlushing.Wait(TimeSpan.FromSeconds(30)));

        // Records appended while the leader's flush is in progress wait for the next flush.
        long[] rest = Enumerable.Range(2, 8).Select(i => journal.Append("r" + i)).ToArray();
        Task[] followers = rest.Select(sequence => Task.Run(() => journal.WaitDurable(sequence))).ToArray();
        Assert.Equal(1, journal.DurableSequence);
        Assert.DoesNotContain(followers, follower => follower.IsCompleted);
        Assert.False(leader.IsCompleted);

        release.Set();
        await Within(Task.WhenAll(followers.Concat(new[] { leader })));
        Assert.Equal(rest[rest.Length - 1], journal.DurableSequence);
        // One flush for the leader's record, ONE for all eight followers: that is the group commit.
        Assert.Equal(2, journal.LogFlushes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedLogFlushFaultsTheStoreAndRecoveryDecidesTheBatch(bool afterFlush)
    {
        using var directory = new WorkDirectory();
        using (var journal = new EvolutionWorkJournal(directory.Path, "snapshot", 4096))
        {
            journal.WaitDurable(journal.Append("kept"));
            long first = journal.Append("batch-1");
            long second = journal.Append("batch-2");
            journal.Publishing = published => { if (published == afterFlush) throw new IOException("injected flush failure"); };
            Assert.Throws<IOException>(() => journal.WaitDurable(second));
            // Neither record of the failed batch is acknowledged, and the store refuses further work.
            Assert.Throws<InvalidOperationException>(() => journal.WaitDurable(first));
            Assert.Throws<InvalidOperationException>(() => journal.Append("after failure"));
            Assert.Throws<InvalidOperationException>(() => journal.Commit("after failure"));
        }
        using var recovered = new EvolutionWorkJournal(directory.Path, "unused", 4096);
        Assert.Equal(afterFlush ? new[] { "kept", "batch-1", "batch-2" } : new[] { "kept" }, recovered.RecoveredRecords);
    }

    [Fact]
    public void DisposeWritesBufferedRecordsBeforeReleasingTheStore()
    {
        using var directory = new WorkDirectory();
        long sequence;
        var journal = new EvolutionWorkJournal(directory.Path, "snapshot", 4096);
        sequence = journal.Append("handed out");
        journal.Dispose();
        journal.WaitDurable(sequence); // durable before disposal finished, so a late waiter is still acknowledged
        Assert.Throws<ObjectDisposedException>(() => journal.Append("disposed"));
        using var recovered = new EvolutionWorkJournal(directory.Path, "unused", 4096);
        Assert.Equal(new[] { "handed out" }, recovered.RecoveredRecords);
    }

    [Fact]
    public void SnapshotFoldsTheLogAndACrashBeforeTruncationOnlyLeavesCoveredRecords()
    {
        using var directory = new WorkDirectory();
        byte[] coveredLog;
        using (var journal = new EvolutionWorkJournal(directory.Path, "snapshot-1", 4096))
        {
            journal.Append("a");
            journal.WaitDurable(journal.Append("b"));
            coveredLog = ReadShared(LogPath(directory));
            journal.Append("c"); // buffered only: the snapshot must make it durable first
            journal.Commit("snapshot-with-a-b-c");
            Assert.Equal(5, journal.Revision);
            Assert.Equal(0, LogLength(directory));
        }
        using (var recovered = new EvolutionWorkJournal(directory.Path, "unused", 4096))
        {
            Assert.Equal("snapshot-with-a-b-c", recovered.Payload);
            Assert.Empty(recovered.RecoveredRecords);
        }
        // Simulate a crash after the snapshot replace but before the log truncation.
        File.WriteAllBytes(LogPath(directory), coveredLog);
        using (var recovered = new EvolutionWorkJournal(directory.Path, "unused", 4096))
        {
            Assert.Empty(recovered.RecoveredRecords);
            Assert.Equal(0, LogLength(directory));
            Assert.Equal(6, recovered.Append("d"));
            recovered.WaitDurable(6);
        }
        using var reopened = new EvolutionWorkJournal(directory.Path, "unused", 4096);
        Assert.Equal(new[] { "d" }, reopened.RecoveredRecords);
        // Covered records followed by newer ones cannot come from any crash; that log is refused, not merged.
        reopened.Dispose();
        File.WriteAllBytes(LogPath(directory), coveredLog.Concat(ReadShared(LogPath(directory))).ToArray());
        Assert.Throws<InvalidDataException>(() => new EvolutionWorkJournal(directory.Path, "unused", 4096));
    }

    [Fact]
    public void VersionOneStoreLoadsAndIsMarkedSoOlderCodeRefusesIt()
    {
        using var directory = new WorkDirectory();
        Directory.CreateDirectory(directory.Path);
        WriteVersionOneStore(directory.Path, 7, "state written by the whole-state rewrite");
        using (var journal = new EvolutionWorkJournal(directory.Path, "must not overwrite", 4096))
        {
            Assert.True(journal.WasRecovered);
            Assert.Equal("state written by the whole-state rewrite", journal.Payload);
            Assert.Equal(7, journal.Revision);
            Assert.Empty(journal.RecoveredRecords);
            // Upgraded before the first record can exist: code that reads only work.current must refuse this store.
            Assert.Equal("AEVWORK2", Encoding.ASCII.GetString(File.ReadAllBytes(OwnerPath(directory))));
            Assert.Equal(8, journal.Append("first record"));
            journal.WaitDurable(8);
        }
        using (var reopened = new EvolutionWorkJournal(directory.Path, "unused", 4096))
        {
            Assert.Equal("state written by the whole-state rewrite", reopened.Payload);
            Assert.Equal(new[] { "first record" }, reopened.RecoveredRecords);
        }
        // A version-1 marker beside a log is not something any release wrote.
        File.WriteAllBytes(OwnerPath(directory), Encoding.ASCII.GetBytes("AEVWORK1"));
        Assert.Throws<InvalidDataException>(() => new EvolutionWorkJournal(directory.Path, "unused", 4096));
    }

#if NET
    [Fact]
    public void RetainedVersionOneEvidenceStoresStillLoad()
    {
        // Real stores written by the whole-state-rewrite release (8aaef1e) during its process-kill recovery run.
        string archivePath = System.IO.Path.Combine(RepositoryRoot(), "benchmarks", "evidence", "external-work", "8aaef1e", "verification.zip");
        using var archive = System.IO.Compression.ZipFile.OpenRead(archivePath);
        string[] stores = archive.Entries.Where(entry => entry.FullName.EndsWith("/work.current", StringComparison.Ordinal))
            .Select(entry => entry.FullName.Substring(0, entry.FullName.Length - "work.current".Length)).ToArray();
        Assert.NotEmpty(stores);
        foreach (string store in stores)
        {
            using var directory = new WorkDirectory();
            Directory.CreateDirectory(directory.Path);
            byte[] current = Read(archive, store + "work.current");
            File.WriteAllBytes(System.IO.Path.Combine(directory.Path, "work.current"), current);
            File.WriteAllBytes(OwnerPath(directory), Read(archive, store + "work.owner"));
            string payload = new UTF8Encoding(false, true).GetString(current, 52, current.Length - 52);
            using (var journal = new EvolutionWorkJournal(directory.Path, "unused", 16 * 1024 * 1024))
            {
                Assert.Equal(payload, journal.Payload);
                Assert.Equal(BitConverter.ToInt64(current, 8), journal.Revision);
                Assert.Empty(journal.RecoveredRecords);
                journal.WaitDurable(journal.Append("record"));
            }
            using var reopened = new EvolutionWorkJournal(directory.Path, "unused", 16 * 1024 * 1024);
            Assert.Equal(payload, reopened.Payload);
            Assert.Equal(new[] { "record" }, reopened.RecoveredRecords);
        }
    }

    private static byte[] Read(System.IO.Compression.ZipArchive archive, string name)
    {
        using Stream stream = (archive.GetEntry(name) ?? throw new InvalidDataException("Missing " + name)).Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(System.IO.Path.Combine(directory.FullName, "benchmarks", "evidence", "external-work"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
#endif

    /// <summary>Writes the store layout the whole-state-rewrite release produced: an AEVWORK1 owner marker and one
    /// checksummed work.current, with no log.</summary>
    private static void WriteVersionOneStore(string directory, long revision, string payload)
    {
        const long magic = 0x314B524F57564541;
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(payload);
        byte[] checksum;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true)) { writer.Write(magic); writer.Write(revision); writer.Write(bytes.Length); writer.Write(bytes); }
            buffer.Position = 0;
            using var hash = System.Security.Cryptography.SHA256.Create();
            checksum = hash.ComputeHash(buffer);
        }
        using (var stream = new FileStream(System.IO.Path.Combine(directory, "work.current"), FileMode.CreateNew))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(magic); writer.Write(revision); writer.Write(bytes.Length); writer.Write(checksum); writer.Write(bytes);
        }
        using (var stream = new FileStream(System.IO.Path.Combine(directory, "work.owner"), FileMode.CreateNew))
        using (var writer = new BinaryWriter(stream)) writer.Write(magic);
    }

    // The live store holds the log open for writing, so reads must share write access; a handle-based length is
    // also exact on Windows, where a directory entry's size can lag an open file.
    private static long LogLength(WorkDirectory directory)
    {
        using var stream = new FileStream(LogPath(directory), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return stream.Length;
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task Within(Task task)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(task, finished);
        await task;
    }

    private static string LogPath(WorkDirectory directory) => System.IO.Path.Combine(directory.Path, "work.log");
    private static string OwnerPath(WorkDirectory directory) => System.IO.Path.Combine(directory.Path, "work.owner");

    private sealed class WorkDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evolution-work-journal-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
