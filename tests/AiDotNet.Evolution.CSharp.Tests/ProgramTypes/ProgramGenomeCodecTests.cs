// Source: ooples/AiDotNet 66d7602c92101e5ab2bd9db8cfa7f7526fa2c75d:tests/AiDotNet.Tests/UnitTests/Evolution/Programs/ProgramGenomeCodecTests.cs; original BSL license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using System.IO;
using System.Text;
using AiDotNet.Evolution.Programs;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AiDotNetTests.UnitTests.Evolution.Programs;

public sealed class ProgramGenomeCodecTests
{
    private static readonly ProgramGenomeCodec Codec = new();

    [Theory]
    [InlineData("print(1)", ProgramLanguage.Python, null)]
    [InlineData("Console.WriteLine(1);", ProgramLanguage.CSharp, "seed")]
    [InlineData("SELECT 1;", ProgramLanguage.SQL, "")]
    public void RoundTripPreservesEveryField(string source, ProgramLanguage language, string? description)
    {
        var genome = new ProgramGenome(source, language, description);
        ProgramGenome restored = Codec.Deserialize(Codec.Serialize(genome));

        Assert.Equal(genome.Source, restored.Source);
        Assert.Equal(genome.Language, restored.Language);
        Assert.Equal(genome.Description, restored.Description);
        Assert.Equal(genome.Id, restored.Id);
        Assert.Equal(genome, restored);
    }

    [Fact]
    public void RoundTripSurvivesCarriageReturnsTabsAndUnicode()
    {
        var genome = new ProgramGenome(
            "def f():\r\n\treturn \"caf\u00E9 \uD83D\uDE80\"\r\n", ProgramLanguage.Python);
        ProgramGenome restored = Codec.Deserialize(Codec.Serialize(genome));
        Assert.Equal(genome.Source, restored.Source);
        Assert.Equal(genome.Id, restored.Id);
    }

    [Fact]
    public void SerializationIsDeterministic()
    {
        var genome = new ProgramGenome("print(1)", ProgramLanguage.Python, "seed");
        string first = Codec.Serialize(genome);
        for (int index = 0; index < 5; index++) Assert.Equal(first, Codec.Serialize(genome));
        Assert.Equal(first, new ProgramGenomeCodec().Serialize(new ProgramGenome("print(1)", ProgramLanguage.Python, "seed")));
    }

    [Fact]
    public void OmittedDescriptionStaysNullAfterRoundTrip()
    {
        var genome = new ProgramGenome("print(1)", ProgramLanguage.Python);
        string payload = Codec.Serialize(genome);
        Assert.DoesNotContain("description", payload, StringComparison.Ordinal);
        Assert.Null(Codec.Deserialize(payload).Description);
    }

