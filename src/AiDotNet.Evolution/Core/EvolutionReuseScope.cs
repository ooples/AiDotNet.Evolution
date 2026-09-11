using System.Text;

namespace AiDotNet.Evolution;

/// <summary>Explicit applicability fingerprints for portable seeds and separately controlled evaluation reuse.</summary>
/// <remarks>
/// Each value identifies semantics, not a display name. Callers must change the relevant fingerprint when
/// conditions change and explicitly use a versioned not-applicable value for irrelevant facets. These declarations
/// are not hardware attestation. A matching scope alone never proves that a stochastic sample is fresh.
/// </remarks>
public sealed class EvolutionReuseScope
{
    private readonly string[] _parts;

    /// <summary>Declares every applicability facet; none is inferred from ambient machine state.</summary>
    public EvolutionReuseScope(string taskId, string taskVersion, string evaluatorVersion,
        string codecId, string codecVersion, string constraintsVersion, string dataVersion,
        string fidelityVersion, string compilerVersion, string runtimeVersion, string hardwareVersion,
        string correctnessPolicyVersion)
        : this(new[] { taskId, taskVersion, evaluatorVersion, codecId, codecVersion, constraintsVersion,
            dataVersion, fidelityVersion, compilerVersion, runtimeVersion, hardwareVersion, correctnessPolicyVersion })
    {
    }

    internal EvolutionReuseScope(string[] parts)
    {
        if (parts.Length != 12) throw new ArgumentException("A reuse scope requires twelve applicability facets.", nameof(parts));
        _parts = (string[])parts.Clone();
        foreach (string part in _parts) EvolutionReuseEncoding.Label(part, nameof(parts));
        StableKey = EvolutionHash.Combine(new[] { "evolution-reuse-scope-v1" }.Concat(_parts));
    }

    /// <summary>Gets the task identity.</summary>
    public string TaskId => _parts[0];
    /// <summary>Gets the task semantics fingerprint.</summary>
    public string TaskVersion => _parts[1];
    /// <summary>Gets the evaluator semantics fingerprint.</summary>
    public string EvaluatorVersion => _parts[2];
    /// <summary>Gets the genome codec identity.</summary>
    public string CodecId => _parts[3];
    /// <summary>Gets the genome schema fingerprint.</summary>
    public string CodecVersion => _parts[4];
    /// <summary>Gets the hard-constraint fingerprint.</summary>
    public string ConstraintsVersion => _parts[5];
    /// <summary>Gets the data and partition fingerprint.</summary>
    public string DataVersion => _parts[6];
    /// <summary>Gets the evaluation-fidelity fingerprint.</summary>
    public string FidelityVersion => _parts[7];
    /// <summary>Gets the compiler and dependency fingerprint.</summary>
    public string CompilerVersion => _parts[8];
    /// <summary>Gets the runtime fingerprint.</summary>
    public string RuntimeVersion => _parts[9];
    /// <summary>Gets the hardware and device fingerprint.</summary>
    public string HardwareVersion => _parts[10];
    /// <summary>Gets the correctness and promotion-policy fingerprint.</summary>
    public string CorrectnessPolicyVersion => _parts[11];
    /// <summary>Gets the domain-separated hash of all applicability facets.</summary>
    public string StableKey { get; }

    internal string[] CopyParts() => (string[])_parts.Clone();

    internal void Validate<TGenome>(IEvolutionTask<TGenome> task, IEvolutionGenomeCodec<TGenome> codec)
    {
        Guard.NotNull(task); Guard.NotNull(codec);
        if (!string.Equals(TaskId, task.Id, StringComparison.Ordinal) ||
            !string.Equals(TaskVersion, task.VersionHash, StringComparison.Ordinal) ||
            !string.Equals(EvaluatorVersion, task.EvaluatorVersionHash, StringComparison.Ordinal) ||
            !string.Equals(CodecId, codec.Id, StringComparison.Ordinal) ||
            !string.Equals(CodecVersion, codec.VersionHash, StringComparison.Ordinal))
            throw new InvalidOperationException("The reuse scope does not match the current task and codec identities.");
    }
}

internal static class EvolutionReuseEncoding
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void Label(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("A nonempty printable label of at most 256 characters is required.", parameterName);
        try { _ = Utf8.GetByteCount(value); }
        catch (EncoderFallbackException error) { throw new ArgumentException("Labels must be valid Unicode.", parameterName, error); }
    }

    internal static void Digest(string value, string parameterName)
    {
        if (value is null || value.Length != 64 || value.Any(c => !((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))))
            throw new ArgumentException("A lowercase SHA256 digest is required.", parameterName);
    }
}
