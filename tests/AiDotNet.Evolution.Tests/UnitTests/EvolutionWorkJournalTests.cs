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

    private sealed class WorkDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evolution-work-journal-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
