// Source: ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d:src/Evolution/Programs/ProgramGenomeCodec.cs; original BSL license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.IO;
using System.Runtime.CompilerServices;
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

    // The payloads Serialize writes, read straight from the text without building a JObject: a resume decodes every
    // archived genome, and the general reader was most of its decoding time (E-1). This accepts only text whose meaning
    // under the general path is certain: the compact form Serialize writes, one object with no white space or comments
    // outside its strings, whose properties are exactly v (the integer 1), language, source and optionally description,
    // each once, each name unescaped and each value a plain string. Anything else - other properties, repeats, other
    // types, a raw control character, an escaped surrogate, a string Json.NET would read as a date - returns false and
    // takes the general path, so a payload decodes, or is refused, exactly as before.
    // Fully optimized from the first call: a resume decodes every archived genome within about a second, before tiered
    // compilation would promote this (E-1).
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryReadCanonical(string payload, out string? language, out string? source, out string? description)
    {
        language = null;
        source = null;
        description = null;
        ReadOnlySpan<char> text = payload.AsSpan();
        if (text.Length == 0 || text[0] != '{') return false;
        int position = 1;
        bool sawVersion = false;
        while (true)
        {
            if (position >= text.Length || text[position] != '"') return false;
            int nameLength = text.Slice(position + 1).IndexOf('"');
            if (nameLength < 0) return false;
            ReadOnlySpan<char> name = text.Slice(position + 1, nameLength);
            position += nameLength + 2;
            if (position >= text.Length || text[position] != ':') return false;
            position++;

            if (name.SequenceEqual("v".AsSpan()))
            {
                if (sawVersion || position >= text.Length || text[position] != '1') return false;
                sawVersion = true;
                position++;
            }
            else if (name.SequenceEqual("language".AsSpan()))
            {
                if (language is not null || !TryReadPlainString(payload, ref position, out language)) return false;
            }
            else if (name.SequenceEqual("source".AsSpan()))
            {
                if (source is not null || !TryReadPlainString(payload, ref position, out source)) return false;
            }
            else if (name.SequenceEqual("description".AsSpan()))
            {
                if (description is not null || !TryReadPlainString(payload, ref position, out description)) return false;
            }
            else
            {
                // Includes any name holding an escape, which can equal none of the four.
                return false;
            }

            if (position >= text.Length) return false;
            char separator = text[position++];
            if (separator == '}') break;
            if (separator != ',') return false;
        }

        return position == text.Length && sawVersion && language is not null && source is not null;
    }

    // A JSON string value at position that Json.NET would also read as that exact string; on success position is just
    // past its closing quote. With its default date handling, Json.NET turns strings that look like ISO or Microsoft JSON
    // dates into dates, and those begin with a digit or a slash.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryReadPlainString(string payload, ref int position, out string? value)
    {
        value = null;
        ReadOnlySpan<char> text = payload.AsSpan();
        if (position >= text.Length || text[position] != '"') return false;
        int start = position + 1;
        int run = text.Slice(start).IndexOfAny('"', '\\');
        if (run < 0) return false;
        string read;
        int end;
        if (text[start + run] == '"')
        {
            // No escapes: the value is the text itself.
            if (text.Slice(start, run).IndexOfAnyInRange('\u0000', '\u001F') >= 0) return false;
            read = payload.Substring(start, run);
            end = start + run;
        }
        else if (!TryUnescape(text, start, out read, out end))
        {
            return false;
        }

        if (read.Length > 0 && (char.IsDigit(read[0]) || read[0] == '/')) return false;
        value = read;
        position = end + 1;
        return true;
    }

    // Unescapes a string that holds at least one escape, from start to its closing quote, which end is set to.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool TryUnescape(ReadOnlySpan<char> text, int start, out string value, out int end)
    {
        value = string.Empty;
        end = -1;
        char[] buffer = System.Buffers.ArrayPool<char>.Shared.Rent(text.Length - start);
        try
        {
            int length = 0;
            int index = start;
            while (true)
            {
                int run = text.Slice(index).IndexOfAny('"', '\\');
                if (run < 0) return false;
                ReadOnlySpan<char> plain = text.Slice(index, run);
                if (plain.IndexOfAnyInRange('\u0000', '\u001F') >= 0) return false;
                plain.CopyTo(buffer.AsSpan(length));
                length += run;
                index += run;
                if (text[index] == '"') break;

                if (index + 1 >= text.Length) return false;
                char escaped = text[index + 1];
                index += 2;
                char unescaped;
                switch (escaped)
                {
                    case '"': unescaped = '"'; break;
                    case '\\': unescaped = '\\'; break;
                    case '/': unescaped = '/'; break;
                    case 'b': unescaped = '\b'; break;
                    case 'f': unescaped = '\f'; break;
                    case 'n': unescaped = '\n'; break;
                    case 'r': unescaped = '\r'; break;
                    case 't': unescaped = '\t'; break;
                    case 'u':
                        if (index + 4 > text.Length ||
                            !ushort.TryParse(text.Slice(index, 4), System.Globalization.NumberStyles.AllowHexSpecifier,
                                System.Globalization.CultureInfo.InvariantCulture, out ushort code) ||
                            char.IsSurrogate((char)code))
                            return false;
                        unescaped = (char)code;
                        index += 4;
                        break;
                    default:
                        return false;
                }

                buffer[length++] = unescaped;
            }

            value = new string(buffer, 0, length);
            end = index;
            return true;
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(buffer);
        }
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
