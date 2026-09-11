using System.Globalization;

namespace AiDotNet.Evolution;

/// <summary>An immutable numeric or categorical parameter value.</summary>
public sealed class EvolutionParameterValue : IEquatable<EvolutionParameterValue>
{
    private readonly double _number;
    private readonly string? _category;
    private EvolutionParameterValue(double number, string? category) { _number = number == 0 ? 0 : number; _category = category; }
    /// <summary>Creates a finite numeric value. Integer-domain validation belongs to the parameter definition.</summary>
    public static EvolutionParameterValue Numeric(double value) => EvolutionDescriptorDefinition.IsFinite(value)
        ? new(value, null) : throw new ArgumentOutOfRangeException(nameof(value));
    /// <summary>Creates a printable, nonempty categorical value of at most 128 characters.</summary>
    public static EvolutionParameterValue Categorical(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
            throw new ArgumentException("Invalid category value.", nameof(value));
        return new(0, value);
    }
    /// <summary>Gets whether this value is numeric.</summary>
    public bool IsNumeric => _category is null;
    /// <summary>Gets the numeric value, refusing a categorical value.</summary>
    public double Number => IsNumeric ? _number : throw new InvalidOperationException("The value is categorical.");
    /// <summary>Gets the category, refusing a numeric value.</summary>
    public string Category => _category ?? throw new InvalidOperationException("The value is numeric.");
    internal string Canonical => IsNumeric ? "n:" + BitConverter.DoubleToInt64Bits(_number).ToString("X16", CultureInfo.InvariantCulture) : "c:" + _category;
    /// <inheritdoc/>
    public bool Equals(EvolutionParameterValue? other) => other is not null && IsNumeric == other.IsNumeric &&
        (IsNumeric ? _number.Equals(other._number) : string.Equals(_category, other._category, StringComparison.Ordinal));
    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EvolutionParameterValue value && Equals(value);
    /// <inheritdoc/>
    public override int GetHashCode() => IsNumeric ? _number.GetHashCode() : StringComparer.Ordinal.GetHashCode(_category!);
    /// <inheritdoc/>
    public override string ToString() => IsNumeric ? _number.ToString("R", CultureInfo.InvariantCulture) : _category!;
}

/// <summary>The sampling and normalization domain of a parameter.</summary>
public enum EvolutionParameterKind
{
    /// <summary>A bounded continuous value sampled uniformly.</summary>
    Real,
    /// <summary>A bounded integral value, including both endpoints.</summary>
    Integer,
    /// <summary>A positive continuous value sampled uniformly in logarithmic coordinates.</summary>
    Logarithmic,
    /// <summary>One of a finite set of named choices.</summary>
    Categorical
}

/// <summary>An activation condition on an earlier parameter. Multiple conditions are combined with AND.</summary>
public sealed class EvolutionParameterCondition
{
    internal EvolutionParameterCondition(string parameter, EvolutionParameterValue[] values)
    { Parameter = parameter; AnyOf = Array.AsReadOnly(values); }
    /// <summary>Gets the name of the earlier parameter.</summary>
    public string Parameter { get; }
    /// <summary>Gets the allowed values of that parent (OR within this condition).</summary>
    public IReadOnlyList<EvolutionParameterValue> AnyOf { get; }
}

/// <summary>An immutable parameter domain with optional activation conditions.</summary>
public sealed class EvolutionParameter
{
    private EvolutionParameter(string name, EvolutionParameterKind kind, double minimum, double maximum,
        string[] categories, EvolutionParameterCondition[] conditions)
    {
        ValidateName(name);
        Name = name; Kind = kind; Minimum = minimum == 0 ? 0 : minimum; Maximum = maximum == 0 ? 0 : maximum;
        Categories = Array.AsReadOnly(categories); Conditions = Array.AsReadOnly(conditions);
    }
    /// <summary>Gets the unique parameter name.</summary>
    public string Name { get; }
    /// <summary>Gets the parameter domain.</summary>
    public EvolutionParameterKind Kind { get; }
    /// <summary>Gets the inclusive numeric lower bound (zero for categorical domains).</summary>
    public double Minimum { get; }
    /// <summary>Gets the inclusive numeric upper bound (zero for categorical domains).</summary>
    public double Maximum { get; }
    /// <summary>Gets categorical choices in declared order.</summary>
    public IReadOnlyList<string> Categories { get; }
    /// <summary>Gets activation conditions, all of which must hold.</summary>
    public IReadOnlyList<EvolutionParameterCondition> Conditions { get; }

