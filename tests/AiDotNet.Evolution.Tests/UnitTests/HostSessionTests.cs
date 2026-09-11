#if !NET471
using System.Runtime.CompilerServices;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// The wire-protocol host, exercised without a process.
/// </summary>
/// <remarks>
/// These run the host's own translation layer rather than the binary, because the defects
/// that live here are configuration mistakes -- a setting the host forgets to forward --
/// and those produce an empty result rather than an exception. A test that only checked
/// "the run completed" would pass against every one of them.
/// </remarks>
public sealed class HostSessionTests
{
    private static RunConfig Quadratic(string direction) => new()
    {
        Parameters =
        {
            new ParameterConfig { Name = "x", Min = -10, Max = 10, Step = 0.5 },
            new ParameterConfig { Name = "y", Min = -10, Max = 10, Step = 0.5 },
        },
        Descriptors =
        {
            new DescriptorConfig { Name = "x", Min = -10, Max = 10, Bins = 20 },
            new DescriptorConfig { Name = "y", Min = -10, Max = 10, Bins = 20 },
        },
        Seed = 20260910UL,
        MaxProposals = 200,
        MaxEvaluations = 200,
        BatchSize = 8,
        Direction = direction,
    };

    /// <summary>Distance from (3, -1); zero at the optimum.</summary>
    private static double Loss(IReadOnlyDictionary<string, double> parameters) =>
        Math.Pow(parameters["x"] - 3.0, 2) + Math.Pow(parameters["y"] + 1.0, 2);

