using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

public sealed class JudgeAdversarialTests
{
    private static ProgramGenome Candidate() => new("return 1", ProgramLanguage.Python);
    private static EvolutionEvaluationContext Context() => new(1, 99UL, 7UL, 1);
    private static LlmFeedbackOptions Options() => new() { Criteria = new[] { "score" }, MaxJudgeRetries = 0 };
    private static IProgramFitnessEvaluator Measured(double quality = .5, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize) =>
        new DelegateProgramFitnessEvaluator(_ => quality, direction: direction);

    [Theory]
    [InlineData("{\"score\":1,\"score\":0}")]
    [InlineData("{\"score\":true}")]
    [InlineData("{\"score\":\"1\"}")]
    [InlineData("{\"score\":1e999}")]
    [InlineData("{\"score\":1} {\"score\":0}")]
    public async Task AmbiguousOrNonNumericScoresCannotChangeFitness(string answer)
    {
        var judge = new LlmJudgeProgramFitnessEvaluator(new FakeChatClient(answer), Measured(), options: Options());
        var result = await judge.EvaluateAsync(Candidate(), Context());
        Assert.Equal(.5, result.Quality);
        Assert.Equal(1, result.CostUnits);
        Assert.Equal(1, judge.JudgeFailures);
    }

    [Fact]
    public async Task OversizedAndDeepResponsesAreBounded()
    {
        foreach (string answer in new[] { new string('x', 100), "{\"score\":1,\"deep\":" + new string('[', 20) + "0" + new string(']', 20) + "}" })
        {
            var options = Options(); options.MaxResponseChars = 80;
            var judge = new LlmJudgeProgramFitnessEvaluator(new FakeChatClient(answer), Measured(), options: options);
            var result = await judge.EvaluateAsync(Candidate(), Context());
            Assert.Equal(.5, result.Quality);
            Assert.Equal(1, judge.JudgeFailures);
        }
    }

    [Fact]
    public async Task BetterJudgeUtilityLowersMinimizedQuality()
    {
        var judge = new LlmJudgeProgramFitnessEvaluator(new FakeChatClient("{\"score\":1}"),
            Measured(.5, EvolutionOptimizationDirection.Minimize), options: Options());
        var result = await judge.EvaluateAsync(Candidate(), Context());
        Assert.Equal(.35, result.Quality!.Value, 10);
        Assert.Equal(EvolutionOptimizationDirection.Minimize, result.Direction);
        Assert.Equal(0, Assert.Single(result.Objectives));
    }

