#if !NET471
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class HostDurableProtocolTests
{
    [Fact]
    public async Task DurableModeFramesErrorsAndClosesWithoutOpeningASearch()
    {
        using var input = new StringReader("  \nnot-json\n{\"id\":1,\"protocol\":1,\"op\":\"status\"}\n{\"id\":2,\"protocol\":1,\"op\":\"close\"}\nignored-after-close\n");
        using var output = new StringWriter();
        Assert.Equal(0, await global::Program.ServeDurableAsync(input, output));
        var frames = output.ToString().Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!).ToArray();
        Assert.Equal(3, frames.Length); Assert.False(frames[0]["ok"]!.GetValue<bool>());
        Assert.Equal(1, frames[1]["id"]!.GetValue<int>()); Assert.False(frames[1]["ok"]!.GetValue<bool>());
        Assert.True(frames[2]["closed"]!.GetValue<bool>());
    }

    [Fact]
    public async Task DurableModeReturnsNormallyAtEof()
    {
        using var input = new StringReader("\n"); using var output = new StringWriter();
        Assert.Equal(0, await global::Program.ServeDurableAsync(input, output)); Assert.Equal(string.Empty, output.ToString());
    }
}
#endif