    /// <summary>Fails the test rather than letting an await hang the run.</summary>
    /// <remarks>
    /// EVERY AWAIT HERE IS BOUNDED, because the failure mode of this class of bug is a
    /// HANG rather than a wrong answer: a result the session rejects leaves its evaluation
    /// unresolved, the engine stays inside the batch, and the next AskAsync waits forever.
    /// Unbounded, that surfaces as CI's watchdog killing the job with no assertion and no
    /// clue; bounded, it surfaces as this message.
    /// </remarks>
    private static async Task<T> WithTimeout<T>(Task<T> task, [CallerArgumentExpression("task")] string? what = null)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(ReferenceEquals(finished, task), $"'{what}' did not complete within 30s");
        return await task;
    }

    private static async Task<(Candidate? Best, string? StopReason, int Evaluated)> RunAsync(RunConfig config)
    {
        using var session = HostSession.Open(config);
        bool minimizing = string.Equals(config.Direction, "minimize", StringComparison.OrdinalIgnoreCase);
        int evaluated = 0;

        while (true)
        {
            List<Candidate> batch = await WithTimeout(session.AskAsync(config.BatchSize, CancellationToken.None));
            if (batch.Count == 0) break;
            evaluated += batch.Count;

            var results = new List<TellResult>(batch.Count);
            foreach (Candidate candidate in batch)
            {
                double loss = Loss(candidate.Parameters);
                results.Add(new TellResult
                {
                    EvaluationId = candidate.EvaluationId,
                    Quality = minimizing ? loss : -loss,
                    Descriptors = candidate.Parameters.ToDictionary(pair => pair.Key, pair => (double?)pair.Value, StringComparer.Ordinal),
                });
            }
            // ASSERTED, NOT IGNORED. Tell returns how many ids were actually outstanding;
            // a rejected one leaves its evaluation unresolved, and the hang that follows
            // happens somewhere else entirely. This reports it where it is caused.
            Assert.Equal(results.Count, session.Tell(results));
        }

        (Candidate? best, string? stopReason) = await WithTimeout(session.FinishAsync());
        return (best, stopReason, evaluated);
    }

    [Fact]
    public async Task MaximizingRunConvergesOnTheOptimum()
    {
        (Candidate? best, _, int evaluated) = await RunAsync(Quadratic("maximize"));

        Candidate optimum = Assert.IsType<Candidate>(best);
        Assert.True(evaluated > 8, $"only {evaluated} candidates were evaluated; the run stopped after the seed batch");
        Assert.True(Math.Abs(optimum.Parameters["x"] - 3.0) <= 1.0, $"x reached {optimum.Parameters["x"]}");
        Assert.True(Math.Abs(optimum.Parameters["y"] + 1.0) <= 1.0, $"y reached {optimum.Parameters["y"]}");
    }

    /// <summary>
    /// The regression for a minimizing run archiving nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="MapElitesArchive{TGenome}"/> defaults to Maximize and REJECTS any evaluation
    /// whose direction differs from its own. The host built the archive without passing the
    /// configured direction, so under `minimize` every insertion was rejected, no elites
    /// existed to breed from, and the run ended after the seed batch with
    /// <c>StopReason.NoCandidates</c> and a null best. Nothing threw and no diagnostic was
    /// produced -- the failure was an empty, entirely plausible-looking result.
    /// </remarks>
    [Fact]
    public async Task MinimizingRunConvergesOnTheOptimum()
    {
        (Candidate? best, string? stopReason, int evaluated) = await RunAsync(Quadratic("minimize"));

        Assert.True(evaluated > 8, $"only {evaluated} candidates were evaluated, stopping with '{stopReason}'");
        Candidate optimum = Assert.IsType<Candidate>(best);
        Assert.True(Math.Abs(optimum.Parameters["x"] - 3.0) <= 1.0, $"x reached {optimum.Parameters["x"]}");
        Assert.True(Math.Abs(optimum.Parameters["y"] + 1.0) <= 1.0, $"y reached {optimum.Parameters["y"]}");
    }

    [Theory]
    [InlineData("minimise")]
    [InlineData("min")]
    [InlineData("")]
    [InlineData("descending")]
    public void AnUnrecognisedDirectionIsRejected(string direction)
    {
        // Silently defaulting to Maximize would run the search the opposite way and still
        // return a plausible genome, which is a failure the caller cannot see in its results.
        ArgumentException error = Assert.Throws<ArgumentException>(() => HostSession.Open(Quadratic(direction)));
        Assert.Contains("maximize", error.Message, StringComparison.Ordinal);
        Assert.Contains("minimize", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheEvaluationBudgetIsSeparateFromTheProposalBudget()
    {
        // Left unset on the engine, MaxEvaluationAttempts defaults to 100 and caps a run
        // that asked for more, reporting a stop reason that names no setting the client
        // could raise. The host forwards it so a client can actually control it.
        RunConfig config = Quadratic("maximize");
        config.MaxProposals = 1000;
        config.MaxEvaluations = 48;

        (_, _, int evaluated) = await RunAsync(config);

        Assert.True(evaluated > 0, "the run produced no candidates at all");
        Assert.True(evaluated <= 48, $"evaluated {evaluated} against a budget of 48");
    }

    [Fact]
    public void AMisspelledSeedNameIsRejected()
    {
        // THE TYPO NEVER APPEARS IN THE RESULTS, which is why it has to be caught here.
        // An absent name defaults to the midpoint of its range, so a client that wrote
        // "learningRate" for "learning_rate" gets a seed somewhere it never asked for,
        // a different search, and `ok: true` saying it all went fine.
        RunConfig config = Quadratic("maximize");
        config.Seeds = new List<Dictionary<string, double>>
        {
            new(StringComparer.Ordinal) { ["x"] = 1.0, ["z"] = 2.0 },
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        Assert.Contains("'z'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASeedNamingOnlySomeParametersIsFine()
    {
        // The complement of the test above: silence about a DECLARED parameter is a
        // request for its midpoint, and rejecting that would make partial seeds unusable.
        RunConfig config = Quadratic("maximize");
        config.Seeds = new List<Dictionary<string, double>>
        {
            new(StringComparer.Ordinal) { ["x"] = 1.0 },
        };

        using HostSession session = HostSession.Open(config);
        Assert.False(session.IsComplete);
    }

    [Fact]
    public void MoreDimensionsThanTheLimitAreRefused()
    {
        // One well-formed frame declaring a million parameters is small on the wire and
        // large in the heap, and the descriptor grid is worse than linear.
        RunConfig config = Quadratic("maximize");
        config.Parameters = new List<ParameterConfig>();
        for (int i = 0; i <= ProtocolLimits.MaxDimensions; i += 1)
        {
            config.Parameters.Add(new ParameterConfig { Name = $"p{i}", Min = 0, Max = 1 });
        }

        ArgumentException error = Assert.Throws<ArgumentException>(() => HostSession.Open(config));
        Assert.Contains(ProtocolLimits.MaxDimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreSeedsThanTheLimitAreRefused()
    {
        RunConfig config = Quadratic("maximize");
        config.Seeds = new List<Dictionary<string, double>>();
        for (int i = 0; i <= ProtocolLimits.MaxSeeds; i += 1)
        {
            config.Seeds.Add(new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = 0 });
        }

        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
    }

    [Fact]
    public async Task AFailedEvaluationIsNotScoredZero()
    {
        // A failure reported as quality zero would enter the archive as a genuinely poor
        // result, so `best` would be non-null and the failure invisible.
        RunConfig config = Quadratic("maximize");
        config.MaxProposals = 40;
        config.MaxEvaluations = 40;

        using var session = HostSession.Open(config);
        while (true)
        {
            List<Candidate> batch = await WithTimeout(session.AskAsync(8, CancellationToken.None));
            if (batch.Count == 0) break;
            List<TellResult> results = batch.ConvertAll(candidate => new TellResult
            {
                EvaluationId = candidate.EvaluationId,
                Reason = "the build did not compile",
            });
            Assert.Equal(results.Count, session.Tell(results));
        }

        (Candidate? best, _) = await WithTimeout(session.FinishAsync());
        Assert.Null(best);
    }
}
#endif
