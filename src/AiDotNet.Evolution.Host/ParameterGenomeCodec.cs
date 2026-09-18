using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiDotNet.Evolution.Host;

/// <summary>Reflection-free, exact value serialization with a versioned, ordered parameter schema.</summary>
internal sealed class ParameterGenomeCodec : IEvolutionGenomeCodec<ParameterGenome>
{
    private readonly ParameterSpace _space;

    internal ParameterGenomeCodec(ParameterSpace space)
    {
        _space = space;
        // CANONICAL ENCODING, NOT SERIALIZER OUTPUT. This hash decides whether a persisted genome is
        // compatible with a space, so it must depend only on the declared parameters. Hashing
        // System.Text.Json's bytes made it depend on that serializer's incidental choices as well --
        // number formatting and property order are implementation details, not part of the contract --
        // so the same logical space could hash differently across runtimes and reject its own genomes.
        // Fixed field order, explicit separators that cannot occur in a parameter name, and round-trip
        // invariant number formatting remove all three degrees of freedom.
        // STREAMED INTO THE HASH, NOT MATERIALIZED. Parameter count is bounded (Protocol.MaxDimensions)
        // and the frame is bounded, but the names are only bounded in aggregate by the frame, so a
        // near-limit space built the whole canonical text three times over -- a StringBuilder in UTF-16,
        // its ToString, and the UTF-8 byte array -- before a single byte was hashed. Feeding the same
        // bytes to an incremental hash keeps the encoding identical and the working set constant.
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (ParameterDefinition definition in space.Parameters)
        {
            Field(hash, definition.Name);
            Field(hash, definition.Minimum.ToString("R", CultureInfo.InvariantCulture));
            Field(hash, definition.Maximum.ToString("R", CultureInfo.InvariantCulture));
            Field(hash, definition.Step.ToString("R", CultureInfo.InvariantCulture));
            // The integral flag takes the record separator directly, not Field: the previous encoding
            // put no unit separator between it and the end of the record, and adding one here would
            // have changed every hash while every relational test still passed.
            hash.AppendData(definition.Integral ? IntegralTrue : IntegralFalse);
            hash.AppendData(RecordSeparator);
        }
        VersionHash = "ordered-normalized-parameters-v2-canonical:" + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    // 0x1f between fields and 0x1e between parameters, the same unit and record separators the
    // previous StringBuilder appended. Both are single UTF-8 bytes and neither can appear in a
    // parameter name, so the encoding stays unambiguous without any escaping.
    private static readonly byte[] UnitSeparator = { 0x1f };
    private static readonly byte[] RecordSeparator = { 0x1e };
    private static readonly byte[] IntegralTrue = { (byte)'1' };
    private static readonly byte[] IntegralFalse = { (byte)'0' };

    private static void Field(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(UnitSeparator);
    }

    public string Id => "host-parameter-vector";
    public string VersionHash { get; }

    public string Serialize(ParameterGenome genome)
    {
        if (!ReferenceEquals(genome.Space, _space)) throw new ArgumentException("Genome belongs to a different parameter space.", nameof(genome));
        return JsonSerializer.Serialize(genome.Values.ToArray(), HostJsonContext.Default.NumericVector);
    }

    public ParameterGenome Deserialize(string serializedGenome)
    {
        if (serializedGenome is null || serializedGenome.Length > _space.Parameters.Count * 32 + 2)
            throw new ArgumentException("Genome vector exceeds its bounded representation.", nameof(serializedGenome));
        double[] values = JsonSerializer.Deserialize(serializedGenome, HostJsonContext.Default.NumericVector)
            ?? throw new ArgumentException("A genome must be a numeric vector.", nameof(serializedGenome));
        if (values.Length != _space.Parameters.Count) throw new ArgumentException("Genome dimension mismatch.", nameof(serializedGenome));
        for (int i = 0; i < values.Length; i++)
        {
            ParameterDefinition p = _space.Parameters[i];
            if (!double.IsFinite(values[i]) || values[i] < p.Minimum || values[i] > p.Maximum
                || (p.Integral && values[i] != Math.Truncate(values[i])))
                throw new ArgumentException("Genome contains an invalid parameter value.", nameof(serializedGenome));
        }
        // Values already came from the quantizer. Re-normalizing floating point values here can
        // move them across a rounding boundary and break exact checkpoint/replay identity.
        return new ParameterGenome(_space, values);
    }
}
