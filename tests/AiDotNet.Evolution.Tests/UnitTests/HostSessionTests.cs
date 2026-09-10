#if !NET471
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

    private static async Task<(Candidate? Best, string? StopReason, int Evaluated)> RunAsync(RunConfig config)
    {
        using var session = HostSession.Open(config);
        bool minimizing = string.Equals(config.Direction, "minimize", StringComparison.OrdinalIgnoreCase);
        int evaluated = 0;

        while (true)
        {
            List<Candidate> batch = await session.AskAsync(config.BatchSize, CancellationToken.None);
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
                    Descriptors = new Dictionary<string, double>(candidate.Parameters, StringComparer.Ordinal),
                });
            }
            session.Tell(results);
        }

        (Candidate? best, string? stopReason) = await session.FinishAsync();
        return (best, stopReason, evaluated);
    }

    [Fact]
    public async Task MaximizingRunConvergesOnTheOptimum()
    {
        (Candidate? best, _, int evaluated) = await RunAsync(Quadratic("maximize"));

        Assert.NotNull(best);
        Assert.True(evaluated > 8, $"only {evaluated} candidates were evaluated; the run stopped after the seed batch");
        Assert.True(Math.Abs(best!.Parameters["x"] - 3.0) <= 1.0, $"x reached {best.Parameters["x"]}");
        Assert.True(Math.Abs(best.Parameters["y"] + 1.0) <= 1.0, $"y reached {best.Parameters["y"]}");
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
        Assert.NotNull(best);
        Assert.True(Math.Abs(best!.Parameters["x"] - 3.0) <= 1.0, $"x reached {best.Parameters["x"]}");
        Assert.True(Math.Abs(best.Parameters["y"] + 1.0) <= 1.0, $"y reached {best.Parameters["y"]}");
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
            List<Candidate> batch = await session.AskAsync(8, CancellationToken.None);
            if (batch.Count == 0) break;
            session.Tell(batch.ConvertAll(candidate => new TellResult
            {
                EvaluationId = candidate.EvaluationId,
                Reason = "the build did not compile",
            }));
        }

        (Candidate? best, _) = await session.FinishAsync();
        Assert.Null(best);
    }
}
#endif
