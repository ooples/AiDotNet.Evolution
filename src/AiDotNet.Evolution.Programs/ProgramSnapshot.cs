using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>Owned, bounded multi-file source. Names are logical paths, never filesystem instructions.</summary>
public sealed class ProgramSnapshot
{
    public const int MaximumCharacters = 262_144;
    public ProgramSnapshot(IEnumerable<KeyValuePair<string, string>> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int size = 0;
        foreach (var file in files)
        {
            if (copy.Count == 16 || string.IsNullOrEmpty(file.Key) || file.Key.Length > 128 ||
                file.Key.Split('/').Any(part => part.Length == 0 || part is "." or ".." ||
                    part.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))) ||
                !names.Add(file.Key) || string.IsNullOrEmpty(file.Value))
                throw new ArgumentException("Supply 1-16 uniquely named bounded source files.", nameof(files));
            size = checked(size + file.Value.Length);
            if (size > MaximumCharacters) throw new ArgumentException("Source exceeds the total character bound.", nameof(files));
            new UTF8Encoding(false, true).GetByteCount(file.Value);
            copy.Add(file.Key, file.Value);
        }
        if (copy.Count == 0) throw new ArgumentException("Source files are required.", nameof(files));
        Files = new ReadOnlyDictionary<string, string>(copy);
        Fingerprint = Digest(JsonSerializer.Serialize(copy));
    }

    public IReadOnlyDictionary<string, string> Files { get; }
    public string Fingerprint { get; }
    public static string Digest(string value) => Digest(new UTF8Encoding(false, true).GetBytes(value));
    public static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

/// <summary>Syntax addresses always refer to the unchanged parent, including across repair attempts.</summary>
public sealed record EditTarget(string File, int Start, int Length, string Kind, string ExpectedHash);
public sealed record SourceEdit(EditTarget Target, string Replacement);
public sealed record PatchPlan(string ParentFingerprint, string Hypothesis, IReadOnlyList<SourceEdit> Edits);
public sealed record SearchRequest(ProgramSnapshot Parent, string MeasuredBottleneck,
    IReadOnlyList<EditTarget> Targets, string Feedback, int Attempt);

/// <summary>Detached emitted image and all inputs that identify the compiled program.</summary>
public sealed class ProgramArtifact
{
    private readonly byte[] _image;
    public ProgramArtifact(ProgramSnapshot source, string compilerFingerprint, string apiFingerprint, byte[] image)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(image);
        if (image.Length is < 1 or > 8 * 1024 * 1024 ||
            string.IsNullOrWhiteSpace(compilerFingerprint) || string.IsNullOrWhiteSpace(apiFingerprint))
            throw new ArgumentException("A bounded image and compiler/API identities are required.");
        Source = source;
        CompilerFingerprint = compilerFingerprint;
        ApiFingerprint = apiFingerprint;
        _image = (byte[])image.Clone();
        ImageFingerprint = ProgramSnapshot.Digest(_image);
        Fingerprint = ProgramSnapshot.Digest(JsonSerializer.Serialize(new[]
            { source.Fingerprint, compilerFingerprint, apiFingerprint, ImageFingerprint }));
    }
    public ProgramSnapshot Source { get; }
    public string CompilerFingerprint { get; }
    public string ApiFingerprint { get; }
    public string ImageFingerprint { get; }
    public string Fingerprint { get; }
    public byte[] GetImage() => (byte[])_image.Clone();
}

public sealed record ProgramBuild(ProgramArtifact? Artifact, string Feedback);

/// <summary>Alternative compiler implementations need not change the improvement loop.</summary>
public interface IProgramCompiler
{
    IReadOnlyList<EditTarget> Catalog(ProgramSnapshot source, CancellationToken cancellationToken);
    ProgramSnapshot Apply(ProgramSnapshot parent, PatchPlan plan, CancellationToken cancellationToken);
    ProgramBuild Build(ProgramSnapshot source, CancellationToken cancellationToken);
}