    [Fact]
    public void IdentityIsStable()
    {
        Assert.Equal("program-genome", Codec.Id);
        Assert.Equal("program-genome-v2-exact-source", Codec.VersionHash);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":1,\"language\":\"Python\"}")]
    [InlineData("{\"v\":1,\"language\":\"Klingon\",\"source\":\"x\"}")]
    [InlineData("{\"v\":2,\"language\":\"Python\",\"source\":\"x\"}")]
    [InlineData("{\"v\":\"one\",\"language\":\"Python\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"   \"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\"} 1")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\"}{}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\"")]
    public void MalformedPayloadsRaiseInvalidData(string payload)
    {
        Assert.Throws<InvalidDataException>(() => Codec.Deserialize(payload));
        Assert.Throws<InvalidDataException>(() => Codec.Deserialize(Encoding.UTF8.GetBytes(payload).AsSpan()));
    }

    // A resume hands the codec each archived payload as UTF-8 (E-1); bytes that are not UTF-8 are a corrupt payload,
    // wherever they sit: in a field the byte reader reads, or in text it hands to the general reader.
    [Theory]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x \u0001 y\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\\n\u0001\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\",\"extra\":\"\u0001\"}")]
    public void PayloadBytesThatAreNotUtf8RaiseInvalidData(string template)
    {
        byte[] payload = Encoding.UTF8.GetBytes(template);
        payload[Array.IndexOf(payload, (byte)0x01)] = 0xFF;
        Assert.Throws<InvalidDataException>(() => Codec.Deserialize(payload.AsSpan()));
    }

    // E-1 reads payloads through pooled reader buffers. A source far longer than the reader's first buffer, with escapes
    // throughout, must still come back exactly, and decoding many in a row must not let one payload's text leak into the
    // next through a reused buffer.
    [Fact]
    public void LongEscapedSourcesRoundTripExactlyThroughReusedBuffers()
    {
        for (int round = 0; round < 64; round++)
        {
            string source = "# candidate " + round + "\r\n" + string.Concat(Enumerable.Range(0, 300)
                .Select(line => "value_" + line + " = \"\\t" + (round * 300 + line) + "\"\t# caf\u00E9 \uD83D\uDE80\n"));
            var genome = new ProgramGenome(source, ProgramLanguage.Python, round % 2 == 0 ? null : "d" + round);
            string payload = Codec.Serialize(genome);
            foreach (ProgramGenome restored in new[] { Codec.Deserialize(payload), Codec.Deserialize(Encoding.UTF8.GetBytes(payload).AsSpan()) })
            {
                Assert.Equal(genome.Source, restored.Source);
                Assert.Equal(genome.Description, restored.Description);
                Assert.Equal(genome.Id, restored.Id);
            }
        }
    }

    // JObject.Parse accepts a trailing comment and white space after the object; the pooled reader keeps that.
    [Fact]
    public void TrailingCommentsAndWhiteSpaceAreStillAccepted()
    {
        ProgramGenome restored = Codec.Deserialize("{\"v\":1,\"language\":\"Python\",\"source\":\"x\"} /* note */ \n");
        Assert.Equal("x", restored.Source);
    }

    // E-1 reads canonical payloads without Json.NET and sends everything else to it. Whichever path a payload takes, it
    // must decode to what Json.NET's JObject reading gives, or be refused when that reading is refused.
    [Theory]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x = 1\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x = 1\",\"description\":\"d\"}")]
    [InlineData("{\"source\":\"x\",\"description\":\"d\",\"language\":\"Python\",\"v\":1}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"2020-01-01T00:00:00Z\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\",\"description\":\"2020-01-01T00:00:00\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"\\/Date(1234567890000)\\/\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"first\",\"source\":\"second\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\",\"extra\":[1,2]}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\",\"description\":null}")]
    [InlineData("{\"v\":1,\"language\":\"1\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python, CSharp\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"caf\\u00e9 \\ud83d\\ude80\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"lone \\ud800 surrogate\"}")]
    [InlineData("{\"\\u0076\":1,\"language\":\"Python\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1.0,\"language\":\"Python\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\"} // trailing")]
    [InlineData("{'v':1,'language':'Python','source':'x'}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\",}")]
    [InlineData("  {\"v\":1,\"language\":\"Python\",\"source\":\"x\"}  ")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"tab\\there\\nnew \\\"quoted\\\" \\\\ back\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"\\b\\f\\/\\r \\u0041\\u00E9\\u00e9\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"\\u0031 starts with a digit once unescaped\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"\\/ starts with a slash once unescaped\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"bad \\x escape\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"single \\u12 hex\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"apostrophe \\u0027 and \\\' escape\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"raw\ttab and raw\nnewline\"}")]
    [InlineData("{\"v\": 1, \"language\": \"Python\", \"source\": \"x\"}")]
    [InlineData("{\"v\":12,\"language\":\"Python\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"unterminated")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\"}}")]
    [InlineData("{\"v\":1,\"lang\\u0075age\":\"Python\",\"source\":\"x\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"x\",\"description\":\"\"}")]
    [InlineData("{\"v\":1,\"language\":\"Python\",\"source\":\"\"}")]
    public void EveryPayloadDecodesAsJsonNetReadsIt(string payload)
    {
        (string? Language, string? Source, string? Description)? expected;
        try
        {
            JObject parsed = JObject.Parse(payload);
            JToken? version = parsed["v"];
            expected = version is { Type: JTokenType.Integer } && version.Value<int>() == ProgramGenomeCodec.PayloadVersion
                ? (parsed["language"]?.Value<string>(), parsed["source"]?.Value<string>(), parsed["description"]?.Value<string>())
                : null;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            expected = null;
        }

        ProgramGenome? decoded;
        try
        {
            decoded = Codec.Deserialize(payload);
        }
        catch (InvalidDataException)
        {
            decoded = null;
        }

        // Read from its UTF-8 bytes, as a resume reads it, the payload decodes to the same genome or is refused too.
        ProgramGenome? fromBytes;
        try
        {
            fromBytes = Codec.Deserialize(Encoding.UTF8.GetBytes(payload).AsSpan());
        }
        catch (InvalidDataException)
        {
            fromBytes = null;
        }
        Assert.Equal(decoded is null, fromBytes is null);
        if (decoded is not null)
        {
            Assert.Equal(decoded.Source, fromBytes!.Source);
            Assert.Equal(decoded.Description, fromBytes.Description);
            Assert.Equal(decoded.Language, fromBytes.Language);
        }

        if (decoded is null)
        {
            // Refused: Json.NET either refused it too, or read something that is not a valid genome.
            if (expected is { } value && value.Source is { } source && !string.IsNullOrWhiteSpace(value.Language) &&
                Enum.TryParse(value.Language, out ProgramLanguage language) && Enum.IsDefined(typeof(ProgramLanguage), language))
                Assert.Throws<ArgumentException>(() => new ProgramGenome(source, language, value.Description));
            return;
        }

        Assert.True(expected.HasValue, "the codec accepted a payload Json.NET refuses");
        (string? Language, string? Source, string? Description) read = expected.GetValueOrDefault();
        Assert.Equal(read.Source, decoded.Source);
        Assert.Equal(read.Description, decoded.Description);
        Assert.Equal((ProgramLanguage)Enum.Parse(typeof(ProgramLanguage), read.Language ?? string.Empty), decoded.Language);
    }

    [Fact]
    public void NullArgumentsAreRejected()
    {
#pragma warning disable CS8600, CS8625
        Assert.Throws<ArgumentNullException>(() => Codec.Serialize(null));
        Assert.Throws<ArgumentNullException>(() => Codec.Deserialize(null));
#pragma warning restore CS8600, CS8625
    }
}
