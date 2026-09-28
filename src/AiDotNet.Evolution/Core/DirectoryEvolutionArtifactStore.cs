using System.Globalization;
using System.Security.Cryptography;

namespace AiDotNet.Evolution;

/// <summary>A content-addressed <see cref="IEvolutionArtifactStore"/> in one directory, with optional retention.</summary>
/// <remarks>
/// Each blob is written once, atomically (temporary file then move), under its SHA-256 name, so concurrent writers of the
/// same content cannot corrupt it and a reader always verifies what it gets. With a retention period, blobs not written
/// or re-stored within it are removed when the store is opened and by <see cref="Prune"/>, which is the counterpart of
/// OpenEvolve's <c>cleanup_old_artifacts</c> and <c>artifact_retention_days</c>.
/// </remarks>
public sealed class DirectoryEvolutionArtifactStore : IEvolutionArtifactStore
{
    private const string Prefix = "sha256:";
    private const string Extension = ".bin";
    private readonly string _directory;
    private readonly TimeSpan? _retention;

    /// <summary>Opens (creating if needed) a store in <paramref name="directory"/>.</summary>
    /// <param name="directory">The directory that holds the blobs.</param>
    /// <param name="retention">How long an unused blob is kept; <c>null</c> keeps everything.</param>
    public DirectoryEvolutionArtifactStore(string directory, TimeSpan? retention = null)
    {
        Guard.NotNullOrWhiteSpace(directory);
        if (retention is { } period && period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        _directory = Path.GetFullPath(directory);
        _retention = retention;
        Directory.CreateDirectory(_directory);
        if (_retention is not null) Prune(DateTimeOffset.UtcNow);
    }

    /// <summary>Gets the directory that holds the blobs.</summary>
    public string DirectoryPath => _directory;

    /// <inheritdoc/>
    public string Put(byte[] content)
    {
        Guard.NotNull(content);
        string hex = Hash(content);
        string path = PathFor(hex);
        // Re-storing refreshes the blob, so retention counts from its latest use.
        if (File.Exists(path) && TryRefresh(path)) return Prefix + hex;
        string temporary = Child("." + hex + "." + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".tmp");
        File.WriteAllBytes(temporary, content);
        try
        {
            File.Move(temporary, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another writer stored the same content first; both copies are identical.
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        return Prefix + hex;
    }

    /// <inheritdoc/>
    public bool TryRead(string reference, out byte[] content)
    {
        content = Array.Empty<byte>();
        if (reference is null || !reference.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string hex = reference.Substring(Prefix.Length);
        if (hex.Length != 64 || hex.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) return false;
        string path = PathFor(hex);
        if (!File.Exists(path)) return false;
        byte[] bytes = File.ReadAllBytes(path);
        if (!string.Equals(Hash(bytes), hex, StringComparison.Ordinal)) return false;
        content = bytes;
        return true;
    }

    /// <summary>Removes blobs not written or re-stored within the retention period; returns how many were removed.</summary>
    public int Prune(DateTimeOffset now)
    {
        if (_retention is not { } retention) return 0;
        DateTime cutoff = (now - retention).UtcDateTime;
        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(_directory, "*" + Extension))
        {
            if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
            try { File.Delete(path); removed++; }
            catch (IOException) { /* in use by a concurrent reader; the next prune retries */ }
        }
        return removed;
    }

    // Another store's prune may delete the blob between the existence check and the refresh. The content is still in
    // hand, so the caller then writes it again rather than failing the evaluation.
    private static bool TryRefresh(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private string PathFor(string hex) => Child(hex + Extension);

    // Names here are generated (validated hex plus a fixed suffix), never rooted; joining explicitly keeps the directory.
    private string Child(string name) => _directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar + name;

    private static string Hash(byte[] content)
    {
        byte[] digest;
        using (SHA256 sha = SHA256.Create()) digest = sha.ComputeHash(content);
        var text = new System.Text.StringBuilder(64);
        foreach (byte item in digest) text.Append(item.ToString("x2", CultureInfo.InvariantCulture));
        return text.ToString();
    }
}
