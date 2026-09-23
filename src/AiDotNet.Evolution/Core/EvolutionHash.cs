using System.Security.Cryptography;
using System.Text;
#if NET8_0_OR_GREATER
using System.Buffers;
using System.Globalization;
#endif

namespace AiDotNet.Evolution;

/// <summary>Provides stable SHA-256 hashes for evolution identities and ordered configuration components.</summary>
/// <remarks>
/// The methods use UTF-8, lowercase hexadecimal output, invariant length prefixes, and no process-specific state,
/// so callers in adapter packages can build identities that remain compatible with the evolution engine.
/// </remarks>
public static class EvolutionHash
{
#if NET8_0_OR_GREATER
    // Inputs up to this many UTF-8 bytes are encoded on the stack; larger ones rent from the shared pool.
    private const int StackBytes = 512;
#endif

    /// <summary>Encodes a double by its exact IEEE 754 bit pattern for cross-framework identity.</summary>
    public static string EncodeDouble(double value) => BitConverter.DoubleToInt64Bits(value)
        .ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string EncodeNullableDouble(double? value) => value.HasValue
        ? string.Concat("value:", EncodeDouble(value.Value))
        : "null";

    internal static string EncodeNullable(string? value) => value is null
        ? "null"
        : string.Concat(
            "value:",
            value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ":",
            value);

    /// <summary>Computes the lowercase SHA-256 hash of a UTF-8 string.</summary>
    /// <param name="value">The value to hash.</param>
    /// <returns>A 64-character lowercase hexadecimal digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <c>null</c>.</exception>
    public static string Compute(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
#if NET8_0_OR_GREATER
        // Hashing is on every identity path: encode into stack or pooled bytes, so the digest string is the only allocation.
        int maximum = Encoding.UTF8.GetMaxByteCount(value.Length);
        if (maximum <= StackBytes)
        {
            Span<byte> bytes = stackalloc byte[StackBytes];
            return Digest(bytes[..Encoding.UTF8.GetBytes(value, bytes)]);
        }
        byte[] rented = ArrayPool<byte>.Shared.Rent(maximum);
        try { return Digest(rented.AsSpan(0, Encoding.UTF8.GetBytes(value, rented))); }
        finally { ArrayPool<byte>.Shared.Return(rented); }
#else
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] hash;
        using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
        return ToLowerHex(hash);
#endif
    }

#if NET8_0_OR_GREATER
    // SHA-256 of the bytes as 64 lowercase hex digits, the same digits as ToString("x2") per byte.
    private static string Digest(ReadOnlySpan<byte> data)
    {
        const string digits = "0123456789abcdef";
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        Span<char> chars = stackalloc char[64];
        for (int i = 0; i < hash.Length; i++)
        {
            chars[2 * i] = digits[hash[i] >> 4];
            chars[(2 * i) + 1] = digits[hash[i] & 0xF];
        }
        return new string(chars);
    }
#else
    // Same lowercase digits as ToString("x2") per byte, written into one buffer instead of 32 strings.
    private static string ToLowerHex(byte[] hash)
    {
        const string digits = "0123456789abcdef";
        var chars = new char[hash.Length * 2];
        for (int i = 0; i < hash.Length; i++)
        {
            chars[2 * i] = digits[hash[i] >> 4];
            chars[(2 * i) + 1] = digits[hash[i] & 0xF];
        }
        return new string(chars);
    }
#endif

    /// <summary>Computes an unambiguous hash of an ordered sequence of string components.</summary>
    /// <param name="values">The ordered components to combine.</param>
    /// <returns>A 64-character lowercase hexadecimal digest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">A component is <c>null</c>.</exception>
    /// <remarks>
    /// Each component is length-prefixed before hashing, so different boundaries such as
    /// <c>["ab", "c"]</c> and <c>["a", "bc"]</c> cannot collide merely through concatenation.
    /// </remarks>
    public static string Combine(IEnumerable<string> values)
    {
        if (values is null) throw new ArgumentNullException(nameof(values));
#if NET8_0_OR_GREATER
        // Hashes exactly the UTF-8 of "len:value;" per component, the bytes the concatenated string encodes to: every
        // component sits between ASCII separators, so encoding each one alone cannot split a surrogate pair. The
        // sequence is enumerated once, because callers pass lazy sequences that compute their components.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024);
        int written = 0, count = 0;
        long characters = 0;
        try
        {
            foreach (string value in values)
            {
                ValidateComponent(value, count, characters);
                int needed = 11 + 2 + Encoding.UTF8.GetMaxByteCount(value.Length);
                if (buffer.Length - written < needed) buffer = Grow(buffer, written, (long)written + needed);
                value.Length.TryFormat(buffer.AsSpan(written), out int digits, default, CultureInfo.InvariantCulture);
                written += digits;
                buffer[written++] = (byte)':';
                written += Encoding.UTF8.GetBytes(value, buffer.AsSpan(written));
                buffer[written++] = (byte)';';
                characters += digits + value.Length + 2;
                count++;
            }
            return Digest(buffer.AsSpan(0, written));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
#else
        var builder = new StringBuilder();
        int count = 0;
        foreach (string value in values)
        {
            ValidateComponent(value, count, builder.Length);
            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append(';');
            count++;
        }
        return Compute(builder.ToString());
#endif
    }

    /// <summary>Encodes one component exactly as <see cref="Combine"/> hashes it: the UTF-8 of <c>len:value;</c>.</summary>
    internal static byte[] EncodeComponent(string value)
    {
        if (value is null) throw new ArgumentNullException(nameof(value));
        return Encoding.UTF8.GetBytes(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + value + ";");
    }

    /// <summary>
    /// Hashes pre-encoded components (from <see cref="EncodeComponent"/>) in order. For any sequence that
    /// <see cref="Combine"/> accepts, the digest equals Combine's over the same components; unlike Combine it has no
    /// component-count cap, so callers can fingerprint collections as large as their own bounds allow.
    /// </summary>
    internal static string CombineEncoded(IEnumerable<byte[]> encodedComponents)
    {
        if (encodedComponents is null) throw new ArgumentNullException(nameof(encodedComponents));
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (byte[] component in encodedComponents)
        {
            if (component is null) throw new ArgumentException("Hash components cannot be null.", nameof(encodedComponents));
            hash.AppendData(component);
        }
        byte[] digest = hash.GetHashAndReset();
        const string digits = "0123456789abcdef";
        var chars = new char[digest.Length * 2];
        for (int i = 0; i < digest.Length; i++)
        {
            chars[2 * i] = digits[digest[i] >> 4];
            chars[(2 * i) + 1] = digits[digest[i] & 0xF];
        }
        return new string(chars);
    }

    private static void ValidateComponent(string? value, int count, long characters)
    {
        if (count == EvolutionCollectionLimits.MaximumHashComponents)
            throw new ArgumentException(
                $"At most {EvolutionCollectionLimits.MaximumHashComponents} components may be combined.",
                "values");
        if (value is null) throw new ArgumentException("Hash components cannot be null.", "values");
        long required = characters + value.Length + 32;
        if (required > EvolutionCollectionLimits.MaximumHashCharacters)
            throw new ArgumentException(
                $"Combined hash input may contain at most {EvolutionCollectionLimits.MaximumHashCharacters} characters.",
                "values");
    }

#if NET8_0_OR_GREATER
    private static byte[] Grow(byte[] buffer, int written, long needed)
    {
        byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Max(needed, 2L * buffer.Length));
        buffer.AsSpan(0, written).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(buffer);
        return larger;
    }
#endif
}
