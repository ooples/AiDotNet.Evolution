// Source: ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d:src/Evolution/Programs/ProgramGenomeCodec.cs; original BSL license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AiDotNet.Evolution.Programs;

/// <summary>Serializes <see cref="ProgramGenome"/> instances for portable evolution checkpoints.</summary>
/// <remarks>
/// <para>
/// The payload is a compact JSON object with a fixed property order — <c>v</c>, <c>language</c>, <c>source</c>,
/// then <c>description</c> when one is present — written with the invariant culture, so serializing the same
/// genome twice produces byte-identical text. That determinism is a contract requirement:
/// <c>EvolutionEngine&lt;TGenome&gt;</c> compares checkpointed seed payloads with ordinal string equality when
/// resuming, and a codec that reordered properties would make every resume look incompatible.
/// </para>
/// <para>
/// <see cref="VersionHash"/> is folded into the engine's compatibility hash. Change it whenever the payload shape
/// changes so older checkpoints are refused outright rather than being misread, and keep <see cref="Id"/> stable
/// for the lifetime of the format. Malformed or truncated payloads raise <see cref="InvalidDataException"/> as the
/// interface requires, never a JSON-library exception.
/// </para>
/// <para><b>For Beginners:</b> A long evolution run needs to be able to stop and start again, which means writing
/// every candidate program to disk and reading it back. This class is the save-file format for one program: it
/// turns a genome into a small line of JSON and turns that JSON back into an identical genome. You only need it
/// when you give the engine a checkpoint store; runs that never pause do not use it at all.</para>
/// </remarks>
public sealed class ProgramGenomeCodec : IEvolutionGenomeCodec<ProgramGenome>
{
    /// <summary>The payload schema version written into every serialized genome.</summary>
    public const int PayloadVersion = 1;

    /// <inheritdoc/>
    public string Id => "program-genome";

    /// <inheritdoc/>
    public string VersionHash => "program-genome-v2-exact-source";

    /// <inheritdoc/>
    public string Serialize(ProgramGenome genome)
    {
        if (genome is null) throw new ArgumentNullException(nameof(genome));

        var payload = new JObject
        {
            ["v"] = PayloadVersion,
            ["language"] = genome.Language.ToString(),
            ["source"] = genome.Source
        };

        if (genome.Description is not null) payload["description"] = genome.Description;
        return payload.ToString(Formatting.None);
    }

    // JObject.Parse(string), with the reader's buffers rented instead of allocated: a resume decodes every archived genome,
    // and at 4 KB a genome the reader's growing buffers were about 34 KB of garbage each, most of a resume's allocation
    // (E-1). The reader and settings are otherwise JObject.Parse's own, so what parses, and how, is unchanged.
    private static JObject Parse(string payload)
    {
        using var reader = new JsonTextReader(new StringReader(payload)) { ArrayPool = SharedCharPool.Instance };
        JObject parsed = JObject.Load(reader);
        // As in JObject.Parse: anything after the object other than a comment makes the reader throw.
        while (reader.Read())
        {
        }

        return parsed;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The payloads Serialize writes, read without building a JObject: a resume decodes every archived genome, and the
    // general reader was most of its decoding time (E-1). This accepts only text whose meaning under the general path is
    // certain: strict JSON, one object whose properties are exactly v (the integer 1), language, source and optionally
    // description, each once and each a plain string. Anything else - comments, other properties, repeats, other
    // types, a string Json.NET would read as a date, a lone surrogate - returns false and takes the general path, so a
    // payload decodes, or is refused, exactly as before.
    private static bool TryReadCanonical(string payload, out string? language, out string? source, out string? description)
    {
        language = null;
        source = null;
        description = null;
        byte[] utf8;
        try
        {
            utf8 = StrictUtf8.GetBytes(payload);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(utf8, new System.Text.Json.JsonReaderOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Disallow
            });
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject) return false;
            bool sawVersion = false;
            while (true)
            {
                if (!reader.Read()) return false;
                if (reader.TokenType == System.Text.Json.JsonTokenType.EndObject) break;
                if (reader.TokenType != System.Text.Json.JsonTokenType.PropertyName) return false;
                if (reader.ValueTextEquals("v"u8))
                {
                    if (sawVersion || !reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.Number ||
                        reader.HasValueSequence || reader.ValueSpan.Length != 1 || reader.ValueSpan[0] != (byte)'1')
                        return false;
                    sawVersion = true;
                }
                else if (reader.ValueTextEquals("language"u8))
                {
                    if (language is not null || !TryReadPlainString(ref reader, out language)) return false;
                }
                else if (reader.ValueTextEquals("source"u8))
                {
                    if (source is not null || !TryReadPlainString(ref reader, out source)) return false;
                }
                else if (reader.ValueTextEquals("description"u8))
                {
                    if (description is not null || !TryReadPlainString(ref reader, out description)) return false;
                }
                else
                {
                    return false;
                }
            }

            // Only white space may follow the object.
            if (reader.Read()) return false;
            return sawVersion && language is not null && source is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // A string value that Json.NET would also read as that exact string. With its default date handling, Json.NET turns
    // strings that look like ISO or Microsoft JSON dates into dates, which begin with a digit or a slash.
    private static bool TryReadPlainString(ref System.Text.Json.Utf8JsonReader reader, out string? value)
    {
        value = null;
        if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.String) return false;
        string text = reader.GetString() ?? string.Empty;
        if (text.Length > 0 && (char.IsDigit(text[0]) || text[0] == '/')) return false;
        value = text;
        return true;
    }

    private sealed class SharedCharPool : IArrayPool<char>
    {
        internal static readonly SharedCharPool Instance = new();

        public char[] Rent(int minimumLength) => System.Buffers.ArrayPool<char>.Shared.Rent(minimumLength);

        public void Return(char[]? array)
        {
            if (array is not null) System.Buffers.ArrayPool<char>.Shared.Return(array);
        }
    }

    /// <inheritdoc/>
    public ProgramGenome Deserialize(string payload)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));

        string? languageName;
        string? source;
        string? description;
        if (!TryReadCanonical(payload, out languageName, out source, out description))
        {
            JObject parsed;
            try
            {
                parsed = Parse(payload);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The program genome payload is not valid JSON.", exception);
            }

            JToken? versionToken = parsed["v"];
            if (versionToken is null || versionToken.Type != JTokenType.Integer)
                throw new InvalidDataException("The program genome payload is missing its integer version field.");
            int version = versionToken.Value<int>();
            if (version != PayloadVersion)
                throw new InvalidDataException($"Unsupported program genome payload version {version}.");

            languageName = parsed["language"]?.Value<string>();
            source = parsed["source"]?.Value<string>();
            description = parsed["description"]?.Value<string>();
        }

        if (string.IsNullOrWhiteSpace(languageName))
            throw new InvalidDataException("The program genome payload is missing its language field.");

        ProgramLanguage language;
        try
        {
            language = (ProgramLanguage)Enum.Parse(typeof(ProgramLanguage), languageName, ignoreCase: false);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Unknown program language '{languageName}'.", exception);
        }

        if (source is null) throw new InvalidDataException("The program genome payload is missing its source field.");

        try
        {
            return new ProgramGenome(source, language, description);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The program genome payload does not describe a valid genome.", exception);
        }
    }
}
