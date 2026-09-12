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
        var definitions = space.Parameters.Select(p => new ParameterConfig
        {
            Name = p.Name,
            Min = p.Minimum,
            Max = p.Maximum,
            Step = p.Step,
            Integral = p.Integral,
        }).ToList();
        byte[] schema = JsonSerializer.SerializeToUtf8Bytes(definitions, HostJsonContext.Default.ParameterDefinitions);
        VersionHash = "ordered-normalized-parameters-v1:" + Convert.ToHexString(SHA256.HashData(schema)).ToLowerInvariant();
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
