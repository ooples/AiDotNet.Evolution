#if !NET471
using System.Text.Json;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class HostRequestCorrelationTests
{
    [Theory]
    [InlineData("{\"id\":42,\"op\":\"ask\",\"max\":\"four\"}", 42)]
    [InlineData("{\"max\":\"four\",\"op\":\"ask\",\"id\":43}", 43)]
    [InlineData("{\"id\":44,\"op\":7}", 44)]
    [InlineData("{\"id\":45,\"op\":\"tell\",\"results\":{}}", 45)]
    [InlineData("{\"id\":46,\"op\":\"open\",\"config\":{\"batchSize\":\"many\"}}", 46)]
    [InlineData("{\"id\":47,\"op\":\"ask\",\"max\":2147483648}", 47)]
    [InlineData("{\"id\":48,\"op\":\"open\",\"config\":{\"seed\":-1}}", 48)]
    public void WellFormedFieldTypeErrorsPreserveTheCorrelationId(string frame, long expectedId)
    {
        Request? request = Protocol.ParseRequest(frame, out string? error, out long id);

        Assert.Null(request);
        Assert.Equal(expectedId, id);
        Assert.StartsWith("invalid request:", error);
    }

    [Theory]
    [InlineData("{\"id\":42,\"max\":\"four\",")]
    [InlineData("{\"id\":42,")]
    public void MalformedDocumentsDoNotRecoverIdsFromPartialInput(string frame)
    {
        Assert.Null(Protocol.ParseRequest(frame, out string? error, out long id));
        Assert.Equal(0, id);
        Assert.StartsWith("malformed JSON:", error);
    }

    [Theory]
    [InlineData("{\"id\":\"42\",\"op\":\"ping\"}")]
    [InlineData("{\"id\":9223372036854775808,\"op\":\"ping\"}")]
    public void AnInvalidIdIsNotCoerced(string frame)
    {
        Assert.Null(Protocol.ParseRequest(frame, out string? error, out long id));
        Assert.Equal(0, id);
        Assert.StartsWith("invalid request:", error);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("17")]
    [InlineData("true")]
    public void NonObjectJsonIsNotReportedAsMalformedJson(string frame)
    {
        Assert.Null(Protocol.ParseRequest(frame, out string? error, out long id));
        Assert.Equal(0, id);
        Assert.Equal("a request must be a JSON object", error);
    }

    [Fact]
    public async Task TheServeLoopCorrelatesTheErrorAndContinuesWithTheNextRequest()
    {
        using var input = new StringReader("{\"id\":42,\"op\":\"ask\",\"max\":\"four\"}\n{\"id\":43,\"op\":\"ping\"}\n");
        using var output = new StringWriter();

        Assert.Equal(0, await Program.ServeAsync(input, output).WaitAsync(TimeSpan.FromSeconds(5)));
        Response[] responses = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Assert.IsType<Response>(JsonSerializer.Deserialize(line, HostJsonContext.Default.Response)))
            .ToArray();

        Assert.Equal(2, responses.Length);
        Response rejected = Assert.Single(responses, response => response.Id == 42);
        Assert.False(rejected.Ok);
        Assert.StartsWith("invalid request:", rejected.Error);
        Assert.True(Assert.Single(responses, response => response.Id == 43).Ok);
    }
}
#endif
