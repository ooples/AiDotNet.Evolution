#if NETFRAMEWORK && !NET471_OR_GREATER
namespace System.Security.Cryptography;

/// <summary>
/// .NET Framework 4.6.2 lacks <c>IncrementalHash</c> (added in 4.7.1). This is the subset the engine uses --
/// <c>CreateHash</c>, <c>AppendData</c>, <c>GetHashAndReset</c> -- over the algorithm's
/// <see cref="HashAlgorithm.TransformBlock"/>, producing the same digest.
/// </summary>
internal sealed class IncrementalHash : IDisposable
{
    private readonly HashAlgorithmName _name;
    private HashAlgorithm _algorithm;

    private IncrementalHash(HashAlgorithmName name)
    {
        _name = name;
        _algorithm = Create(name);
    }

    public static IncrementalHash CreateHash(HashAlgorithmName hashAlgorithm) => new(hashAlgorithm);

    public void AppendData(byte[] data, int offset, int count)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        if (count > 0) _algorithm.TransformBlock(data, offset, count, null, 0);
    }

    public void AppendData(byte[] data)
    {
        if (data is null) throw new ArgumentNullException(nameof(data));
        AppendData(data, 0, data.Length);
    }

    public byte[] GetHashAndReset()
    {
        _algorithm.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        byte[] digest = _algorithm.Hash!;
        _algorithm.Dispose();
        _algorithm = Create(_name);
        return digest;
    }

    public void Dispose() => _algorithm.Dispose();

    private static HashAlgorithm Create(HashAlgorithmName name)
    {
        if (name == HashAlgorithmName.SHA256) return SHA256.Create();
        if (name == HashAlgorithmName.SHA384) return SHA384.Create();
        if (name == HashAlgorithmName.SHA512) return SHA512.Create();
        if (name == HashAlgorithmName.SHA1) return SHA1.Create();
        if (name == HashAlgorithmName.MD5) return MD5.Create();
        throw new CryptographicException($"Unknown hash algorithm '{name.Name}'.");
    }
}
#endif
