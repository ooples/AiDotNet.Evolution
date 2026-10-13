using System.Security.Cryptography;
using System.Text;

namespace AiDotNet.Evolution;

/// <summary>Single-owner, checksummed external-work store: an atomic snapshot plus an append-only, group-committed
/// record log. It never falls back to older work state.</summary>
/// <remarks>
/// <para>Local filesystem/process-crash contract only. Not an authenticated store, network-filesystem lock,
/// backup rollback detector, or guarantee of directory metadata durability across power failure.</para>
/// <para>WHY A LOG (#119). Every claim and commit used to rewrite the whole retained state twice (temporary file,
/// write-through flush, replace), so the per-lease cost grew with retained work. A mutation now appends one record
/// to <c>work.log</c>; its cost depends on the record, not on how much work is retained. Recovery replays the log
/// over the newest snapshot, and <see cref="Commit"/> folds the log into a new snapshot so replay stays bounded.</para>
/// <para>GROUP COMMIT. <see cref="Append"/> only buffers a record. <see cref="WaitDurable"/> returns once that record
/// is on disk: the first waiter writes every buffered record in one write and one flush, and waiters that arrive
/// meanwhile are covered by the next flush. Nothing is acknowledged before its own flush completes.</para>
/// <para>TORN TAILS. A process crash can interrupt the write of a batch, leaving a strict prefix of its bytes. A
/// trailing record with a valid header but missing bytes, or an incomplete header, was therefore never flushed and
/// never acknowledged; it is truncated on open. A complete record with a bad checksum, a bad header, or a gap in
/// sequence numbers is corruption and remains a hard failure.</para>
/// </remarks>
internal sealed class EvolutionWorkJournal : IDisposable
{
    private const long Magic = 0x314B524F57564541; // AEVWORK1, little endian: the snapshot format and the old owner marker.
    // AEVWORK2: the directory may hold log records. Code that only knows AEVWORK1 refuses the directory instead of
    // reading the snapshot alone and silently forgetting acknowledged records.
    private const long OwnerMagic = 0x324B524F57564541;
    private const int HeaderBytes = 8 + 8 + 4 + 32;
    private const int RecordMagic = 0x52564541; // AEVR
    // magic, sequence, length, header hash, payload hash.
    private const int RecordHeaderBytes = 4 + 8 + 4 + 8 + 32;
    // The log is folded into a snapshot once it holds this many bytes or as many bytes as the snapshot itself,
    // whichever is larger: rewriting is amortized to at most one snapshot byte per logged byte, and replay is
    // bounded by twice the state plus this floor.
    private const long MinimumCompactionBytes = 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly FileStream _owner;
    private readonly FileStream _log;
    private readonly string _directory;
    private readonly string _current;
    private readonly int _maximumBytes;
    private readonly object _buffer = new();
    private readonly object _flush = new();
    private readonly List<byte[]> _pending = new();
    private long _lastSequence;
    private long _durableSequence;
    private long _logBytes;
    private long _snapshotBytes;
    private Exception? _fault;
    private bool _faulted;
    private bool _disposed;

