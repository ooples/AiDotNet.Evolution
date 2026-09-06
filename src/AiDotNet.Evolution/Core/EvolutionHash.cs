using System.Security.Cryptography;
using System.Text;

namespace AiDotNet.Evolution;

/// <summary>Provides stable SHA-256 hashes for evolution identities and ordered configuration components.</summary>
/// <remarks>
/// The methods use UTF-8, lowercase hexadecimal output, invariant length prefixes, and no process-specific state,
/// so callers in adapter packages can build identities that remain compatible with the evolution engine.
/// </remarks>
public static class EvolutionHash
{
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
        using (SHA256 sha = SHA256.Create())
        {
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
            var result = new StringBuilder(hash.Length * 2);
            foreach (byte item in hash) result.Append(item.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            return result.ToString();
        }
    }

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
        var builder = new StringBuilder();
        int count = 0;
        foreach (string value in values)
        {
            if (count == EvolutionCollectionLimits.MaximumHashComponents)
                throw new ArgumentException(
                    $"At most {EvolutionCollectionLimits.MaximumHashComponents} components may be combined.",
                    nameof(values));
            if (value is null) throw new ArgumentException("Hash components cannot be null.", nameof(values));
            long required = (long)builder.Length + value.Length + 32;
            if (required > EvolutionCollectionLimits.MaximumHashCharacters)
                throw new ArgumentException(
                    $"Combined hash input may contain at most {EvolutionCollectionLimits.MaximumHashCharacters} characters.",
                    nameof(values));
            builder.Append(value.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Append(':').Append(value).Append(';');
            count++;
        }
        return Compute(builder.ToString());
    }
}
