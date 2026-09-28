using System.Text;

namespace AiDotNet.Evolution;

/// <summary>One bounded, text-only diagnostic artifact produced by an evaluator.</summary>
/// <remarks>
/// <para>
/// An artifact is the free-form counterpart to <see cref="EvolutionDiagnostic"/>: where a diagnostic carries a stable
/// code and a short message, an artifact carries a block of text such as captured standard error, a compiler log, or a
/// failing assertion trace. Artifacts ride inline on <see cref="EvolutionTaskResult"/> and
/// <see cref="EvolutionEvaluation"/>, are bounded by
/// <see cref="EvolutionArtifactOptions"/> before the engine stores them, are written into
/// checkpoints, and are folded into the run's deterministic state hash. Only text is retained inline: binary payloads have
/// no stable textual form, so <see cref="FromBytes"/> content goes to an <see cref="IEvolutionArtifactStore"/> instead.
/// </para>
/// <para>
/// <b>Artifact text is untrusted content.</b> It originates from an evaluated candidate, which in a program-synthesis
/// run is code the search itself wrote. Never execute it, never interpolate it into a command line, and treat it as
/// data when it reaches a prompt, a log, or a user interface. <see cref="IsRedacted"/> records that
/// <see cref="EvolutionArtifactSanitizer"/> removed something that looked like a credential before the text was stored;
/// <see cref="IsTruncated"/> records that the text was cut to fit its configured byte budget.
/// </para>
/// <para><b>For Beginners:</b> When a candidate is evaluated and something goes wrong, the single-line error code is
/// often not enough to fix it - you want the actual error output. An artifact is that output, attached to the
/// evaluation result. The engine keeps artifacts small on purpose (a size cap per artifact and a cap on how many are
/// kept) so a chatty evaluator cannot fill your checkpoint files, and it scrubs anything that looks like a password or
/// an API key before saving. A useful pattern is to hand the previous failure's artifact to whatever generates the
/// next candidate, so the next attempt can react to the actual error rather than guessing.</para>
/// </remarks>
public sealed class EvolutionArtifact
{
    /// <summary>The largest permitted <see cref="Key"/> length in characters.</summary>
    public const int MaximumKeyLength = 128;

    /// <summary>The largest permitted <see cref="Text"/> length in characters.</summary>
    public const int MaximumTextLength = 1_048_576;

    /// <summary>Initializes a bounded text artifact.</summary>
    /// <param name="key">A stable, non-empty name of at most <see cref="MaximumKeyLength"/> characters.</param>
    /// <param name="text">The artifact body, at most <see cref="MaximumTextLength"/> characters.</param>
    /// <param name="isTruncated">Whether the body was cut to fit a configured byte budget.</param>
    /// <param name="isRedacted">Whether credential-shaped content was removed from the body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> or <paramref name="text"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty or white space or exceeds <see cref="MaximumKeyLength"/>, or
    /// <paramref name="text"/> exceeds <see cref="MaximumTextLength"/>.
    /// </exception>
    public EvolutionArtifact(string key, string text, bool isTruncated = false, bool isRedacted = false)
    {
        Guard.NotNullOrWhiteSpace(key);
        Guard.NotNull(text);
        if (key.Length > MaximumKeyLength)
            throw new ArgumentException($"Artifact keys cannot exceed {MaximumKeyLength} characters.", nameof(key));
        if (text.Length > MaximumTextLength)
            throw new ArgumentException($"Artifact text cannot exceed {MaximumTextLength} characters.", nameof(text));
        Key = key.Trim();
        Text = text;
        SizeBytes = Encoding.UTF8.GetByteCount(text);
        IsTruncated = isTruncated;
        IsRedacted = isRedacted;
    }

    /// <summary>Gets the stable artifact name.</summary>
    public string Key { get; }

    /// <summary>Gets the untrusted artifact body.</summary>
    public string Text { get; }

    /// <summary>Gets the UTF-8 byte length of <see cref="Text"/>.</summary>
    public int SizeBytes { get; }

    /// <summary>Gets whether the body was cut to fit a configured byte budget.</summary>
    public bool IsTruncated { get; }

    /// <summary>Gets whether credential-shaped content was removed from the body.</summary>
    public bool IsRedacted { get; }

    /// <summary>The largest binary payload one artifact may carry.</summary>
    public const int MaximumContentBytes = 64 * 1024 * 1024;

    private readonly byte[]? _content;

    private EvolutionArtifact(string key, byte[] content, string mediaType) : this(key, string.Empty)
    {
        _content = content;
        MediaType = mediaType;
    }

    /// <summary>Creates a binary artifact, such as an image or a profile, from an evaluator.</summary>
    /// <remarks>
    /// Binary content never enters an evaluation, a checkpoint or the state hash. The engine writes it to
    /// <see cref="EvolutionArtifactOptions.Store"/> and keeps a text artifact naming its content address, size and
    /// media type; with no store configured it keeps only a note that the content was not retained.
    /// </remarks>
    /// <param name="key">A stable, non-empty name of at most <see cref="MaximumKeyLength"/> characters.</param>
    /// <param name="content">The bytes, at most <see cref="MaximumContentBytes"/>; copied.</param>
    /// <param name="mediaType">A media type such as <c>image/png</c>.</param>
    public static EvolutionArtifact FromBytes(string key, byte[] content, string mediaType)
    {
        Guard.NotNull(content);
        Guard.NotNullOrWhiteSpace(mediaType);
        if (content.Length > MaximumContentBytes)
            throw new ArgumentException($"Binary artifacts cannot exceed {MaximumContentBytes} bytes.", nameof(content));
        if (mediaType.Length > 128 || mediaType.Any(char.IsControl))
            throw new ArgumentException("The media type must be printable and at most 128 characters.", nameof(mediaType));
        return new EvolutionArtifact(key, (byte[])content.Clone(), mediaType.Trim());
    }

    /// <summary>Gets whether this artifact carries binary content rather than text.</summary>
    public bool IsBinary => _content is not null;

    /// <summary>Gets the media type of a binary artifact, or <c>null</c> for text.</summary>
    public string? MediaType { get; }

    /// <summary>Returns a copy of the binary content, or an empty array for a text artifact.</summary>
    public byte[] GetContent() => _content is null ? Array.Empty<byte>() : (byte[])_content.Clone();
}
