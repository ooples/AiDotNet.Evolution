using System.Security.Cryptography;
using System.Text;

namespace AiDotNet.Evolution;

/// <summary>Single-owner, checksummed atomic state for external work; never falls back to older work state.</summary>
/// <remarks>Local filesystem/process-crash contract only. Not an authenticated store, network-filesystem lock,
/// backup rollback detector, or guarantee of directory metadata durability across power failure.</remarks>
internal sealed class EvolutionWorkJournal : IDisposable
{
    private const long Magic = 0x314B524F57564541; // AEVWORK1, little endian.
    private const int HeaderBytes = 8 + 8 + 4 + 32;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly FileStream _owner;
    private readonly string _directory;
    private readonly string _current;
    private readonly int _maximumBytes;
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
        _owner = new FileStream(Path.Combine(_directory, "work.owner"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
        try
        {
            if (_owner.Length == 0)
            {
                if (File.Exists(_current)) throw new InvalidDataException("Work state exists without its owner marker.");
                // Publish an initialization marker before any work state. A crash after this marker but
                // before initial state is deliberately unrecoverable, rather than guessed to be a fresh run.
                using var writer = new BinaryWriter(_owner, Encoding.UTF8, true);
                writer.Write(Magic); writer.Flush(); _owner.Flush(true);
                Payload = string.Empty;
                Commit(initialPayload);
            }
            else
            {
                using var reader = new BinaryReader(_owner, Encoding.UTF8, true);
                if (_owner.Length != 8 || reader.ReadInt64() != Magic)
                    throw new InvalidDataException("Invalid work owner marker.");
                ReadCurrent();
                WasRecovered = true;
            }
        }
        catch { _owner.Dispose(); throw; }
    }

    internal string Payload { get; private set; } = string.Empty;
    internal long Revision { get; private set; }
    internal bool WasRecovered { get; }
    // Fault injection at the two acknowledgement boundaries; not part of the public coordinator API.
    internal Action<bool>? Publishing { get; set; }

    internal void Commit(string payload)
    {
        EnsureUsable();
        Guard.NotNull(payload);
        if (Utf8.GetByteCount(payload) > _maximumBytes) throw new InvalidOperationException("Work state exceeds the configured byte limit.");
        byte[] bytes = Utf8.GetBytes(payload);
        long revision = checked(Revision + 1);
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
        }
        catch
        {
            // A publication might have succeeded before acknowledgement failed. The caller must reopen
            // the authoritative state before doing anything else, never continue from its old memory.
            _faulted = true;
            throw;
        }
        finally
        {
            // Only our unique temporary file; committed state and unrelated files are never removed.
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

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

    internal void EnsureUsable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EvolutionWorkJournal));
        if (_faulted) throw new InvalidOperationException("Work publication failed; dispose and reopen before continuing.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _owner.Dispose();
    }
}
