using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Builds an ordered, dependency-checked parameter space without changing existing spaces.</summary>
public sealed class EvolutionSearchSpaceBuilder
{
    private readonly List<EvolutionParameter> _parameters = new();
    /// <summary>Adds a unique parameter; conditional parents must precede their children.</summary>
    public EvolutionSearchSpaceBuilder Add(EvolutionParameter parameter)
    {
        Guard.NotNull(parameter);
        if (_parameters.Count >= 256 || _parameters.Any(prior => prior.Name == parameter.Name))
            throw new ArgumentException("A space supports at most 256 uniquely named parameters.", nameof(parameter));
        _parameters.Add(parameter); return this;
    }
    /// <summary>Validates dependencies and creates an immutable space.</summary>
    public EvolutionSearchSpace Build() => new(_parameters.ToArray());
}

/// <summary>An immutable, canonical, independently owned parameter assignment.</summary>
public sealed class EvolutionSearchGenome : IImmutableEvolutionGenome<EvolutionSearchGenome>
{
    internal EvolutionSearchGenome(string schemaHash, IReadOnlyDictionary<string, EvolutionParameterValue> values)
    {
        SchemaHash = schemaHash;
        var copy = new SortedDictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, EvolutionParameterValue> pair in values) copy.Add(pair.Key, pair.Value);
        Values = new ReadOnlyDictionary<string, EvolutionParameterValue>(copy);
        Identity = EvolutionHash.Combine(new[] { "search-genome-v1", schemaHash }.Concat(copy.Select(pair =>
            EvolutionHash.Combine(new[] { pair.Key, pair.Value.Canonical }))));
    }
    /// <summary>Gets the schema fingerprint required for compatible interpretation.</summary>
    public string SchemaHash { get; }
    /// <summary>Gets active values only; inactive parameters have no canonical value.</summary>
    public IReadOnlyDictionary<string, EvolutionParameterValue> Values { get; }
    /// <summary>Gets the schema-aware canonical identity.</summary>
    public string Identity { get; }
    /// <summary>Gets an active numeric parameter without coercion.</summary>
    public double Number(string name) => Values[name].Number;
    /// <summary>Gets an active categorical parameter without coercion.</summary>
    public string Category(string name) => Values[name].Category;
    /// <inheritdoc/>
    public EvolutionSearchGenome CreateOwnedSnapshot() => new(SchemaHash, Values);
}

/// <summary>An immutable mixed/conditional parameter space, sampler, feature encoder and checkpoint codec.</summary>
public sealed class EvolutionSearchSpace : IEvolutionGenomeCodec<EvolutionSearchGenome>
{
    internal EvolutionSearchSpace(EvolutionParameter[] parameters)
    {
        if (parameters.Length == 0) throw new ArgumentException("A search space needs at least one parameter.", nameof(parameters));
        var prior = new Dictionary<string, EvolutionParameter>(StringComparer.Ordinal);
        foreach (EvolutionParameter parameter in parameters)
        {
            foreach (EvolutionParameterCondition condition in parameter.Conditions)
            {
                if (!prior.TryGetValue(condition.Parameter, out EvolutionParameter? parent) || condition.AnyOf.Any(value => !parent.Contains(value)))
                    throw new ArgumentException("Conditions must reference earlier parameters and valid parent values.", nameof(parameters));
            }
            prior.Add(parameter.Name, parameter);
        }
        Parameters = Array.AsReadOnly(parameters);
        FeatureCount = parameters.Sum(parameter => parameter.Kind == EvolutionParameterKind.Categorical ? parameter.Categories.Count + 1 : 2);
        if (FeatureCount > 4096) throw new ArgumentException("The expanded feature vector exceeds 4096 coordinates.", nameof(parameters));
        VersionHash = EvolutionHash.Combine(new[] { "typed-search-space-v1" }.Concat(parameters.Select(parameter => parameter.DefinitionHash)));
    }
    /// <summary>Gets the domains in dependency order.</summary>
    public IReadOnlyList<EvolutionParameter> Parameters { get; }
    /// <summary>Gets the codec identity.</summary>
    public string Id => "typed-search-space";
    /// <summary>Gets the fingerprint of domains, ordering, conditions and encoding semantics.</summary>
    public string VersionHash { get; }
    /// <summary>Gets the fixed feature width: activity flags plus numeric coordinates or categorical one-hot values.</summary>
    public int FeatureCount { get; }