    internal EvolutionWorkJournal(string directory, string initialPayload, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A work directory is required.", nameof(directory));
        if (maximumBytes < 1024 || maximumBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        _maximumBytes = maximumBytes;
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _current = Path.Combine(_directory, "work.current");
        string log = Path.Combine(_directory, "work.log");
        _owner = new FileStream(Path.Combine(_directory, "work.owner"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        FileStream? opened = null;
        try
        {
            if (_owner.Length == 0)
            {
                if (File.Exists(_current) || File.Exists(log)) throw new InvalidDataException("Work state exists without its owner marker.");
                // Publish an initialization marker before any work state. A crash after this marker but
                // before initial state is deliberately unrecoverable, rather than guessed to be a fresh run.
                WriteOwner(OwnerMagic);
                opened = OpenLog(log);
                _log = opened;
                Payload = string.Empty;
                Commit(initialPayload);
            }
            else
            {
                using (var reader = new BinaryReader(_owner, Encoding.UTF8, true))
                {
                    long marker = _owner.Length == 8 ? reader.ReadInt64() : 0;
                    if (marker != Magic && marker != OwnerMagic) throw new InvalidDataException("Invalid work owner marker.");
                    // An AEVWORK1 directory predates the log, so it cannot have one; a log next to it is foreign data.
                    if (marker == Magic && File.Exists(log)) throw new InvalidDataException("A version-1 work store cannot contain a record log.");
                }
                ReadCurrent();
                opened = OpenLog(log);
                _log = opened;
                RecoveredRecords = ReadLog();
                // Upgrade the marker only after the old store has been read successfully, and before any record can
                // be appended, so older code refuses the directory from the first moment it could miss a record.
                WriteOwner(OwnerMagic);
                WasRecovered = true;
            }
        }
        catch { opened?.Dispose(); _owner.Dispose(); throw; }
    }

    /// <summary>The newest snapshot payload. Records in <see cref="RecoveredRecords"/> apply on top of it.</summary>
    internal string Payload { get; private set; } = string.Empty;
    /// <summary>The sequence number the newest snapshot covers.</summary>
    internal long Revision { get; private set; }
    /// <summary>Log records newer than the snapshot, in order, read when an existing store was opened.</summary>
    internal IReadOnlyList<string> RecoveredRecords { get; private set; } = Array.Empty<string>();
    internal bool WasRecovered { get; }
    internal bool HasTemporaryCleanupFailure { get; private set; }
    /// <summary>The sequence number of the newest appended record, durable or not.</summary>
    internal long LastSequence { get { lock (_buffer) return _lastSequence; } }
    /// <summary>The sequence number up to which every record is on disk.</summary>
    internal long DurableSequence { get { lock (_buffer) return _durableSequence; } }
    /// <summary>Physical log flushes performed by this instance; a diagnostic that makes group commit observable.</summary>
    internal long LogFlushes { get; private set; }
    /// <summary>Whether the log has grown enough that folding it into a new snapshot is due.</summary>
    internal bool CompactionDue { get { lock (_buffer) return _logBytes >= Math.Max(MinimumCompactionBytes, _snapshotBytes); } }
    // Fault injection at the two acknowledgement boundaries of every physical write (snapshot or log batch):
    // false before the bytes are written, true after they are durable. Not part of the public coordinator API.
    internal Action<bool>? Publishing { get; set; }

    /// <summary>Buffers one record and returns its sequence number. It is not durable until <see cref="WaitDurable"/>.</summary>
    /// <remarks>Callers serialize appends themselves (the coordinator holds its lock), so sequence order is
    /// mutation order.</remarks>
    internal long Append(string record)
    {
        Guard.NotNull(record);
        byte[] payload = Utf8.GetBytes(record);
        if (payload.Length > MaximumRecordBytes) throw new InvalidOperationException("A work record exceeds the configured byte limit.");
        lock (_buffer)
        {
            ThrowIfUnusable();
            long sequence = checked(_lastSequence + 1);
            _pending.Add(Frame(sequence, payload));
            _lastSequence = sequence;
            return sequence;
        }
    }

    /// <summary>Returns once every record up to <paramref name="sequence"/> is on disk, writing buffered records
    /// itself when no other caller is already doing so.</summary>
    internal void WaitDurable(long sequence)
    {
        lock (_buffer)
        {
            if (sequence <= _durableSequence) return;
            if (sequence > _lastSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
            ThrowIfUnusable();
        }
        lock (_flush)
        {
            lock (_buffer)
            {
                // A previous leader may have covered this sequence while this caller waited for the flush lock.
                if (sequence <= _durableSequence) return;
                ThrowIfUnusable();
            }
            FlushPending();
        }
    }

    /// <summary>Publishes a full snapshot atomically and folds the log into it.</summary>
    /// <remarks>Every buffered record is made durable first, so the snapshot never precedes a record it supersedes.
    /// A crash after the snapshot is replaced but before the log is truncated leaves only records the snapshot
    /// already covers; recovery skips them.</remarks>
    internal void Commit(string payload)
    {
        EnsureUsable();
        Guard.NotNull(payload);
        if (Utf8.GetByteCount(payload) > _maximumBytes) throw new InvalidOperationException("Work state exceeds the configured byte limit.");
        byte[] bytes = Utf8.GetBytes(payload);
        lock (_flush)
        {
            FlushPending();
            long revision;
            lock (_buffer) { ThrowIfUnusable(); revision = checked(_lastSequence + 1); }
            string temporary = Path.Combine(_directory, "work-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
                    writer.Write(Magic); writer.Write(revision); writer.Write(bytes.Length);
                    writer.Write(Checksum(revision, bytes)); writer.Write(bytes);
                    writer.Flush(); stream.Flush(true);
                }
                Publishing?.Invoke(false);
                if (Revision == 0) File.Move(temporary, _current);
                else File.Replace(temporary, _current, null);
                Publishing?.Invoke(true);
                Revision = revision;
                Payload = payload;
                lock (_buffer)
                {
                    _lastSequence = revision; _durableSequence = revision;
                    _snapshotBytes = HeaderBytes + (long)bytes.Length;
                }
                // Records the snapshot covers are no longer needed. Failing here leaves them for recovery to skip,
                // but this instance can no longer append after them safely, so the failure faults it below.
                _log.SetLength(0); _log.Flush(true);
                lock (_buffer) _logBytes = 0;
            }
            catch (Exception ex)
            {
                // A publication might have succeeded before acknowledgement failed. The caller must reopen
                // the authoritative state before doing anything else, never continue from its old memory.
                Fault(ex);
                throw;
            }
            finally
            {
                // Only our unique temporary file; committed state and unrelated files are never removed.
                // File.Delete already tolerates a missing file. Avoid a separate existence check
                // that can hide access failures; preserve the publication exception if cleanup fails.
                try { File.Delete(temporary); }
                catch (IOException) { HasTemporaryCleanupFailure = true; }
                catch (UnauthorizedAccessException) { HasTemporaryCleanupFailure = true; }
            }
        }
    }

    internal void EnsureUsable()
    {
        lock (_buffer) ThrowIfUnusable();
    }

    public void Dispose()
    {
        lock (_flush)
        {
            lock (_buffer)
            {
                if (_disposed) return;
            }
            // Records already handed out for acknowledgement are written before the store closes, so a waiter that
            // reaches the flush lock after disposal finds them durable instead of lost.
            try
            {
                bool flush;
                lock (_buffer) flush = !_faulted && _pending.Count != 0;
                if (flush) FlushPending();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // FlushPending already faulted the store; its waiters observe that failure. Disposal must still release the lock.
            }
            finally
            {
                lock (_buffer) { _disposed = true; _pending.Clear(); }
                _log.Dispose(); _owner.Dispose();
            }
        }
    }

    // Records are bounded by the state they describe, plus field names; the extra room keeps a record of a state
    // near its own limit from being refused before the state bound is checked.
    private int MaximumRecordBytes => _maximumBytes + 64 * 1024;

    // Must be called with _flush held.
    private void FlushPending()
    {
        byte[][] batch;
        long last;
        lock (_buffer)
        {
            ThrowIfUnusable();
            if (_pending.Count == 0) return;
            batch = _pending.ToArray(); _pending.Clear(); last = _lastSequence;
        }
        long bytes = 0;
        try
        {
            Publishing?.Invoke(false);
            foreach (byte[] record in batch) { _log.Write(record, 0, record.Length); bytes += record.Length; }
            _log.Flush(true);
            LogFlushes++;
            Publishing?.Invoke(true);
        }
        catch (Exception ex)
        {
            // Some of the batch may be on disk. Recovery truncates a torn tail and replays complete records, so the
            // outcome of every record in this batch is unknown until the store is reopened.
            Fault(ex);
            throw;
        }
        lock (_buffer) { _durableSequence = last; _logBytes += bytes; }
    }

    private void Fault(Exception ex)
    {
        lock (_buffer) { _faulted = true; _fault ??= ex; _pending.Clear(); }
    }

    private void ThrowIfUnusable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EvolutionWorkJournal));
        if (_faulted) throw new InvalidOperationException("Work publication failed; dispose and reopen before continuing.", _fault);
    }

