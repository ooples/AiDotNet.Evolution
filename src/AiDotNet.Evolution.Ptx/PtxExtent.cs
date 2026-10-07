using System.Globalization;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Ptx;

/// <summary>An integer computed from shape symbols: <c>ceil(product(factors) * multiplier / (divisor * product(divisorFactors)))</c>.</summary>
/// <remarks>
/// Every size in a kernel contract - buffer lengths, grid dimensions, scalar arguments and dynamic shared memory - is an
/// extent, so one contract describes a kernel for every shape it is checked or timed on. The reserved symbols
/// <c>blockX</c>, <c>blockY</c> and <c>blockZ</c> name the launch's block dimensions, so a grid written as
/// <c>ceil(N / blockX)</c> stays correct when a candidate changes its block size.
/// </remarks>
public sealed class PtxExtent : IEquatable<PtxExtent>
{
    /// <summary>The reserved symbol for the block's x dimension.</summary>
    public const string BlockX = "blockX";
    /// <summary>The reserved symbol for the block's y dimension.</summary>
    public const string BlockY = "blockY";
    /// <summary>The reserved symbol for the block's z dimension.</summary>
    public const string BlockZ = "blockZ";

    /// <summary>Creates an extent.</summary>
    /// <param name="factors">Symbols multiplied together; empty for a constant.</param>
    /// <param name="multiplier">A positive constant factor.</param>
    /// <param name="divisorFactors">Symbols the product is divided by, rounding up.</param>
    /// <param name="divisor">A positive constant divisor, rounding up.</param>
    /// <exception cref="ArgumentException">A symbol is not an identifier, or a constant is not positive.</exception>
    [JsonConstructor]
    public PtxExtent(IReadOnlyList<string>? factors, long multiplier, IReadOnlyList<string>? divisorFactors, long divisor)
    {
        Factors = Array.AsReadOnly((factors ?? Array.Empty<string>()).ToArray());
        DivisorFactors = Array.AsReadOnly((divisorFactors ?? Array.Empty<string>()).ToArray());
        if (Factors.Concat(DivisorFactors).Any(symbol => !PtxNames.IsIdentifier(symbol)) || Factors.Count + DivisorFactors.Count > 16)
            throw new ArgumentException("Extent symbols must be at most 16 identifiers.");
        if (multiplier < 0 || divisor < 1) throw new ArgumentException("An extent needs a nonnegative multiplier and a positive divisor.");
        Multiplier = multiplier;
        Divisor = divisor;
    }

    /// <summary>Gets the symbols multiplied together.</summary>
    public IReadOnlyList<string> Factors { get; }
    /// <summary>Gets the constant factor.</summary>
    public long Multiplier { get; }
    /// <summary>Gets the symbols the product is divided by.</summary>
    public IReadOnlyList<string> DivisorFactors { get; }
    /// <summary>Gets the constant divisor.</summary>
    public long Divisor { get; }

    /// <summary>Creates a constant extent.</summary>
    /// <param name="value">The value; not negative.</param>
    /// <returns>The extent.</returns>
    public static PtxExtent Constant(long value) => new(null, value, null, 1);

    /// <summary>Creates the product of the given symbols.</summary>
    /// <param name="symbols">The symbols to multiply.</param>
    /// <returns>The extent.</returns>
    public static PtxExtent Of(params string[] symbols) => new(symbols, 1, null, 1);

    /// <summary>Multiplies by a constant.</summary>
    /// <param name="value">The positive factor.</param>
    /// <returns>A new extent.</returns>
    public PtxExtent Times(long value) => new(Factors, checked(Multiplier * value), DivisorFactors, Divisor);

    /// <summary>Divides by a constant, rounding up.</summary>
    /// <param name="value">The positive divisor.</param>
    /// <returns>A new extent.</returns>
    public PtxExtent CeilDiv(long value) => new(Factors, Multiplier, DivisorFactors, checked(Divisor * value));

    /// <summary>Divides by a symbol, rounding up; use <see cref="BlockX"/> for a grid that covers a dimension.</summary>
    /// <param name="symbol">The divisor symbol.</param>
    /// <returns>A new extent.</returns>
    public PtxExtent CeilDiv(string symbol) => new(Factors, Multiplier, DivisorFactors.Append(symbol).ToArray(), Divisor);

    /// <summary>Evaluates the extent.</summary>
    /// <param name="symbols">A value for every symbol the extent names.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">A symbol is missing or not positive.</exception>
    /// <exception cref="OverflowException">The value does not fit in 64 bits.</exception>
    public long Evaluate(IReadOnlyDictionary<string, long> symbols)
    {
        if (symbols is null) throw new ArgumentNullException(nameof(symbols));
        long numerator = Multiplier, denominator = Divisor;
        foreach (string symbol in Factors) numerator = checked(numerator * Lookup(symbols, symbol));
        foreach (string symbol in DivisorFactors) denominator = checked(denominator * Lookup(symbols, symbol));
        return numerator / denominator + (numerator % denominator == 0 ? 0 : 1);
    }

    internal IEnumerable<string> Symbols => Factors.Concat(DivisorFactors);

    private static long Lookup(IReadOnlyDictionary<string, long> symbols, string symbol) =>
        symbols.TryGetValue(symbol, out long value) && value > 0
            ? value
            : throw new ArgumentException("Extent symbol '" + symbol + "' has no positive value.", nameof(symbols));

    /// <inheritdoc/>
    public bool Equals(PtxExtent? other) => other is not null && Multiplier == other.Multiplier && Divisor == other.Divisor &&
        Factors.SequenceEqual(other.Factors, StringComparer.Ordinal) && DivisorFactors.SequenceEqual(other.DivisorFactors, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as PtxExtent);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToString());

    /// <summary>Formats the extent as an expression, such as <c>ceil(N*C/blockX)</c>.</summary>
    /// <returns>The expression.</returns>
    public override string ToString()
    {
        var top = Factors.ToList();
        if (Multiplier != 1 || top.Count == 0) top.Insert(0, Multiplier.ToString(CultureInfo.InvariantCulture));
        var bottom = DivisorFactors.ToList();
        if (Divisor != 1) bottom.Insert(0, Divisor.ToString(CultureInfo.InvariantCulture));
        return bottom.Count == 0 ? string.Join("*", top) : "ceil(" + string.Join("*", top) + "/" + string.Join("*", bottom) + ")";
    }
}

internal static class PtxNames
{
    internal static bool IsIdentifier(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 64 && (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$');
}