    /// <summary>Validates active values and drops inactive values, so irrelevant settings cannot create cache identities.</summary>
    public EvolutionSearchGenome CreateGenome(IEnumerable<KeyValuePair<string, EvolutionParameterValue>> values)
    {
        Guard.NotNull(values);
        var supplied = new Dictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, EvolutionParameterValue> pair in values)
        {
            if (supplied.Count >= Parameters.Count || pair.Key is null || supplied.ContainsKey(pair.Key) ||
                !Parameters.Any(parameter => parameter.Name == pair.Key) || pair.Value is null)
                throw new ArgumentException("Unknown, duplicate, null or excessive parameter value.", nameof(values));
            supplied.Add(pair.Key, pair.Value);
        }
        var active = new Dictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        foreach (EvolutionParameter parameter in Parameters)
        {
            if (!parameter.IsActive(active)) continue;
            if (!supplied.TryGetValue(parameter.Name, out EvolutionParameterValue? value) || !parameter.Contains(value))
                throw new ArgumentException("Missing or invalid active parameter: " + parameter.Name, nameof(values));
            active.Add(parameter.Name, value);
        }
        return new EvolutionSearchGenome(VersionHash, active);
    }

    /// <summary>Samples a complete valid candidate using only the supplied stream.</summary>
    public EvolutionSearchGenome Sample(StableRandom random)
    {
        Guard.NotNull(random);
        var values = new Dictionary<string, EvolutionParameterValue>(StringComparer.Ordinal);
        foreach (EvolutionParameter parameter in Parameters)
            if (parameter.IsActive(values)) values.Add(parameter.Name, parameter.Sample(random));
        return new EvolutionSearchGenome(VersionHash, values);
    }

    /// <summary>Validates schema/domain identity and returns an independently owned candidate.</summary>
    public EvolutionSearchGenome Validate(EvolutionSearchGenome genome)
    {
        Guard.NotNull(genome);
        if (genome.SchemaHash != VersionHash) throw new ArgumentException("The genome belongs to a different search space.", nameof(genome));
        return CreateGenome(genome.Values);
    }

    /// <summary>Encodes activity explicitly and categories one-hot; inactive and minimum-valued parameters remain distinguishable.</summary>
    public IReadOnlyList<double> EncodeFeatures(EvolutionSearchGenome genome)
    {
        genome = Validate(genome);
        var features = new double[FeatureCount];
        int offset = 0;
        foreach (EvolutionParameter parameter in Parameters)
        {
            bool active = genome.Values.TryGetValue(parameter.Name, out EvolutionParameterValue? value);
            features[offset++] = active ? 1 : 0;
            if (parameter.Kind == EvolutionParameterKind.Categorical)
                foreach (string category in parameter.Categories) features[offset++] = active && category == value!.Category ? 1 : 0;
            else features[offset++] = active ? parameter.Normalize(value!) : 0;
        }
        return Array.AsReadOnly(features);
    }

    /// <inheritdoc/>
    public string Serialize(EvolutionSearchGenome genome)
    {
        genome = Validate(genome);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("schema", VersionHash); writer.WriteStartObject("values");
            foreach (KeyValuePair<string, EvolutionParameterValue> pair in genome.Values)
                if (pair.Value.IsNumeric) writer.WriteNumber(pair.Key, pair.Value.Number); else writer.WriteString(pair.Key, pair.Value.Category);
            writer.WriteEndObject(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <inheritdoc/>
    public EvolutionSearchGenome Deserialize(string payload)
    {
        Guard.NotNull(payload);
        if (payload.Length > 256 * 1024) throw new ArgumentException("Search genome payload exceeds 256 KiB.", nameof(payload));
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
            !root.TryGetProperty("schema", out JsonElement schema) || schema.ValueKind != JsonValueKind.String || schema.GetString() != VersionHash ||
            !root.TryGetProperty("values", out JsonElement values) || values.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Invalid or incompatible search genome payload.", nameof(payload));
        return CreateGenome(values.EnumerateObject().Select(property => new KeyValuePair<string, EvolutionParameterValue>(property.Name,
            property.Value.ValueKind switch
            {
                JsonValueKind.Number => EvolutionParameterValue.Numeric(property.Value.GetDouble()),
                JsonValueKind.String => EvolutionParameterValue.Categorical(property.Value.GetString()!),
                _ => throw new ArgumentException("Parameter values must be numbers or strings.", nameof(payload))
            })));
    }
}