    private void WriteOwner(long marker)
    {
        _owner.Position = 0;
        using (var writer = new BinaryWriter(_owner, Encoding.UTF8, true)) writer.Write(marker);
        _owner.SetLength(8);
        _owner.Flush(true);
    }

    private static FileStream OpenLog(string path) =>
        new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.None);

    private void ReadCurrent()
    {
        // Missing/corrupt latest state is a hard failure, unlike recoverable algorithm checkpoints.
        using var stream = new FileStream(_current, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < HeaderBytes || stream.Length > HeaderBytes + (long)_maximumBytes)
            throw new InvalidDataException("Work state has an invalid bounded length.");
        using var reader = new BinaryReader(stream, Encoding.UTF8, true);
        long magic = reader.ReadInt64(); long revision = reader.ReadInt64(); int count = reader.ReadInt32();
        if (magic != Magic || revision < 1 || count < 0 || count > _maximumBytes || stream.Length != HeaderBytes + (long)count)
            throw new InvalidDataException("Work state header is invalid.");
        byte[] checksum = reader.ReadBytes(32); byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count || !checksum.SequenceEqual(Checksum(revision, bytes)))
            throw new InvalidDataException("Work state checksum is invalid; fallback is forbidden.");
        Payload = Utf8.GetString(bytes);
        Revision = revision;
        _snapshotBytes = stream.Length;
    }

    private IReadOnlyList<string> ReadLog()
    {
        var records = new List<string>();
        long length = _log.Length;
        long offset = 0;
        long previous = 0;
        bool stale = false;
        var header = new byte[RecordHeaderBytes];
        _log.Position = 0;
        while (offset < length)
        {
            if (length - offset < RecordHeaderBytes) break; // An incomplete header is a torn, unacknowledged write.
            ReadExactly(header, RecordHeaderBytes);
            int magic = ReadInt32(header, 0);
            long sequence = ReadInt64(header, 4);
            int count = ReadInt32(header, 12);
            if (magic != RecordMagic || sequence < 1 || count < 0 || count > MaximumRecordBytes
                || !Equal(header, 16, HeaderHash(sequence, count), 0, 8))
                throw new InvalidDataException("Work log record header is invalid; fallback is forbidden.");
            if (length - offset - RecordHeaderBytes < count) break; // A complete header with missing payload bytes is torn.
            var payload = new byte[count];
            ReadExactly(payload, count);
            if (!Equal(header, 24, PayloadHash(sequence, payload), 0, 32))
                throw new InvalidDataException("Work log record checksum is invalid; fallback is forbidden.");
            if (previous != 0 && sequence != checked(previous + 1))
                throw new InvalidDataException("Work log sequence has a gap; fallback is forbidden.");
            if (previous == 0 && sequence > checked(Revision + 1))
                throw new InvalidDataException("Work log starts after its snapshot; records are missing.");
            if (sequence <= Revision) stale = true;
            else
            {
                // A snapshot replace followed by a crash before truncation leaves only covered records. New records
                // are appended only after that truncation, so covered and newer records never share a log.
                if (stale) throw new InvalidDataException("Work log mixes records the snapshot covers with newer records.");
                records.Add(Utf8.GetString(payload));
            }
            previous = sequence;
            offset += RecordHeaderBytes + (long)count;
        }
        if (stale) offset = 0; // Everything here is already in the snapshot.
        if (offset != length) _log.SetLength(offset);
        // Replayed records become visible state, so they are made durable before anything can observe them.
        _log.Flush(true);
        _log.Position = offset;
        _lastSequence = Revision + records.Count;
        _durableSequence = _lastSequence;
        _logBytes = offset;
        return records.AsReadOnly();
    }

    private void ReadExactly(byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = _log.Read(buffer, read, count - read);
            if (n == 0) throw new EndOfStreamException("Work log ended inside a record it reported as complete.");
            read += n;
        }
    }

    private static byte[] Frame(long sequence, byte[] payload)
    {
        var record = new byte[RecordHeaderBytes + payload.Length];
        WriteInt32(record, 0, RecordMagic);
        WriteInt64(record, 4, sequence);
        WriteInt32(record, 12, payload.Length);
        Buffer.BlockCopy(HeaderHash(sequence, payload.Length), 0, record, 16, 8);
        Buffer.BlockCopy(PayloadHash(sequence, payload), 0, record, 24, 32);
        Buffer.BlockCopy(payload, 0, record, RecordHeaderBytes, payload.Length);
        return record;
    }

    // The header hash lets a torn tail be told apart from a damaged length: only a header that hashes correctly
    // is trusted to say how many bytes its record should have.
    private static byte[] HeaderHash(long sequence, int count)
    {
        var bytes = new byte[16];
        WriteInt32(bytes, 0, RecordMagic); WriteInt64(bytes, 4, sequence); WriteInt32(bytes, 12, count);
        using SHA256 hash = SHA256.Create();
        return hash.ComputeHash(bytes);
    }

    private static byte[] PayloadHash(long sequence, byte[] payload)
    {
        var bytes = new byte[16 + payload.Length];
        WriteInt32(bytes, 0, RecordMagic); WriteInt64(bytes, 4, sequence); WriteInt32(bytes, 12, payload.Length);
        Buffer.BlockCopy(payload, 0, bytes, 16, payload.Length);
        using SHA256 hash = SHA256.Create();
        return hash.ComputeHash(bytes);
    }

    private static byte[] Checksum(long revision, byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true))
        {
            writer.Write(Magic); writer.Write(revision); writer.Write(payload.Length); writer.Write(payload);
        }
        buffer.Position = 0;
        using SHA256 hash = SHA256.Create();
        return hash.ComputeHash(buffer);
    }

    private static bool Equal(byte[] a, int aOffset, byte[] b, int bOffset, int count)
    {
        for (int i = 0; i < count; i++) if (a[aOffset + i] != b[bOffset + i]) return false;
        return true;
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        for (int i = 0; i < 4; i++) buffer[offset + i] = (byte)(value >> (8 * i));
    }

    private static void WriteInt64(byte[] buffer, int offset, long value)
    {
        for (int i = 0; i < 8; i++) buffer[offset + i] = (byte)(value >> (8 * i));
    }

    private static int ReadInt32(byte[] buffer, int offset)
    {
        int value = 0;
        for (int i = 0; i < 4; i++) value |= buffer[offset + i] << (8 * i);
        return value;
    }

    private static long ReadInt64(byte[] buffer, int offset)
    {
        long value = 0;
        for (int i = 0; i < 8; i++) value |= (long)buffer[offset + i] << (8 * i);
        return value;
    }
}
