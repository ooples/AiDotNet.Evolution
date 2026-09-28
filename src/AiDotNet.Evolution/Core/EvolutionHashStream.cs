using System.Security.Cryptography;
using System.Text;

namespace AiDotNet.Evolution;

/// <summary>
/// SHA-256 of a UTF-8 text built up in a <see cref="StringBuilder"/>, fed through fixed buffers as it grows, so a
/// hash over a whole run's state never holds that state as one string or one byte array.
/// </summary>
/// <remarks>
/// The digest equals <see cref="EvolutionHash.Compute(string)"/> of everything appended, in order: the encoder carries a
/// surrogate pair split across a flush, so the bytes are exactly those of encoding the whole text at once. A whole-run
/// state hash encodes every cached result; building it as one string held about 2 KB per evaluation at the end of a
/// run, which was most of the engine's peak working set (V1-70).
/// </remarks>
internal sealed class EvolutionHashStream : IDisposable
{
    // The builder is drained once it holds this many characters, so it stays small whatever the state size.
    private const int FlushChars = 16 * 1024;
    private const string Digits = "0123456789abcdef";
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Encoder _encoder = new UTF8Encoding(false).GetEncoder();
    private readonly char[] _chars = new char[FlushChars];
    private readonly byte[] _bytes = new byte[(FlushChars + 1) * 3];

    /// <summary>Hashes and clears the builder once it has grown past the flush size; cheap to call per item.</summary>
    public void Flush(StringBuilder builder)
    {
        if (builder.Length >= FlushChars) Drain(builder);
    }

    /// <summary>Hashes whatever is left in the builder and returns the lowercase hexadecimal digest.</summary>
    public string Finish(StringBuilder builder)
    {
        Drain(builder);
        int tail = _encoder.GetBytes(_chars, 0, 0, _bytes, 0, flush: true);
        _hash.AppendData(_bytes, 0, tail);
        byte[] digest = _hash.GetHashAndReset();
        var text = new char[digest.Length * 2];
        for (int i = 0; i < digest.Length; i++)
        {
            text[2 * i] = Digits[digest[i] >> 4];
            text[(2 * i) + 1] = Digits[digest[i] & 0xF];
        }
        return new string(text);
    }

    public void Dispose() => _hash.Dispose();

    private void Drain(StringBuilder builder)
    {
        for (int start = 0; start < builder.Length; start += _chars.Length)
        {
            int count = Math.Min(_chars.Length, builder.Length - start);
            builder.CopyTo(start, _chars, 0, count);
            int written = _encoder.GetBytes(_chars, 0, count, _bytes, 0, flush: false);
            _hash.AppendData(_bytes, 0, written);
        }
        builder.Clear();
    }
}
