#if !NET471
using System.Text;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The wire boundary: what the host accepts, what it refuses, and what it says either way.
/// </summary>
/// <remarks>
/// These exist because the host is the one component with an untrusted input. Everything
/// else in this library is called by code that already type-checked its arguments; this
/// reads bytes from a pipe, and every one of the failures below reaches a client as
/// either a usable error or a mystery.
/// </remarks>
public sealed class HostProtocolTests
{
    // The expected value is a NAME rather than the enum: xUnit requires a public test
    // class and Protocol.Op is internal, so a public signature cannot mention it.
    [Theory]
    [InlineData("ping", "Ping")]
    [InlineData("open", "Open")]
    [InlineData("ask", "Ask")]
    [InlineData("tell", "Tell")]
    [InlineData("status", "Status")]
    [InlineData("close", "Close")]
    public void EveryDocumentedOpMaps(string wire, string expected) =>
        Assert.Equal(expected, Protocol.ParseOp(wire)?.ToString());

    [Theory]
    [InlineData("Ping")]
    [InlineData("PING")]
    [InlineData("asks")]
    [InlineData("")]
    [InlineData(" ask")]
    public void AnUnknownOpMapsToNothing(string wire) => Assert.Null(Protocol.ParseOp(wire));

    [Fact]
    public void AParseableRequestWithNoOpStillReportsItsId()
    {
        // THE WHOLE POINT OF THE ID. `{"id":42,"op":""}` is good JSON with a bad op, and
        // answering it with id 0 leaves the client unable to match the error to the
        // request in a protocol whose only ordering guarantee is that the id comes back.
        Request? request = Protocol.ParseRequest("{\"id\":42,\"op\":\"\"}", out string? error, out long id);

        Assert.Null(request);
        Assert.Equal(42, id);
        Assert.Contains("op", error, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJsonReportsNoIdBecauseThereIsNoneToRead()
    {
        Request? request = Protocol.ParseRequest("{\"id\":42,", out string? error, out long id);

        Assert.Null(request);
        Assert.Equal(0, id);
        Assert.Contains("malformed JSON", error, StringComparison.Ordinal);
    }

    [Fact]
    public void AWellFormedRequestParses()
    {
        Request? request = Protocol.ParseRequest(
            "{\"id\":7,\"op\":\"ask\",\"max\":4}", out string? error, out long id);

        Assert.NotNull(request);
        Assert.Null(error);
        Assert.Equal(7, id);
        Assert.Equal("ask", request!.Op);
        Assert.Equal(4, request.Max);
    }

    private static async Task<List<(string? Line, bool Overlong)>> ReadAllAsync(string input)
    {
        var reader = new FrameReader(new StringReader(input));
        var frames = new List<(string?, bool)>();
        while (true)
        {
            (string? line, bool overlong) = await reader.NextAsync();
            if (line is null) break;
            frames.Add((line, overlong));
        }
        return frames;
    }

    [Fact]
    public async Task FramesSplitOnNewlines()
    {
        List<(string? Line, bool Overlong)> frames = await ReadAllAsync("one\ntwo\nthree");

        Assert.Equal(new[] { "one", "two", "three" }, frames.ConvertAll(f => f.Line));
        Assert.All(frames, f => Assert.False(f.Overlong));
    }

    [Fact]
    public async Task CarriageReturnsAreDropped()
    {
        // A client on Windows may send CRLF, and the payload is JSON, where trailing
        // whitespace is insignificant. A CR left in place makes every frame unparseable.
        List<(string? Line, bool Overlong)> frames = await ReadAllAsync("{\"op\":\"ping\"}\r\n");

        Assert.Equal(new[] { "{\"op\":\"ping\"}" }, frames.ConvertAll(f => f.Line));
    }

    [Fact]
    public async Task AnEmptyFrameIsStillAFrame()
    {
        List<(string? Line, bool Overlong)> frames = await ReadAllAsync("a\n\nb\n");

        Assert.Equal(new[] { "a", string.Empty, "b" }, frames.ConvertAll(f => f.Line));
    }

    [Fact]
    public async Task AnOversizedFrameIsAbandonedAndTheNextOneStillArrives()
    {
        // THE FAILURE THIS PREVENTS IS NOT AN ERROR, IT IS DEATH. ReadLineAsync grows a
        // string until it finds a newline, so a peer that never sends one walks the
        // process out of memory and all anyone sees is that the host vanished. Here the
        // frame is abandoned, the connection resynchronises on the next newline, and the
        // client is told why.
        var oversized = new StringBuilder();
        oversized.Append('x', ProtocolLimits.MaxFrameChars + 10);
        oversized.Append('\n');
        oversized.Append("{\"op\":\"ping\"}\n");

        List<(string? Line, bool Overlong)> frames = await ReadAllAsync(oversized.ToString());

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Overlong, "the oversized frame must be reported as abandoned");
        Assert.Equal(string.Empty, frames[0].Line);
        Assert.False(frames[1].Overlong);
        Assert.Equal("{\"op\":\"ping\"}", frames[1].Line);
    }

    [Fact]
    public async Task AFrameExactlyAtTheLimitIsAccepted()
    {
        // The boundary itself, because an off-by-one here rejects valid traffic.
        string exact = new('x', ProtocolLimits.MaxFrameChars);

        List<(string? Line, bool Overlong)> frames = await ReadAllAsync(exact + "\n");

        Assert.Single(frames);
        Assert.False(frames[0].Overlong);
        Assert.Equal(ProtocolLimits.MaxFrameChars, frames[0].Line!.Length);
    }
}
#endif