    [Fact]
    public async Task RawUnnormalizedScoresAreRefusedBeforeSpend()
    {
        var client = new FakeChatClient("{\"score\":1}");
        var result = await new LlmJudgeProgramFitnessEvaluator(client, Measured(100), options: Options()).EvaluateAsync(Candidate(), Context());
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public void NormalizedCriterionCollisionsAreRejected()
    {
        foreach (var criteria in new[] { new[] { "a b", "a-b" }, new[] { "average" }, new[] { "Reasoning!" } })
            Assert.Throws<ArgumentException>(() => new LlmFeedbackOptions { Criteria = criteria }.Validate());
    }

    [Fact]
    public async Task RetriesAreChargedEvenWithoutUsableScores()
    {
        var options = Options(); options.MaxJudgeRetries = 2; options.JudgeCallCostUnits = 4;
        var judge = new LlmJudgeProgramFitnessEvaluator(new FakeChatClient("bad"), Measured(), options: options);
        var result = await judge.EvaluateAsync(Candidate(), Context());
        Assert.Equal(12, result.CostUnits);
        Assert.Equal(3, judge.JudgeCalls);
    }

    [Fact]
    public async Task WeightedPanelPreservesPartialSuccessAndCostThroughDecorator()
    {
        var panel = new ProgramJudgePanel(new[] {
            new ProgramJudgeMember(new FakeChatClient("{\"score\":1}"), 3),
            new ProgramJudgeMember(new FakeChatClient("{\"score\":0}"), 1),
            new ProgramJudgeMember(new FakeChatClient("bad"), double.MaxValue) });
        var options = Options(); options.JudgeWithEveryEnsembleMember = true;
        var judge = new LlmJudgeProgramFitnessEvaluator(new Decorator(panel), Measured(), options: options);
        var result = await judge.EvaluateAsync(Candidate(), Context());
        Assert.Equal(.75, result.Descriptors["llm_average"]);
        Assert.Equal(3, result.CostUnits);
    }

    [Fact]
    public async Task AllFailedPanelStillReportsCost()
    {
        var panel = new ProgramJudgePanel(new[] { new ProgramJudgeMember(new FakeChatClient("bad")) });
        var options = Options(); options.JudgeWithEveryEnsembleMember = true;
        var result = await new LlmJudgeProgramFitnessEvaluator(panel, Measured(), options: options).EvaluateAsync(Candidate(), Context());
        Assert.Equal(.5, result.Quality);
        Assert.Equal(1, result.CostUnits);
    }

    [Fact]
    public void VersionIncludesModelAndSamplingSettings()
    {
        string Hash(IProgramChatClient client, LlmFeedbackOptions options) => new LlmJudgeProgramFitnessEvaluator(client, Measured(), options: options).VersionHash;
        var first = Options(); var second = Options(); second.Temperature = .3;
        Assert.NotEqual(Hash(new MutableClient(), first), Hash(new MutableClient(), second));
        Assert.NotEqual(Hash(new MutableClient(), first), Hash(new MutableClient { ModelId = "other" }, first));
    }

    [Fact]
    public async Task ProviderIdentityDriftIsRejectedBeforeDispatch()
    {
        var client = new MutableClient(); var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(), options: Options());
        client.ModelId = "changed";
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await judge.EvaluateAsync(Candidate(), Context()));
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task CancellationAfterProviderReturnCannotBecomeSuccess()
    {
        using var cancel = new CancellationTokenSource();
        var client = new MutableClient { OnCall = () => cancel.Cancel() };
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(), options: Options());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await judge.EvaluateAsync(Candidate(), Context(), cancel.Token));
    }

    [Fact]
    public async Task FatalProviderErrorsAreNotRetried()
    {
        var client = new FakeChatClient("bad") { ThrowOnFirstCall = new OutOfMemoryException() };
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(), options: Options());
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await judge.EvaluateAsync(Candidate(), Context()));
    }

    [Fact]
    public async Task CritiqueSchemaUsesConfiguredNameAndDoesNotClaimRedaction()
    {
        var options = Options(); options.CritiqueField = "feedback";
        var client = new FakeChatClient("{\"score\":1,\"feedback\":\"private-sentinel\"}");
        var result = await new LlmJudgeProgramFitnessEvaluator(client, Measured(), options: options).EvaluateAsync(Candidate(), Context());
        Assert.Contains("feedback", client.Conversations[0][1].Text);
        Assert.False(Assert.Single(result.Artifacts).IsRedacted);
    }

    [Fact]
    public async Task CanceledPanelCountsOnlyDispatchedMembers()
    {
        using var cancel = new CancellationTokenSource();
        var first = new MutableClient { OnCall = () => cancel.Cancel() };
        var second = new MutableClient();
        var panel = new ProgramJudgePanel(new[] { new ProgramJudgeMember(first), new ProgramJudgeMember(second) });
        var options = Options(); options.JudgeWithEveryEnsembleMember = true;
        var judge = new LlmJudgeProgramFitnessEvaluator(panel, Measured(), options: options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await judge.EvaluateAsync(Candidate(), Context(), cancel.Token));
        Assert.Equal(1, judge.JudgeCalls);
        Assert.Equal(0, second.Calls);
    }

    [Fact]
    public async Task EqualHugePanelWeightsDoNotOverflow()
    {
        var panel = new ProgramJudgePanel(new[] {
            new ProgramJudgeMember(new FakeChatClient("{\"score\":1}"), double.MaxValue),
            new ProgramJudgeMember(new FakeChatClient("{\"score\":0}"), double.MaxValue) });
        var options = Options(); options.JudgeWithEveryEnsembleMember = true;
        var result = await new LlmJudgeProgramFitnessEvaluator(panel, Measured(), options: options).EvaluateAsync(Candidate(), Context());
        Assert.Equal(.5, result.Descriptors["llm_average"]);
    }

    [Fact]
    public async Task DescriptorCollisionsAreRefusedWithoutOverwritingMeasuredEvidence()
    {
        var client = new FakeChatClient("{\"score\":1}");
        var inner = new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>(
            new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, .5, descriptors: new Dictionary<string, double> { ["llm_score"] = .2 }, costUnits: 3)));
        var result = await new LlmJudgeProgramFitnessEvaluator(client, inner, options: Options()).EvaluateAsync(Candidate(), Context());
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Equal(.2, result.Descriptors["llm_score"]);
        Assert.Equal(3, result.CostUnits);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task IdentityChangeInsideProviderCannotBePublished()
    {
        var client = new MutableClient(); client.OnCall = () => client.ModelId = "changed";
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(), options: Options());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await judge.EvaluateAsync(Candidate(), Context()));
    }

    private sealed class MutableClient : IProgramChatClient
    {
        public string ModelId { get; set; } = "model";
        public int Calls { get; private set; }
        public Action? OnCall { get; set; }
        public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null, CancellationToken cancellationToken = default)
        { Calls++; OnCall?.Invoke(); return Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant("{\"score\":1}"))); }
    }

    private sealed class Decorator(IProgramChatClient inner) : IProgramChatClientDecorator
    {
        public IProgramChatClient Inner => inner;
        public string ModelId => "decorated";
        public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null, CancellationToken cancellationToken = default) => inner.GetResponseAsync(messages, options, cancellationToken);
    }
}
