using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AiDotNet.Evolution.Host;

/// <summary>Enforces request collection limits before allocating each next element.</summary>
internal abstract class BoundedRequestListConverter<TItem> : JsonConverter<List<TItem>> where TItem : class
{
    protected abstract int Limit { get; }
    protected abstract string CollectionName { get; }
    protected abstract JsonTypeInfo<TItem> ItemTypeInfo { get; }

    public override List<TItem> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"{CollectionName} must be an array.");
        var items = new List<TItem>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) return items;
            if (items.Count >= Limit)
                throw new JsonException($"{CollectionName} exceeds the {Limit} entry limit.");
            items.Add(ReadItem(ref reader));
        }
        throw new JsonException($"{CollectionName} is not a complete array.");
    }

    protected virtual TItem ReadItem(ref Utf8JsonReader reader) =>
        JsonSerializer.Deserialize(ref reader, ItemTypeInfo)
        ?? throw new JsonException($"{CollectionName} cannot contain a null entry.");

    public override void Write(Utf8JsonWriter writer, List<TItem> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (TItem item in value) JsonSerializer.Serialize(writer, item, ItemTypeInfo);
        writer.WriteEndArray();
    }
}

internal sealed class BoundedParameterListConverter : BoundedRequestListConverter<ParameterConfig>
{
    public override bool HandleNull => true;
    protected override int Limit => ProtocolLimits.MaxDimensions;
    protected override string CollectionName => "config.parameters";
    protected override JsonTypeInfo<ParameterConfig> ItemTypeInfo => HostJsonContext.Default.ParameterConfig;
}

internal sealed class BoundedDescriptorListConverter : BoundedRequestListConverter<DescriptorConfig>
{
    public override bool HandleNull => true;
    protected override int Limit => ProtocolLimits.MaxDimensions;
    protected override string CollectionName => "config.descriptors";
    protected override JsonTypeInfo<DescriptorConfig> ItemTypeInfo => HostJsonContext.Default.DescriptorConfig;
}

internal sealed class BoundedSeedListConverter : BoundedRequestListConverter<Dictionary<string, double>>
{
    private static readonly BoundedNumericMapConverter SeedMapConverter = new();
    protected override int Limit => ProtocolLimits.MaxSeeds;
    protected override string CollectionName => "config.seeds";
    protected override JsonTypeInfo<Dictionary<string, double>> ItemTypeInfo => HostJsonContext.Default.NumericMap;
    protected override Dictionary<string, double> ReadItem(ref Utf8JsonReader reader) =>
        SeedMapConverter.ReadMap(ref reader);
}

internal sealed class BoundedResultListConverter : BoundedRequestListConverter<TellResult>
{
    protected override int Limit => ProtocolLimits.MaxResults;
    protected override string CollectionName => "results";
    protected override JsonTypeInfo<TellResult> ItemTypeInfo => HostJsonContext.Default.TellResult;
}

internal abstract class BoundedMapConverter<TValue> : JsonConverter<Dictionary<string, TValue>>
{
    public override Dictionary<string, TValue> Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options) => ReadMap(ref reader);

    internal Dictionary<string, TValue> ReadMap(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
            throw new JsonException("A parameter or descriptor map must be an object.");
        var values = new Dictionary<string, TValue>(StringComparer.Ordinal);
        int entries = 0;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject) return values;
            if (reader.TokenType != JsonTokenType.PropertyName)
                throw new JsonException("A numeric map requires named entries.");
            // Count entries, not distinct keys: repeated names must not bypass the cap.
            if (entries >= ProtocolLimits.MaxDimensions)
                throw new JsonException($"A numeric map exceeds the {ProtocolLimits.MaxDimensions} entry limit.");
            entries += 1;
            string name = reader.GetString() ?? throw new JsonException("A numeric map requires a key.");
            if (!reader.Read()) throw new JsonException($"The map value for '{name}' is missing.");
            values[name] = ReadValue(ref reader, name);
        }
        throw new JsonException("The numeric map is not a complete object.");
    }

    protected abstract TValue ReadValue(ref Utf8JsonReader reader, string name);
    protected abstract void WriteValue(Utf8JsonWriter writer, string name, TValue value);

    public override void Write(Utf8JsonWriter writer, Dictionary<string, TValue> value,
        JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (KeyValuePair<string, TValue> item in value) WriteValue(writer, item.Key, item.Value);
        writer.WriteEndObject();
    }
}

/// <summary>Seed/parameter values must be numbers, never null measurements.</summary>
internal sealed class BoundedNumericMapConverter : BoundedMapConverter<double>
{
    protected override double ReadValue(ref Utf8JsonReader reader, string name) => ReadNumber(ref reader, name);

    internal static double ReadNumber(ref Utf8JsonReader reader, string name) =>
        reader.TokenType == JsonTokenType.Number ? reader.GetDouble()
            : throw new JsonException($"The map value for '{name}' must be a number.");

    protected override void WriteValue(Utf8JsonWriter writer, string name, double value) =>
        writer.WriteNumber(name, value);
}

/// <summary>JSON null is a missing measurement, including JSON.stringify of NaN/Infinity.</summary>
internal sealed class BoundedDescriptorMapConverter : BoundedMapConverter<double?>
{
    protected override double? ReadValue(ref Utf8JsonReader reader, string name) =>
        reader.TokenType == JsonTokenType.Null ? null : BoundedNumericMapConverter.ReadNumber(ref reader, name);

    protected override void WriteValue(Utf8JsonWriter writer, string name, double? value)
    {
        if (value is double number) writer.WriteNumber(name, number);
        else writer.WriteNull(name);
    }
}
