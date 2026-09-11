using System.Collections.Concurrent;

namespace AiDotNet.Evolution;

/// <summary>Pluggable persistent successful-measurement storage; callers own admission, freshness and operation accounting.</summary>
public interface IEvolutionEvaluationStore
{
    /// <summary>Reads one exact key, returning null only when no record exists; malformed/unavailable storage throws.</summary>
    ValueTask<EvolutionEvaluationCacheRecord?> ReadAsync(EvolutionEvaluationCacheKey key, CancellationToken cancellationToken = default);
    /// <summary>Attempts to retain fresh evidence; false means capacity, an identical record or a newer record prevented publication.</summary>
    ValueTask<bool> TryWriteAsync(EvolutionEvaluationCacheRecord record, CancellationToken cancellationToken = default);
}

/// <summary>Bounded atomic-file storage in an explicitly supplied private absolute directory.</summary>
/// <remarks>
/// No clock, evaluator, genome decoder or model executes here. Callers must protect this directory from hostile
/// modification; checksums are not signatures. Process-local gates serialize writes through equivalent lexical
/// roots; filesystem aliases and concurrent external writers need external coordination. Atomic replacement keeps
/// readers on a complete old or new record, but does not establish cross-process newest-writer ordering/capacity.
/// Expiration does not delete evidence. Capacity refusal and stale writes do not evict records automatically.
/// </remarks>
public sealed class DirectoryEvolutionEvaluationStore : IEvolutionEvaluationStore
{
    private static readonly ConcurrentDictionary<string, object> Gates = new(
        Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _gate;

    /// <summary>Creates an explicitly owned store directory; roots and relative/drive-relative paths are rejected.</summary>
    public DirectoryEvolutionEvaluationStore(string directory, int maximumEntries = 4096)
    {
        Guard.NotNullOrWhiteSpace(directory);
        if (!Path.IsPathRooted(directory) || (Path.DirectorySeparatorChar == '\\' &&
            !directory.StartsWith("\\\\", StringComparison.Ordinal) &&
            !(directory.Length >= 3 && char.IsLetter(directory[0]) && directory[1] == ':' &&
              (directory[2] == '\\' || directory[2] == '/'))))
            throw new ArgumentException("Use a fully qualified private store directory.", nameof(directory));
        if (maximumEntries < 1 || maximumEntries > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        string fullPath = Path.GetFullPath(directory);
        string root = (Path.GetPathRoot(fullPath) ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        fullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A filesystem root cannot be an evaluation store.", nameof(directory));
        DirectoryPath = fullPath; MaximumEntries = maximumEntries; _gate = Gates.GetOrAdd(fullPath, _ => new object());
        Directory.CreateDirectory(fullPath);
    }
    /// <summary>Gets the canonical lexical directory, not an OS isolation/alias attestation.</summary>
    public string DirectoryPath { get; }
    /// <summary>Gets the process-coordinated file-count limit; an existing key can be refreshed at capacity.</summary>
    public int MaximumEntries { get; }

    /// <inheritdoc/>
    public ValueTask<EvolutionEvaluationCacheRecord?> ReadAsync(EvolutionEvaluationCacheKey key, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(key); cancellationToken.ThrowIfCancellationRequested();
        return new(Read(key, cancellationToken));
    }

    /// <inheritdoc/>
    public ValueTask<bool> TryWriteAsync(EvolutionEvaluationCacheRecord record, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(record); cancellationToken.ThrowIfCancellationRequested();
        string json = record.ToJson();
        byte[] bytes = EvolutionReuseEncoding.Utf8.GetBytes(json);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = Read(record.Key, cancellationToken);
            if (existing is not null)
            {
                if (existing.Origin.SampleSetHash == record.Origin.SampleSetHash)
                {
                    if (existing.ToJson() != json) throw new InvalidDataException("Conflicting evidence for the same original sample set.");
                    return new(false);
                }
                if (existing.Origin.ObservedAt >= record.Origin.ObservedAt) return new(false);
            }
            else if (Directory.EnumerateFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly).Take(MaximumEntries).Count() >= MaximumEntries)
                return new(false);

            string destination = RecordPath(record.Key);
            // Short sibling names avoid the net471 temporary-path overflow of finalName + Guid suffixes.
            string temporary = Path.Combine(DirectoryPath, ".eval-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (existing is null) File.Move(temporary, destination);
                else File.Replace(temporary, destination, null);
                return new(true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private string RecordPath(EvolutionEvaluationCacheKey key) => Path.Combine(DirectoryPath, key.StableKey + ".json");

    private EvolutionEvaluationCacheRecord? Read(EvolutionEvaluationCacheKey key, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            using var stream = new FileStream(RecordPath(key), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > EvolutionEvaluationCacheRecord.MaximumJsonBytes) throw new InvalidDataException("Persistent evaluation file exceeds its byte limit.");
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[81920];
            int count;
            while ((count = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length + count > EvolutionEvaluationCacheRecord.MaximumJsonBytes) throw new InvalidDataException("Persistent evaluation file exceeds its byte limit.");
                buffer.Write(chunk, 0, count);
            }
            bytes = buffer.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        cancellationToken.ThrowIfCancellationRequested();
        EvolutionEvaluationCacheRecord record = EvolutionEvaluationCacheRecord.FromJson(EvolutionReuseEncoding.Utf8.GetString(bytes));
        if (record.Key.StableKey != key.StableKey) throw new InvalidDataException("Persistent evaluation file does not match its requested key.");
        return record;
    }
}
