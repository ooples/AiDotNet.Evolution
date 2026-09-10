#if !NET471
using System.Text;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The framing loop: what a client gets back for each line it sends.
/// </summary>
/// <remarks>
/// Driven through <see cref="Program.ServeAsync"/> with string streams rather than by
/// spawning the binary. Every case below is a MALFORMED or hostile input -- the paths a
/// well-behaved client never takes and a broken one always does, and the ones where the
/// difference between a usable error and a closed pipe is decided.
/// </remarks>
public sealed class HostServeLoopTests
{
    /// <summary>Feeds the loop a script and returns one parsed response per line out.</summary>
    private static async Task<List<Response>> ServeAsync(params string[] lines)
    {
        var input = new StringReader(string.Join("\n", lines) + "\n");
        var output = new StringWriter();

        int code = await Program.ServeAsync(input, output);
        Assert.Equal(0, code);

        var responses = new List<Response>();
        foreach (string line in output.ToString().Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            responses.Add(System.Text.Json.JsonSerializer.Deserialize<Response>(line)!);
        }
        return responses;
    }

    [Fact]
    public async Task PingIsAnsweredAndTheIdComesBack()
    {
        List<Response> out_ = await ServeAsync("{\"op\":\"ping\",\"id\":11}");

        Assert.Single(out_);
        Assert.True(out_[0].Ok);
        Assert.Equal(11, out_[0].Id);
    }

    [Fact]
    public async Task BlankLinesAreIgnoredRatherThanAnswered()
    {
        // A client that writes a trailing newline, or a heartbeat, must not receive an
        // error for it -- and must not have its next real request thrown off.
        List<Response> out_ = await ServeAsync("", "   ", "{\"op\":\"ping\",\"id\":1}", "");

        Assert.Single(out_);
        Assert.Equal(1, out_[0].Id);
    }

    [Fact]
    public async Task MalformedJsonIsAnsweredWithoutKillingTheLoop()
    {
        List<Response> out_ = await ServeAsync("{\"op\":", "{\"op\":\"ping\",\"id\":2}");

        Assert.Equal(2, out_.Count);
        Assert.False(out_[0].Ok);
        Assert.Contains("malformed JSON", out_[0].Error, StringComparison.Ordinal);
        Assert.True(out_[1].Ok);
        Assert.Equal(2, out_[1].Id);
    }

    [Fact]
    public async Task AParseableRequestWithNoOpEchoesItsId()
    {
        // The whole reason the parse and the validation are separate: this frame is good
        // JSON with a bad op, and answering it with id 0 leaves the client unable to
        // match the error to the request.
        List<Response> out_ = await ServeAsync("{\"id\":42,\"op\":\"\"}");

        Assert.Single(out_);
        Assert.False(out_[0].Ok);
        Assert.Equal(42, out_[0].Id);
    }

    [Fact]
    public async Task AnOversizedFrameIsRefusedAndTheNextOneIsStillServed()
    {
        // The frame is abandoned rather than assembled, so there is no id to echo; what
        // matters is that the connection resynchronises on the next newline instead of
        // the process dying with the client seeing only a closed pipe.
        var oversized = new StringBuilder();
        oversized.Append('x', ProtocolLimits.MaxFrameChars + 5);

        List<Response> out_ = await ServeAsync(oversized.ToString(), "{\"op\":\"ping\",\"id\":3}");

        Assert.Equal(2, out_.Count);
        Assert.False(out_[0].Ok);
        Assert.Contains("frame exceeds", out_[0].Error, StringComparison.Ordinal);
        Assert.True(out_[1].Ok);
        Assert.Equal(3, out_[1].Id);
    }

    [Fact]
    public async Task CloseEndsTheLoopAndAnythingAfterItIsNotRead()
    {
        // `close` is the client saying it is done. Continuing to serve after it would
        // mean answering a peer that has already stopped listening.
        List<Response> out_ = await ServeAsync(
            "{\"op\":\"close\",\"id\":4}",
            "{\"op\":\"ping\",\"id\":5}");

        Assert.Single(out_);
        Assert.Equal(4, out_[0].Id);
        Assert.True(out_[0].Ok);
    }

    [Fact]
    public async Task EndOfInputEndsTheLoopCleanly()
    {
        // A client that exits without saying close -- killed, crashed -- must not make
        // this hang or fail. The exit code is asserted inside the helper.
        List<Response> out_ = await ServeAsync("{\"op\":\"ping\",\"id\":6}");

        Assert.Single(out_);
    }

    [Fact]
    public async Task AWholeRunCanBeDrivenThroughTheLoop()
    {
        // The happy path end to end, in process: open, ask, tell, close. This is the
        // only test that exercises the loop's session hand-off, which is what lets one
        // request's `open` be visible to the next request's `ask`.
        string config = "{\"parameters\":[{\"name\":\"x\",\"min\":0,\"max\":1}],"
            + "\"descriptors\":[{\"name\":\"x\",\"min\":0,\"max\":1,\"bins\":4}],"
            + "\"maxProposals\":4,\"maxEvaluations\":4,\"batchSize\":2}";

        var input = new StringReader(
            "{\"op\":\"open\",\"id\":1,\"config\":" + config + "}\n"
            + "{\"op\":\"ask\",\"id\":2,\"max\":2}\n");
        var output = new StringWriter();
        Assert.Equal(0, await Program.ServeAsync(input, output));

        string[] lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);

        Response opened = System.Text.Json.JsonSerializer.Deserialize<Response>(lines[0])!;
        Response asked = System.Text.Json.JsonSerializer.Deserialize<Response>(lines[1])!;

        Assert.True(opened.Ok);
        Assert.True(asked.Ok);
        Assert.NotNull(asked.Candidates);
        Assert.NotEmpty(asked.Candidates!);
    }
}
#endif
