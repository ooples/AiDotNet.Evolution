#if !NET471
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The op dispatch: every answer a client can get without a run behaving badly.
/// </summary>
/// <remarks>
/// These are all ERROR PATHS, which is the point. A client that asks in the wrong order,
/// sends a request without its payload, or speaks a newer protocol than this host gets a
/// reply it can act on rather than a closed pipe -- and none of that is exercised by the
/// happy-path tests, because the happy path never takes these branches.
/// </remarks>
public sealed class HostDispatchTests
{
    private static Request Ask(string op) => new() { Op = op, Id = 1 };

    private static RunConfig MinimalConfig() => new()
    {
        Parameters = { new ParameterConfig { Name = "x", Min = 0, Max = 1 } },
        Descriptors = { new DescriptorConfig { Name = "x", Min = 0, Max = 1, Bins = 4 } },
        MaxProposals = 4,
        MaxEvaluations = 4,
        BatchSize = 2,
    };

    private static Task<Response> Dispatch(Request request, HostSession? session = null) =>
        Program.Handle(request, session, _ => { });

    [Fact]
    public async Task PingAnswersWithTheVersion()
    {
        Response response = await Dispatch(Ask("ping"));

        Assert.True(response.Ok);
        Assert.False(string.IsNullOrWhiteSpace(response.Version));
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("Ask")]
    [InlineData("retrieve")]
    public async Task AnUnknownOpIsRefusedAndQuoted(string op)
    {
        // Quoted, because a client speaking a newer protocol needs to know WHICH op this
        // host did not recognise -- "unknown op" alone sends them reading the wrong docs.
        Response response = await Dispatch(Ask(op));

        Assert.False(response.Ok);
        Assert.Contains($"'{op}'", response.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("tell")]
    [InlineData("status")]
    public async Task EveryOpThatNeedsARunSaysSoWhenThereIsNone(string op)
    {
        Response response = await Dispatch(Ask(op));

        Assert.False(response.Ok);
        Assert.Contains("no run is open", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenWithoutAConfigIsRefused()
    {
        Response response = await Dispatch(new Request { Op = "open", Id = 2 });

        Assert.False(response.Ok);
        Assert.Contains("config", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpeningTwiceOnOneProcessIsRefused()
    {
        // One run per process is the contract; silently replacing the first would strand
        // whatever the caller was still holding evaluation ids for.
        using var existing = HostSession.Open(MinimalConfig());

        Response response = await Dispatch(
            new Request { Op = "open", Id = 3, Config = MinimalConfig() },
            existing);

        Assert.False(response.Ok);
        Assert.Contains("already open", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReportsTheVersionAndHandsBackTheSession()
    {
        HostSession? captured = null;
        Response response = await Program.Handle(
            new Request { Op = "open", Id = 4, Config = MinimalConfig() },
            null,
            s => captured = s);

        Assert.True(response.Ok);

        // THE NAME OF THIS TEST PROMISED A CHECK IT DID NOT MAKE. Without it the open
        // response could stop reporting a version entirely and nothing here would
        // notice, which matters because a client reads this field to decide whether it
        // understands the host at all.
        //
        // The literal is deliberate. This is a wire contract, not an implementation
        // detail, so changing the version should require deliberately editing the test
        // that pins it -- asserting merely "not empty" would let a rename through.
        Assert.Equal("0.1.0", response.Version);

        Assert.NotNull(captured);
        captured!.Dispose();
    }

    [Fact]
    public async Task AConfigTheSessionRejectsBecomesAnErrorResponse()
    {
        // DELIBERATELY GENERAL upstream: this is a protocol boundary, so a throw from
        // deeper in the stack has to reach the client as a readable error rather than
        // killing the process and leaving a closed pipe.
        RunConfig broken = MinimalConfig();
        broken.Direction = "sideways";

        Response response = await Dispatch(new Request { Op = "open", Id = 5, Config = broken });

        Assert.False(response.Ok);
        Assert.Contains("maximize", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TellWithoutResultsIsRefused()
    {
        using var session = HostSession.Open(MinimalConfig());

        Response response = await Dispatch(new Request { Op = "tell", Id = 6 }, session);

        Assert.False(response.Ok);
        Assert.Contains("results", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TellBeyondTheResultLimitIsRefused()
    {
        using var session = HostSession.Open(MinimalConfig());
        var request = new Request { Op = "tell", Id = 7, Results = new List<TellResult>() };
        for (int i = 0; i <= ProtocolLimits.MaxResults; i += 1)
            request.Results.Add(new TellResult { EvaluationId = i, Quality = 0 });

        Response response = await Dispatch(request, session);

        Assert.False(response.Ok);
        Assert.Contains(
            ProtocolLimits.MaxResults.ToString(System.Globalization.CultureInfo.InvariantCulture),
            response.Error,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsCompletion()
    {
        using var session = HostSession.Open(MinimalConfig());

        Response response = await Dispatch(new Request { Op = "status", Id = 8 }, session);

        Assert.True(response.Ok);
        Assert.NotNull(response.Complete);
    }

    [Fact]
    public async Task ClosingWithNoRunOpenSucceedsQuietly()
    {
        // Idempotent by design: a client tearing down after a failed open must not be
        // told its cleanup was an error.
        Response response = await Dispatch(new Request { Op = "close", Id = 9 });

        Assert.True(response.Ok);
        Assert.Null(response.Best);
    }

    [Fact]
    public async Task AskReturnsCandidatesAndCloseReturnsTheBest()
    {
        HostSession? session = HostSession.Open(MinimalConfig());
        try
        {
            Response asked = await Dispatch(new Request { Op = "ask", Id = 10, Max = 2 }, session);
            Assert.True(asked.Ok);
            Assert.NotNull(asked.Candidates);
            Assert.NotEmpty(asked.Candidates!);
            Assert.False(asked.Complete);

            var told = new Request { Op = "tell", Id = 11, Results = new List<TellResult>() };
            foreach (Candidate candidate in asked.Candidates!)
            {
                told.Results!.Add(new TellResult
                {
                    EvaluationId = candidate.EvaluationId,
                    Quality = 1.0,
                    Descriptors = new Dictionary<string, double>(candidate.Parameters, StringComparer.Ordinal),
                });
            }
            Response accepted = await Dispatch(told, session);
            Assert.True(accepted.Ok);
            Assert.Equal(asked.Candidates!.Count, accepted.Accepted);

            HostSession? cleared = session;
            Response closed = await Program.Handle(
                new Request { Op = "close", Id = 12 },
                session,
                s => cleared = s);

            Assert.True(closed.Ok);
            Assert.Null(cleared);
            session = null;
        }
        finally
        {
            session?.Dispose();
        }
    }

    [Fact]
    public async Task AskWithANonPositiveMaxStillReturnsWork()
    {
        // `max: 0` is a client bug, not a request for nothing; answering with an empty
        // batch would read as "the run is over" and end the loop.
        using var session = HostSession.Open(MinimalConfig());

        Response response = await Dispatch(new Request { Op = "ask", Id = 13, Max = 0 }, session);

        Assert.True(response.Ok);
        Assert.NotEmpty(response.Candidates!);
    }
}
#endif
