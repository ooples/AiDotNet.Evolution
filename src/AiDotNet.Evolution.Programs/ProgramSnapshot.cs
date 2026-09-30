using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution.Programs;

/// <summary>Owned, bounded multi-file source. Names are logical paths, never filesystem instructions.</summary>
public sealed class ProgramSnapshot
{
    /// <summary>The largest total number of characters across every file of one snapshot.</summary>
    public const int MaximumCharacters = 262_144;

    /// <summary>Copies up to 16 named source files into an immutable, fingerprinted snapshot.</summary>
    /// <param name="files">Logical path and text of each file. Paths are <c>/</c>-separated ASCII letters, digits,
    /// <c>.</c>, <c>_</c> and <c>-</c>, unique ignoring case, with no empty, <c>.</c> or <c>..</c> segment.</param>
    /// <exception cref="ArgumentException">A name or file is invalid, there are none or more than 16 files, the
    /// total exceeds <see cref="MaximumCharacters"/>, or a file is not valid UTF-16 text.</exception>
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

    /// <summary>Gets the files, ordered by path.</summary>
    public IReadOnlyDictionary<string, string> Files { get; }

    /// <summary>Gets the lowercase hex SHA-256 of the snapshot, which identifies it exactly.</summary>
    public string Fingerprint { get; }

    /// <summary>Hashes text as strict UTF-8.</summary>
    /// <param name="value">The text to hash.</param>
    /// <returns>The lowercase hex SHA-256 digest.</returns>
    public static string Digest(string value) => Digest(new UTF8Encoding(false, true).GetBytes(value));

    /// <summary>Hashes bytes.</summary>
    /// <param name="value">The bytes to hash.</param>
    /// <returns>The lowercase hex SHA-256 digest.</returns>
    public static string Digest(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

/// <summary>Syntax addresses always refer to the unchanged parent, including across repair attempts.</summary>
public sealed record EditTarget(string File, int Start, int Length, string Kind, string ExpectedHash);

/// <summary>Replaces one syntax target of the parent with new text.</summary>
/// <param name="Target">The parent span to replace.</param>
/// <param name="Replacement">The text that takes its place.</param>
public sealed record SourceEdit(EditTarget Target, string Replacement);

/// <summary>A model's proposed change: the parent it applies to, why, and the edits.</summary>
/// <param name="ParentFingerprint">The <see cref="ProgramSnapshot.Fingerprint"/> the edits were written against.</param>
/// <param name="Hypothesis">The testable reason the change should help.</param>
/// <param name="Edits">The edits, each addressed to the unchanged parent.</param>
public sealed record PatchPlan(string ParentFingerprint, string Hypothesis, IReadOnlyList<SourceEdit> Edits);

/// <summary>What the model is asked to improve on one attempt.</summary>
/// <param name="Parent">The program to improve.</param>
/// <param name="MeasuredBottleneck">What measurement says limits it.</param>
/// <param name="Targets">The syntax targets the model may edit.</param>
/// <param name="Feedback">Compiler or checker feedback from the previous attempt, or empty on the first.</param>
/// <param name="Attempt">The one-based attempt number.</param>
public sealed record SearchRequest(ProgramSnapshot Parent, string MeasuredBottleneck,
    IReadOnlyList<EditTarget> Targets, string Feedback, int Attempt);

/// <summary>Detached emitted image and all inputs that identify the compiled program.</summary>
public sealed class ProgramArtifact
{
    private readonly byte[] _image;

    /// <summary>Records a compiled image with the source and toolchain identities that produced it.</summary>
    /// <param name="source">The compiled source.</param>
    /// <param name="compilerFingerprint">Identifies the compiler and its options.</param>
    /// <param name="apiFingerprint">Identifies the API surface the program was compiled against.</param>
    /// <param name="image">The emitted image, 1 byte to 8 MiB; it is copied.</param>
    /// <exception cref="ArgumentException">The image is empty or too large, or an identity is blank.</exception>
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
    /// <summary>Gets the compiled source.</summary>
    public ProgramSnapshot Source { get; }

    /// <summary>Gets the compiler identity.</summary>
    public string CompilerFingerprint { get; }

    /// <summary>Gets the API-surface identity.</summary>
    public string ApiFingerprint { get; }

    /// <summary>Gets the SHA-256 of the image.</summary>
    public string ImageFingerprint { get; }

    /// <summary>Gets the combined identity of source, compiler, API surface and image.</summary>
    public string Fingerprint { get; }

    /// <summary>Returns a copy of the image, so callers cannot change the recorded one.</summary>
    /// <returns>The emitted image bytes.</returns>
    public byte[] GetImage() => (byte[])_image.Clone();
}

/// <summary>The outcome of one build.</summary>
/// <param name="Artifact">The compiled artifact, or <c>null</c> when the build failed.</param>
/// <param name="Feedback">Bounded compiler feedback for the next repair attempt.</param>
public sealed record ProgramBuild(ProgramArtifact? Artifact, string Feedback);

/// <summary>Alternative compiler implementations need not change the improvement loop.</summary>
public interface IProgramCompiler
{
    /// <summary>Lists the syntax targets a model may edit.</summary>
    /// <param name="source">The program to catalog.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The editable targets, addressed to <paramref name="source"/>.</returns>
    IReadOnlyList<EditTarget> Catalog(ProgramSnapshot source, CancellationToken cancellationToken);

    /// <summary>Applies a plan to its parent, verifying each target's expected hash.</summary>
    /// <param name="parent">The program the plan was written against.</param>
    /// <param name="plan">The edits to apply.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The edited program.</returns>
    ProgramSnapshot Apply(ProgramSnapshot parent, PatchPlan plan, CancellationToken cancellationToken);

    /// <summary>Compiles a program.</summary>
    /// <param name="source">The program to compile.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    /// <returns>The artifact, or failure feedback.</returns>
    ProgramBuild Build(ProgramSnapshot source, CancellationToken cancellationToken);
}
