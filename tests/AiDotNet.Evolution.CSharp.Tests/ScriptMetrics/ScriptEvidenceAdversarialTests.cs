using AiDotNet.Evolution.CSharp.Tests.Execution;
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Metrics;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ScriptMetrics;

public sealed class ScriptEvidenceAdversarialTests
{
    private static readonly ProgramGenome Candidate = new("source", ProgramLanguage.Python);
    private static readonly EvolutionEvaluationContext Context = new(0, 1, 1, 1);
    private static ProgramExecuteResponse Output(string text) => new()
    {
        Success = true,
        Language = ProgramLanguage.Python,
        ExitCode = 0,
        StdOut = text
    };
    private static ScriptProgramFitnessEvaluator Evaluator(string json, ScriptProgramEvaluationOptions? options = null,
        ProgramMetricAggregator? aggregator = null) => new(new ScriptedProgramExecutionEngine(_ => Output(json)),
            "# evaluate", options, metricAggregator: aggregator);

    [Theory]
    [InlineData("{\"quality\":true,\"speed\":5}")]
    [InlineData("{\"quality\":null,\"speed\":5}")]
    [InlineData("{\"quality\":\"bad\",\"speed\":5}")]
    [InlineData("{\"quality\":1e999,\"speed\":5}")]
    [InlineData("{\"quality\":0,\"quality\":1}")]
    [InlineData("{\"combined_score\":true,\"speed\":5}")]
    [InlineData("{\"notes\":\"not a measurement\"}")]
    [InlineData("{\"quality\":1,\"metrics\":[]}")]
    [InlineData("{\"quality\":1,\"descriptors\":{\"a\":1,\" a\":2}}")]
    [InlineData("{\"quality\":1,\"metrics\":{\"a\":1e999}}")]
    public async Task MalformedEvidenceCannotBecomeAValidFallbackScore(string json)
    {
        var result = await Evaluator(json, aggregator: new()).EvaluateAsync(Candidate, Context);
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Null(result.Quality);
        Assert.Equal(1, result.CostUnits);
    }

    [Fact]
    public async Task MetricFallbackUsesItsOwnDirectionAndRetainsMeasurements()
    {
        var aggregate = new ProgramMetricAggregator(new()
        {
            Strategy = ProgramMetricAggregationStrategy.Tchebycheff,
            Weights = new Dictionary<string, double> { ["speed"] = 1 },
            ReferencePoint = new Dictionary<string, double> { ["speed"] = 10 }
        });
        var result = await Evaluator("{\"metrics\":{\"speed\":8}}", aggregator: aggregate).EvaluateAsync(Candidate, Context);
        Assert.Equal(2, result.Quality);
        Assert.Equal(EvolutionOptimizationDirection.Minimize, result.Direction);
        Assert.Equal(8, result.Metrics["speed"]);
    }

