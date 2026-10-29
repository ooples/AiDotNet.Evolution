using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

// An archived genome's payload. Written from the codec's string; read back as its unescaped UTF-8 bytes, so a codec that
// reads UTF-8 decodes the genome without the payload ever becoming a string. At 10,000 elites the payload strings were
// 54 MB of a resume's 264 MB of allocation, and their UTF-8 bytes are about half that (E-1).
[JsonConverter(typeof(EvolutionGenomePayloadConverter))]
internal sealed class EvolutionGenomePayload
{
    internal static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[]? _utf8;
    private string? _text;

    internal EvolutionGenomePayload(string text) => _text = text ?? throw new ArgumentNullException(nameof(text));

    internal EvolutionGenomePayload(byte[] utf8) => _utf8 = utf8 ?? throw new ArgumentNullException(nameof(utf8));

    // The payload's UTF-8 bytes when it was read from a checkpoint; null when it was built from a string.
    internal byte[]? Utf8 => _utf8;

    // The payload as text, decoded on first use when it was read; its bytes were checked as UTF-8 then.
    internal string Text => _text ??= StrictUtf8.GetString(_utf8!);
}

internal sealed class EvolutionGenomePayloadConverter : JsonConverter<EvolutionGenomePayload>
{
    public override EvolutionGenomePayload Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("A genome payload must be a JSON string.");
        ReadOnlySpan<byte> raw = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
        byte[] utf8;
        if (!reader.ValueIsEscaped)
        {
            utf8 = raw.ToArray();
        }
        else
        {
            // Unescaping only shortens the text, so the escaped length bounds it.
            byte[] buffer = ArrayPool<byte>.Shared.Rent(raw.Length);
            try
            {
                // An escape that is not valid UTF-16 makes the reader throw, which the serializer reports as a JsonException.
                int written = reader.CopyString(buffer);
                utf8 = buffer.AsSpan(0, written).ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Refused here, as reading the payload as a string refused it, rather than by whichever codec reads it later.
#if NET8_0_OR_GREATER
        if (!System.Text.Unicode.Utf8.IsValid(utf8)) throw new JsonException("A genome payload is not valid UTF-8.");
#else
        try
        {
            EvolutionGenomePayload.StrictUtf8.GetCharCount(utf8);
        }
        catch (DecoderFallbackException exception)
        {
            throw new JsonException("A genome payload is not valid UTF-8.", exception);
        }
#endif
        return new EvolutionGenomePayload(utf8);
    }

    public override void Write(Utf8JsonWriter writer, EvolutionGenomePayload value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Text);
}