    /// <summary>Defines a finite continuous interval. A zero-width interval is a fixed parameter.</summary>
    public static EvolutionParameter Real(string name, double minimum, double maximum) => Numeric(name, EvolutionParameterKind.Real, minimum, maximum);
    /// <summary>Defines an inclusive 32-bit integer interval.</summary>
    public static EvolutionParameter Integer(string name, int minimum, int maximum) => Numeric(name, EvolutionParameterKind.Integer, minimum, maximum);
    /// <summary>Defines a strictly positive interval sampled and mutated in log space.</summary>
    public static EvolutionParameter Logarithmic(string name, double minimum, double maximum) => Numeric(name, EvolutionParameterKind.Logarithmic, minimum, maximum);
    /// <summary>Defines between one and 256 distinct categorical choices.</summary>
    public static EvolutionParameter Categorical(string name, IEnumerable<string> choices)
    {
        Guard.NotNull(choices);
        string[] values = choices.Take(257).ToArray();
        if (values.Length is < 1 or > 256 || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw new ArgumentException("Supply between one and 256 unique categories.", nameof(choices));
        foreach (string value in values) EvolutionParameterValue.Categorical(value);
        return new(name, EvolutionParameterKind.Categorical, 0, 0, values, Array.Empty<EvolutionParameterCondition>());
    }

    /// <summary>Returns a copy activated only when an earlier parent has one of the specified values.</summary>
    public EvolutionParameter When(string parent, params EvolutionParameterValue[] anyOf)
    {
        ValidateName(parent); Guard.NotNull(anyOf);
        if (Conditions.Count >= 16 || anyOf.Length is < 1 or > 256 || anyOf.Any(value => value is null) ||
            Conditions.Any(condition => condition.Parameter == parent))
            throw new ArgumentException("Invalid or repeated activation condition.", nameof(anyOf));
        EvolutionParameterValue[] values = anyOf.Distinct().OrderBy(value => value.Canonical, StringComparer.Ordinal).ToArray();
        return new(Name, Kind, Minimum, Maximum, Categories.ToArray(), Conditions.Concat(new[] { new EvolutionParameterCondition(parent, values) }).ToArray());
    }

    /// <summary>Checks type, bounds, integrality and categorical membership without coercion.</summary>
    public bool Contains(EvolutionParameterValue value)
    {
        if (value is null) return false;
        if (Kind == EvolutionParameterKind.Categorical)
            return !value.IsNumeric && Categories.Contains(value.Category, StringComparer.Ordinal);
        if (!value.IsNumeric) return false;
        double number = value.Number;
        bool withinBounds = number >= Minimum && number <= Maximum;
        if (!withinBounds) return false;
        return Kind != EvolutionParameterKind.Integer || number == Math.Truncate(number);
    }

    /// <summary>Samples a value using only the supplied stable random stream.</summary>
    public EvolutionParameterValue Sample(StableRandom random)
    {
        Guard.NotNull(random);
        if (Kind == EvolutionParameterKind.Categorical) return EvolutionParameterValue.Categorical(Categories[random.NextInt(Categories.Count)]);
        if (Kind == EvolutionParameterKind.Integer)
            return EvolutionParameterValue.Numeric(Minimum + Math.Floor(random.NextDouble() * (Maximum - Minimum + 1)));
        return FromNormalized(random.NextDouble());
    }

    /// <summary>Maps a numeric value to [0,1], using logarithmic coordinates where declared.</summary>
    public double Normalize(EvolutionParameterValue value)
    {
        if (!Contains(value) || Kind == EvolutionParameterKind.Categorical) throw new ArgumentException("A valid numeric value is required.", nameof(value));
        if (Maximum == Minimum) return 0;
        return Kind == EvolutionParameterKind.Logarithmic && !HasCollapsedLogSpan
            ? (Math.Log(value.Number) - Math.Log(Minimum)) / (Math.Log(Maximum) - Math.Log(Minimum))
            : (value.Number - Minimum) / (Maximum - Minimum);
    }

    /// <summary>Decodes a normalized numeric coordinate, clipping finite out-of-range values and rounding integers.</summary>
    public EvolutionParameterValue FromNormalized(double coordinate)
    {
        if (!EvolutionDescriptorDefinition.IsFinite(coordinate) || Kind == EvolutionParameterKind.Categorical)
            throw new ArgumentOutOfRangeException(nameof(coordinate));
        double t = Math.Max(0, Math.Min(1, coordinate));
        double value = Kind == EvolutionParameterKind.Logarithmic && !HasCollapsedLogSpan
            ? Math.Exp(Math.Log(Minimum) + t * (Math.Log(Maximum) - Math.Log(Minimum)))
            : Minimum + t * (Maximum - Minimum);
        value = Math.Max(Minimum, Math.Min(Maximum, value));
        return EvolutionParameterValue.Numeric(Kind == EvolutionParameterKind.Integer ? Math.Round(value, MidpointRounding.AwayFromZero) : value);
    }

    internal bool IsActive(IReadOnlyDictionary<string, EvolutionParameterValue> values) => Conditions.All(condition =>
        values.TryGetValue(condition.Parameter, out EvolutionParameterValue? parent) && condition.AnyOf.Contains(parent));
    // Adjacent positive doubles can have equal rounded logarithms. Their relative span is below log
    // resolution, so use bounded linear interpolation rather than dividing zero by zero or collapsing sampling.
    private bool HasCollapsedLogSpan => Maximum > Minimum && Math.Log(Maximum) == Math.Log(Minimum);
    internal string DefinitionHash => EvolutionHash.Combine(new[] { Name, Kind.ToString(),
        EvolutionParameterValue.Numeric(Minimum).Canonical, EvolutionParameterValue.Numeric(Maximum).Canonical,
        EvolutionHash.Combine(Categories) }.Concat(Conditions.OrderBy(condition => condition.Parameter, StringComparer.Ordinal)
        .Select(condition => EvolutionHash.Combine(new[] { condition.Parameter }.Concat(condition.AnyOf.Select(value => value.Canonical)))))
        .Concat(Kind == EvolutionParameterKind.Logarithmic ? new[] { "log-domain-v2-finite-narrow" } : Array.Empty<string>()));

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(character => !char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.'))
            throw new ArgumentException("Parameter names must use letters, digits, underscore, dot or hyphen, with at most 64 characters.", nameof(name));
    }
    private static EvolutionParameter Numeric(string name, EvolutionParameterKind kind, double minimum, double maximum)
    {
        if (!EvolutionDescriptorDefinition.IsFinite(minimum) || !EvolutionDescriptorDefinition.IsFinite(maximum) ||
            maximum < minimum || !EvolutionDescriptorDefinition.IsFinite(maximum - minimum) ||
            (kind == EvolutionParameterKind.Logarithmic && minimum <= 0)) throw new ArgumentOutOfRangeException(nameof(minimum));
        return new(name, kind, minimum, maximum, Array.Empty<string>(), Array.Empty<EvolutionParameterCondition>());
    }
}