    [Fact]
    public async Task MissingWeightedMetricFailsWithAReceiptInsteadOfThrowing()
    {
        var aggregate = new ProgramMetricAggregator(new()
        {
            Strategy = ProgramMetricAggregationStrategy.Weighted,
            Weights = new Dictionary<string, double> { ["required"] = 1 }
        });
        var result = await Evaluator("{\"other\":5}", aggregator: aggregate).EvaluateAsync(Candidate, Context);
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Equal(1, result.CostUnits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationKeepsDispatchedCallAccounting(bool dispatched)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new ScriptedProgramExecutionEngine(_ => throw new OperationCanceledException());
        var evaluator = new ScriptProgramFitnessEvaluator(runner, "# evaluate");
        if (!dispatched) cancellation.Cancel();
        var result = await evaluator.EvaluateAsync(Candidate, Context, cancellation.Token);
        Assert.Equal(EvolutionEvaluationStatus.Canceled, result.Status);
        Assert.Equal(dispatched ? 1 : 0, result.CostUnits);
        Assert.Equal(dispatched ? 1 : 0, runner.Calls);
    }

    [Fact]
    public async Task FatalRuntimeFailureEscapesForUnknownConsumptionAccounting()
    {
        var evaluator = new ScriptProgramFitnessEvaluator(new ScriptedProgramExecutionEngine(_ => throw new OutOfMemoryException()), "# evaluate");
        await Assert.ThrowsAsync<OutOfMemoryException>(() => evaluator.EvaluateAsync(Candidate, Context).AsTask());
    }

    [Fact]
    public async Task ParserEnforcesResponseDepthAndCollectionBounds()
    {
        var cases = new[]
        {
            Evaluator("{\"quality\":1}", new() { MaxResponseChars = 3 }),
            Evaluator("{\"quality\":1,\"nested\":{\"a\":{\"b\":1}}}", new() { MaxJsonDepth = 2 }),
            Evaluator("{\"quality\":1,\"objectives\":[1,2,3]}", new() { MaxMetricCount = 2 })
        };
        foreach (var evaluator in cases)
            Assert.Equal(EvolutionEvaluationStatus.Failed, (await evaluator.EvaluateAsync(Candidate, Context)).Status);
    }

    [Fact]
    public async Task MalformedJsonAndArtifactsCannotExportPrivatePayloads()
    {
        const string secret = "PRIVATE_SENTINEL";
        var malformed = await Evaluator("{\"quality\":0,\"" + secret + "\":}").EvaluateAsync(Candidate, Context);
        var artifact = await Evaluator("{\"quality\":1,\"artifacts\":{\"" + secret + "\":\"" + secret + "\"}}").EvaluateAsync(Candidate, Context);
        Assert.DoesNotContain(secret, string.Join(" ", malformed.Diagnostics.Concat(artifact.Diagnostics).Select(d => d.Message)));
    }

    [Fact]
    public void ExactScriptsBoundsAndUnambiguousMetricConfigurationChangeIdentity()
    {
        var runner = new ScriptedProgramExecutionEngine(_ => Output("{}"));
        var first = new ScriptProgramFitnessEvaluator(runner, "# evaluate\n");
        var newline = new ScriptProgramFitnessEvaluator(runner, "# evaluate\r\n");
        var bounded = new ScriptProgramFitnessEvaluator(runner, "# evaluate\n", new() { MaxResponseChars = 32 });
        Assert.NotEqual(first.VersionHash, newline.VersionHash);
        Assert.NotEqual(first.VersionHash, bounded.VersionHash);
        var oneName = new ProgramMetricAggregationOptions { ExcludedFeatureDimensions = new[] { "a,b" } };
        var twoNames = new ProgramMetricAggregationOptions { ExcludedFeatureDimensions = new[] { "a", "b" } };
        Assert.NotEqual(oneName.ToString(), twoNames.ToString());
        Assert.Throws<ArgumentException>(() => new ProgramMetricAggregator().Aggregate(new Dictionary<string, double> { ["a"] = 1, [" a"] = 2 }));
    }

    [Fact]
    public async Task LargeDiscardListsAreBoundedRatherThanCrashingResultConstruction()
    {
        string json = "{\"score\":1," + string.Join(",", Enumerable.Range(0, 100).Select(i => $"\"note{i}\":\"text\"")) + "}";
        var result = await Evaluator(json, aggregator: new()).EvaluateAsync(Candidate, Context);
        Assert.Equal(EvolutionEvaluationStatus.Completed, result.Status);
        Assert.Equal(64, result.Diagnostics.Count);
        Assert.Equal(1, result.Metrics["score"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task InconsistentRunnerSuccessIsRefused(int flaw)
    {
        var runner = new ScriptedProgramExecutionEngine(_ => new()
        {
            Success = true,
            Language = flaw == 0 ? ProgramLanguage.JavaScript : ProgramLanguage.Python,
            ExitCode = flaw == 1 ? 5 : 0,
            CompilationAttempted = flaw == 2,
            StdOut = "{\"quality\":1}"
        });
        var result = await new ScriptProgramFitnessEvaluator(runner, "# evaluate").EvaluateAsync(Candidate, Context);
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Equal("program_script_invalid_response", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, result.CostUnits);
    }

    [Fact]
    public async Task ArtifactTextRequiresExplicitOptInAndIsNotMislabeledRedacted()
    {
        const string json = "{\"quality\":1,\"artifacts\":{\"note\":\"retained text\"}}";
        var privateEvaluator = Evaluator(json);
        var optedIn = Evaluator(json, new() { RetainArtifactText = true });
        var result = await optedIn.EvaluateAsync(Candidate, Context);
        Assert.Contains("retained text", Assert.Single(result.Diagnostics).Message);
        Assert.False(result.Diagnostics[0].IsRedacted);
        Assert.NotEqual(privateEvaluator.VersionHash, optedIn.VersionHash);
    }

    [Fact]
    public async Task ExplicitAndDerivedScoresShareOneRegisteredDirection()
    {
        var aggregate = new ProgramMetricAggregator(new()
        {
            Strategy = ProgramMetricAggregationStrategy.Tchebycheff,
            Weights = new Dictionary<string, double> { ["speed"] = 1 },
            ReferencePoint = new Dictionary<string, double> { ["speed"] = 10 }
        });
        var explicitScore = await Evaluator("{\"quality\":2}", aggregator: aggregate).EvaluateAsync(Candidate, Context);
        var derivedScore = await Evaluator("{\"speed\":8}", aggregator: aggregate).EvaluateAsync(Candidate, Context);
        Assert.Equal(EvolutionOptimizationDirection.Minimize, explicitScore.Direction);
        Assert.Equal(explicitScore.Direction, derivedScore.Direction);
        Assert.Throws<ArgumentException>(() => Evaluator("{}", new() { Direction = EvolutionOptimizationDirection.Maximize }, aggregate));
    }
}
