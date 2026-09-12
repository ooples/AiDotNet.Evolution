#if !NET471
using System.Globalization;
using System.Text.Json;
using AiDotNet.Evolution.Host;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class HostReviewBoundaryTests
{
    public enum Budget { Proposals, Evaluations, Generations, BatchSize }
    public enum DescriptorInput { MissingMap, MissingKey, Null, NaN, PositiveInfinity, NegativeInfinity, Zero }
    public enum Collection { Parameters, Descriptors, Seeds, Results, SeedMap, DescriptorMap }

    [Theory]
    [InlineData(Budget.Proposals)]
    [InlineData(Budget.Evaluations)]
    [InlineData(Budget.Generations)]
    [InlineData(Budget.BatchSize)]
    public void NegativeBudgetsAreRejectedInsteadOfRunningOneUnit(Budget budget)
    {
        RunConfig config = Config();
        SetBudget(config, budget, -1);
        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
    }

    [Fact]
    public async Task ZeroEvaluationBudgetDoesNotEvaluateCandidates()
    {
        RunConfig config = Config();
        config.MaxEvaluations = 0;
        using var session = HostSession.Open(config);
        Assert.Empty(await session.AskAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        (Candidate? best, string? stopReason) = await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(best);
        Assert.Equal("EvaluationBudgetReached", stopReason);
    }

    [Fact]
    public async Task ZeroGenerationBudgetEvaluatesAllSeedsWithoutVariation()
    {
        RunConfig config = Config();
        config.MaxGenerations = 0;
        config.Seeds = new() { new() { ["x"] = 0 }, new() { ["x"] = 1 } };
        using var session = HostSession.Open(config);
        foreach (double expected in new[] { 0.0, 1.0 })
        {
            Candidate candidate = Assert.Single(await session.AskAsync(1, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(expected, candidate.Parameters["x"]);
            Assert.Equal(1, session.Tell(new[]
            {
                new TellResult
                {
                    EvaluationId = candidate.EvaluationId, Quality = expected,
                    Descriptors = new() { ["d"] = 0 },
                },
            }));
        }
        Assert.Empty(await session.AskAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        (Candidate? best, string? stopReason) = await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, Assert.IsType<Candidate>(best).Quality);
        Assert.Equal("GenerationLimitReached", stopReason);
    }

    [Theory]
    [InlineData(Budget.Proposals)]
    [InlineData(Budget.BatchSize)]
    public void ZeroCapacityIsRejectedBeforeStartingTheEngine(Budget budget)
    {
        RunConfig config = Config();
        SetBudget(config, budget, 0);
        Assert.Throws<ArgumentException>(() => HostSession.Open(config));
    }

    [Theory]
    [InlineData(EvolutionStopReason.EvaluationBudgetReached, "EvaluationBudgetReached")]
    [InlineData(EvolutionStopReason.ProposalBudgetReached, "ProposalBudgetReached")]
    [InlineData(EvolutionStopReason.NoCandidates, "NoCandidates")]
    [InlineData(EvolutionStopReason.Canceled, "Canceled")]
    [InlineData(EvolutionStopReason.TimeLimitReached, "TimeLimitReached")]
    [InlineData(EvolutionStopReason.CandidateFailure, "CandidateFailure")]
    [InlineData(EvolutionStopReason.GenerationLimitReached, "GenerationLimitReached")]
    [InlineData(EvolutionStopReason.TargetReached, "TargetReached")]
    [InlineData(EvolutionStopReason.EarlyStopped, "EarlyStopped")]
    public void StopReasonsHaveExplicitStableWireTokens(EvolutionStopReason reason, string expected)
        => Assert.Equal(expected, Protocol.StopReasonToWire(reason));

    [Fact]
    public void UnknownStopReasonsCannotInventWireTokens()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Protocol.StopReasonToWire((EvolutionStopReason)int.MaxValue));

    [Fact]
    public void EveryDefinedStopReasonHasAWireMapping()
    {
        foreach (EvolutionStopReason reason in Enum.GetValues<EvolutionStopReason>())
            Assert.NotEmpty(Protocol.StopReasonToWire(reason));
    }

    [Theory]
    [InlineData("{\"op\":\"open\",\"config\":{\"parameters\":{}},\"id\":73}")]
    [InlineData("{\"op\":\"open\",\"config\":{\"parameters\":null},\"id\":73}")]
    [InlineData("{\"op\":\"open\",\"config\":{\"descriptors\":null},\"id\":73}")]
    [InlineData("{\"op\":\"open\",\"config\":{\"parameters\":[null]},\"id\":73}")]
    [InlineData("{\"op\":\"open\",\"config\":{\"seeds\":[[]]},\"id\":73}")]
    [InlineData("{\"op\":\"open\",\"config\":{\"seeds\":[null]},\"id\":73}")]
    [InlineData("{\"op\":\"open\",\"config\":{\"seeds\":[{\"x\":null}]},\"id\":73}")]
    [InlineData("{\"op\":\"tell\",\"results\":[null],\"id\":73}")]
    [InlineData("{\"op\":\"tell\",\"results\":[{\"descriptors\":[]}],\"id\":73}")]
    public void InvalidCollectionEntriesRemainCorrelatedProtocolErrors(string frame)
    {
        Assert.Null(Protocol.ParseRequest(frame, out string? error, out long id));
        Assert.Equal(73, id);
        Assert.StartsWith("invalid request:", error);
    }

    [Fact]
    public void BoundedConvertersPreserveSourceGeneratedRoundTrips()
    {
        var original = new Request
        {
            Op = "open",
            Id = 73,
            Config = Config(),
            Results = new() { new() { EvaluationId = 1, Descriptors = new() { ["d"] = 0.5, ["missing"] = null } } },
        };
        original.Config.Seeds = new() { new() { ["x"] = 0.25 } };
        string frame = JsonSerializer.Serialize(original, HostJsonContext.Default.Request);
        Request parsed = Assert.IsType<Request>(Protocol.ParseRequest(frame, out string? error, out long id));
        Assert.Null(error);
        Assert.Equal(73, id);
        RunConfig config = Assert.IsType<RunConfig>(parsed.Config);
        Assert.Equal("x", Assert.Single(config.Parameters).Name);
        Assert.Equal("d", Assert.Single(config.Descriptors).Name);
        Assert.Equal(0.25, Assert.Single(Assert.IsType<List<Dictionary<string, double>>>(config.Seeds))["x"]);
        TellResult result = Assert.Single(Assert.IsType<List<TellResult>>(parsed.Results));
        Dictionary<string, double?> measurements = Assert.IsType<Dictionary<string, double?>>(result.Descriptors);
        Assert.Equal(0.5, measurements["d"]);
        Assert.Null(measurements["missing"]);
    }

    [Theory]
    [InlineData(DescriptorInput.MissingMap)]
    [InlineData(DescriptorInput.MissingKey)]
    [InlineData(DescriptorInput.Null)]
    [InlineData(DescriptorInput.NaN)]
    [InlineData(DescriptorInput.PositiveInfinity)]
    [InlineData(DescriptorInput.NegativeInfinity)]
    [InlineData(DescriptorInput.Zero)]
    public async Task InvalidDescriptorsCannotCreateAnEliteButRealZeroCan(DescriptorInput input)
    {
        RunConfig config = Config();
        config.MaxProposals = 1;
        config.MaxEvaluations = 1;
        using var session = HostSession.Open(config);
        Candidate candidate = Assert.Single(await session.AskAsync(1, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)));
        Dictionary<string, double?>? descriptors = input switch
        {
            DescriptorInput.MissingMap => null,
            DescriptorInput.MissingKey => new() { ["other"] = 0 },
            DescriptorInput.Null => new() { ["d"] = null },
            DescriptorInput.NaN => new() { ["d"] = double.NaN },
            DescriptorInput.PositiveInfinity => new() { ["d"] = double.PositiveInfinity },
            DescriptorInput.NegativeInfinity => new() { ["d"] = double.NegativeInfinity },
            DescriptorInput.Zero => new() { ["d"] = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };
        Assert.Equal(1, session.Tell(new[]
        {
            new TellResult { EvaluationId = candidate.EvaluationId, Quality = 123, Descriptors = descriptors },
        }));
        Assert.Empty(await session.AskAsync(1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        (Candidate? best, _) = await session.FinishAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (input == DescriptorInput.Zero) Assert.Equal(123, Assert.IsType<Candidate>(best).Quality);
        else Assert.Null(best);
    }

    [Fact]
    public void ParameterLayoutDoesNotFollowCallerListMutations()
    {
        var definitions = new List<ParameterDefinition>
        {
            new("x", 0, 1, 0.25, false),
            new("y", 0, 1, 0.25, false),
        };
        var space = new ParameterSpace(definitions);
        definitions[0] = new ParameterDefinition("z", -100, 100, 1, true);
        definitions.Reverse();
        definitions.RemoveAt(1);

        Assert.Equal(new[] { "x", "y" }, space.Parameters.Select(parameter => parameter.Name));
        ParameterGenome genome = space.Create(new Dictionary<string, double> { ["x"] = 0.25, ["y"] = 0.75 });
        Assert.Equal(0.25, space.ToMap(genome)["x"]);
        Assert.Equal(0.75, space.ToMap(genome)["y"]);
        Assert.Throws<ArgumentException>(() => space.Create(new Dictionary<string, double> { ["z"] = 0 }));
    }

    [Theory]
    [InlineData(Collection.Parameters)]
    [InlineData(Collection.Descriptors)]
    [InlineData(Collection.Seeds)]
    [InlineData(Collection.Results)]
    [InlineData(Collection.SeedMap)]
    [InlineData(Collection.DescriptorMap)]
    public void CollectionsAreLimitedDuringParsingAndLateIdsSurvive(Collection collection)
    {
        int limit = Limit(collection);
        Request? request = Protocol.ParseRequest(Frame(collection, limit + 1), out string? error, out long id);
        Assert.Null(request);
        Assert.Equal(73, id);
        Assert.Contains("limit", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(limit.ToString(CultureInfo.InvariantCulture), error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Collection.Parameters)]
    [InlineData(Collection.Descriptors)]
    [InlineData(Collection.Seeds)]
    [InlineData(Collection.Results)]
    [InlineData(Collection.SeedMap)]
    [InlineData(Collection.DescriptorMap)]
    public void CollectionsAtTheirExactLimitStillParse(Collection collection)
    {
        Assert.IsType<Request>(Protocol.ParseRequest(Frame(collection, Limit(collection)), out string? error, out long id));
        Assert.Null(error);
        Assert.Equal(73, id);
    }

    [Fact]
    public void RepeatedMapKeysCannotBypassTheEntryLimit()
    {
        string entries = string.Join(',', Enumerable.Repeat("\"x\":0", ProtocolLimits.MaxDimensions + 1));
        Assert.Null(Protocol.ParseRequest("{\"op\":\"open\",\"config\":{\"seeds\":[{" + entries
            + "}]},\"id\":73}", out string? error, out long id));
        Assert.Equal(73, id);
        Assert.Contains("limit", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RejectingALargeCollectionDoesNotMaterializeItsWholeObjectGraph()
    {
        // Warm serializer metadata before measuring. The rejected body contains 100,000
        // DTOs, but only the permitted prefix and bounded UTF-8 buffers may be allocated.
        Protocol.ParseRequest(Frame(Collection.Parameters, ProtocolLimits.MaxDimensions + 1), out _, out _);
        string frame = Frame(Collection.Parameters, 100_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Request? request = Protocol.ParseRequest(frame, out string? error, out long id);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Null(request);
        Assert.Equal(73, id);
        Assert.Contains("limit", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(allocated < 2_000_000 + frame.Length * 4L,
            $"Allocated {allocated:N0} bytes for a {frame.Length:N0}-character rejected frame.");
    }

    private static RunConfig Config() => new()
    {
        Parameters = { new ParameterConfig { Name = "x", Min = 0, Max = 1, Step = 0.25 } },
        Descriptors = { new DescriptorConfig { Name = "d", Min = -1, Max = 1, Bins = 4 } },
        MaxProposals = 8,
        MaxEvaluations = 8,
        MaxGenerations = 8,
        BatchSize = 1,
    };

    private static void SetBudget(RunConfig config, Budget budget, int value)
    {
        switch (budget)
        {
            case Budget.Proposals: config.MaxProposals = value; break;
            case Budget.Evaluations: config.MaxEvaluations = value; break;
            case Budget.Generations: config.MaxGenerations = value; break;
            case Budget.BatchSize: config.BatchSize = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(budget));
        }
    }

    private static int Limit(Collection collection) => collection switch
    {
        Collection.Seeds => ProtocolLimits.MaxSeeds,
        Collection.Results => ProtocolLimits.MaxResults,
        _ => ProtocolLimits.MaxDimensions,
    };

    private static string Frame(Collection collection, int count)
    {
        string objects = string.Join(',', Enumerable.Repeat("{}", count));
        string entries = string.Join(',', Enumerable.Range(0, count).Select(i => $"\"p{i}\":0"));
        string content = collection switch
        {
            Collection.Parameters => "\"config\":{\"parameters\":[" + objects + "]}",
            Collection.Descriptors => "\"config\":{\"descriptors\":[" + objects + "]}",
            Collection.Seeds => "\"config\":{\"seeds\":[" + objects + "]}",
            Collection.Results => "\"results\":[" + objects + "]",
            Collection.SeedMap => "\"config\":{\"seeds\":[{" + entries + "}]}",
            Collection.DescriptorMap => "\"results\":[{\"descriptors\":{" + entries + "}}]",
            _ => throw new ArgumentOutOfRangeException(nameof(collection)),
        };
        return "{\"op\":\"ping\"," + content + ",\"id\":73}";
    }
}
#endif
