// Migrated from ooples/AiDotNet 9cd7d5d6c366a483874024650d02901f69a1829c:tests/AiDotNet.Tests/UnitTests/Evolution/Programs/LlmJudgeProgramFitnessEvaluatorTests.cs
// Original license retained in src/AiDotNet.Evolution.Programs/AIDOTNET-LICENSE.txt.
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Prompts;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

public sealed class LlmJudgeProgramFitnessEvaluatorTests
{
    private const string Source = "def solve(x):\n    return x * 2\n";

    private static EvolutionEvaluationContext Context() => new(1, 99UL, 7UL, 1);

    private static ProgramGenome Candidate() => new(Source, ProgramLanguage.Python);

    private static IProgramFitnessEvaluator Measured(
        double quality,
        EvolutionEvaluationStatus status = EvolutionEvaluationStatus.Completed) =>
        new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>(
            new EvolutionTaskResult(
                status,
                quality,
                EvolutionOptimizationDirection.Maximize,
                new Dictionary<string, double>(StringComparer.Ordinal) { ["passRate"] = quality },
                costUnits: 3)));

    private static string JudgeAnswer(double correctness, double efficiency, double readability) =>
        "{\"correctness\": " + correctness.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
        ", \"efficiency\": " + efficiency.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
        ", \"readability\": " + readability.ToString("R", System.Globalization.CultureInfo.InvariantCulture) +
        ", \"reasoning\": \"looks fine\"}";

    [Fact]
    public async Task JudgeScoresAreBlendedWithTheConfiguredShare()
    {
        var client = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        // 0.7 * 0.5 measured + 0.3 * 1.0 judged.
        Assert.Equal(0.65, result.Quality.GetValueOrDefault(), 10);
        Assert.Equal(1.0, result.Descriptors["llm_average"], 10);
        Assert.Equal(0.5, result.Descriptors["passRate"], 10);
        Assert.Equal(EvolutionEvaluationStatus.Completed, result.Status);
        Assert.Equal(4, result.CostUnits);
    }

    [Theory]
    // The two knobs compose multiplicatively on the judge's side, and the published measurements for #167
    // are all derived by INVERTING that composition. The forward direction is already pinned in both
    // run directions at Weight = 0.8, CombinedBlend = 0.7, by
    // JudgeCannotPromoteInvalidCandidateTests.The_documented_blend_formula_is_what_the_code_computes and
    // .Minimizing_inverts_the_judges_utility. Two things were pinned nowhere: the inverse the evidence
    // derives every judge score with, and llm_average being the judge's utility with Weight already
    // applied - which is the fact that decides whether the inverse divides by Weight. A derivation that
    // used Weight as the mixing coefficient instead survived both existing tests, and numbers were
    // reported from it. Asserted here across coefficient pairs and in both directions of the arithmetic.
    [InlineData(1.0, 0.7, 0.9, 0.6, 0.3)]
    [InlineData(0.5, 0.7, 0.9, 0.6, 0.3)]
    [InlineData(1.0, 0.2, 0.4, 0.4, 0.4)]
    [InlineData(0.25, 0.9, 1.0, 0.0, 0.5)]
    public async Task WeightAndBlendComposeMultiplicativelyAndTheInverseRecoversTheJudgeMean(
        double weight, double combinedBlend, double first, double second, double third)
    {
        const double measured = 0.5;
        var client = new FakeChatClient(JudgeAnswer(first, second, third));
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(measured), null,
            new LlmFeedbackOptions { Weight = weight, CombinedBlend = combinedBlend });

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        // Forward, exactly as LlmFeedbackOptions.Weight documents it:
        //   judgeUtility = (SUM s_i * Weight) / n;  quality = c * measured + (1 - c) * judgeUtility
        double judgeMean = (first + second + third) / 3.0;
        double expected = (combinedBlend * measured) + ((1.0 - combinedBlend) * (weight * judgeMean));
        Assert.Equal(expected, result.Quality.GetValueOrDefault(), 10);

        // llm_average is the judge's utility, Weight already applied - which is why the inverse has to
        // divide by Weight to get back to the mean the judge actually returned.
        Assert.Equal(weight * judgeMean, result.Descriptors["llm_average"], 10);

        // The inverse the evidence uses. It needs both coefficients; dividing by either alone recovers the
        // wrong number whenever the other is not 1.
        double blended = result.Quality.GetValueOrDefault();
        double recovered = (blended - (combinedBlend * measured)) / ((1.0 - combinedBlend) * weight);
        Assert.Equal(judgeMean, recovered, 10);
    }

    [Fact]
    public async Task TheBlendIsConfigurableRatherThanHardCoded()
    {
        var client = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null, new LlmFeedbackOptions { CombinedBlend = 1.0 });

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        // A blend of 1 records the judge's opinion without letting it move the score at all.
        Assert.Equal(0.5, result.Quality.GetValueOrDefault(), 10);
        Assert.Equal(1.0, result.Descriptors["llm_average"], 10);
    }

    [Fact]
    public async Task EachCriterionBecomesItsOwnPrefixedMetric()
    {
        var client = new FakeChatClient(JudgeAnswer(0.9, 0.6, 0.3));
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(0.9, result.Descriptors["llm_correctness"], 10);
        Assert.Equal(0.6, result.Descriptors["llm_efficiency"], 10);
        Assert.Equal(0.3, result.Descriptors["llm_readability"], 10);
        Assert.Equal(0.6, result.Descriptors["llm_average"], 10);
        Assert.Equal(new[] { 0.9, 0.6, 0.3 }, result.Objectives.Select(value => Math.Round(value, 10)));
    }

    [Fact]
    public async Task WeightIsAppliedToTheScoresThatDriveTheBlend()
    {
        // Upstream weights the criteria average and then blends the UNWEIGHTED one, so its weight has no effect on
        // the final score. Here halving the weight really does halve the judge's contribution.
        var full = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));
        var halved = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));

        EvolutionTaskResult atFullWeight = await new LlmJudgeProgramFitnessEvaluator(full, Measured(0.5))
            .EvaluateAsync(Candidate(), Context());
        EvolutionTaskResult atHalfWeight = await new LlmJudgeProgramFitnessEvaluator(
                halved, Measured(0.5), null, new LlmFeedbackOptions { Weight = 0.5 })
            .EvaluateAsync(Candidate(), Context());

        Assert.Equal(0.65, atFullWeight.Quality.GetValueOrDefault(), 10);
        Assert.Equal(0.5, atHalfWeight.Descriptors["llm_average"], 10);
        Assert.Equal(0.5, atHalfWeight.Quality.GetValueOrDefault(), 10);
    }

    [Fact]
    public async Task CustomCriteriaAreAskedForAndReadBack()
    {
        var client = new FakeChatClient("{\"handles_edge_cases\": 0.8, \"is_idiomatic\": 0.4}");
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null,
            new LlmFeedbackOptions { Criteria = new List<string> { "handles edge cases", "is idiomatic" } });

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(0.8, result.Descriptors["llm_handles_edge_cases"], 10);
        Assert.Equal(0.4, result.Descriptors["llm_is_idiomatic"], 10);
        Assert.Contains("handles edge cases", client.Conversations[0][1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedCandidatesAreNotJudgedByDefault()
    {
        // Upstream runs its judge on error metrics too, which lets a crashed program earn a respectable score.
        var client = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.1, EvolutionEvaluationStatus.Failed));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(0.1, result.Quality.GetValueOrDefault(), 10);
        Assert.Equal(0, client.Calls);
        Assert.DoesNotContain("llm_average", result.Descriptors.Keys);
    }

    [Fact]
    public async Task FailedCandidatesCanBeJudgedWhenAskedFor()
    {
        var client = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.1, EvolutionEvaluationStatus.Failed), null,
            new LlmFeedbackOptions { RunOnFailedEvaluations = true });

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(1, client.Calls);
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Contains("llm_average", result.Descriptors.Keys);
    }

    [Fact]
    public async Task AnUnusableAnswerLeavesTheMeasuredScoreUntouched()
    {
        var client = new FakeChatClient("I am not going to answer in JSON.", "still refusing");
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.42));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(0.42, result.Quality.GetValueOrDefault(), 10);
        Assert.DoesNotContain("llm_average", result.Descriptors.Keys);
        Assert.True(result.Diagnostics.Single(diagnostic => diagnostic.Code == "llm_judge_unusable").IsRedacted);
        Assert.Equal(1, judge.JudgeFailures);
        Assert.Equal(2, judge.JudgeCalls);
    }

    [Fact]
    public async Task AnUnparseableAnswerIsRetriedBeforeGivingUp()
    {
        var client = new FakeChatClient("prose", JudgeAnswer(0.5, 0.5, 0.5));
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(2, judge.JudgeCalls);
        Assert.Equal(0, judge.JudgeFailures);
        Assert.Equal(0.5, result.Descriptors["llm_average"], 10);
    }

    [Fact]
    public async Task RetryCountIsConfigurable()
    {
        var client = new FakeChatClient("prose", "prose", "prose", "prose", "prose");
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null, new LlmFeedbackOptions { MaxJudgeRetries = 3 });

        await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(4, judge.JudgeCalls);
    }

    [Fact]
    public async Task ProviderExceptionsNeverLeakTheirMessageIntoADiagnostic()
    {
        var client = new FakeChatClient("prose")
        {
            ThrowOnFirstCall = new InvalidOperationException("api-key=sk-abc123 host=internal.example")
        };
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        EvolutionDiagnostic diagnostic = result.Diagnostics.Single(item => item.Code == "llm_judge_unusable");
        Assert.DoesNotContain("sk-abc123", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal.example", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutOfRangeScoresAreClampedRatherThanRejected()
    {
        var client = new FakeChatClient(JudgeAnswer(1.4, -0.2, 0.5));
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(1.0, result.Descriptors["llm_correctness"], 10);
        Assert.Equal(0.0, result.Descriptors["llm_efficiency"], 10);
        Assert.Equal(1, judge.JudgeCalls);
    }

    [Fact]
    public async Task AConstrainedJsonResponseFormatIsRequested()
    {
        var client = new FakeChatClient(JudgeAnswer(0.5, 0.5, 0.5));
        var judge = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5));

        await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(ProgramChatResponseFormat.Json, client.LastOptions?.ResponseFormat);
        Assert.NotNull(client.LastOptions?.Seed);
    }

    [Fact]
    public async Task TheJudgeCanBeTurnedOffEntirely()
    {
        var client = new FakeChatClient(JudgeAnswer(1.0, 1.0, 1.0));
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null, new LlmFeedbackOptions { Enabled = false });

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(0.5, result.Quality.GetValueOrDefault(), 10);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task ObjectiveRecordingCanBeTurnedOff()
    {
        var client = new FakeChatClient(JudgeAnswer(0.9, 0.6, 0.3));
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null, new LlmFeedbackOptions { RecordObjectives = false });

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Empty(result.Objectives);
        Assert.Equal(0.6, result.Descriptors["llm_average"], 10);
    }

    [Fact]
    public async Task JudgingIsDeterministicForTheSameEvaluationContext()
    {
        var first = new FakeChatClient(JudgeAnswer(0.5, 0.5, 0.5));
        var second = new FakeChatClient(JudgeAnswer(0.5, 0.5, 0.5));

        await new LlmJudgeProgramFitnessEvaluator(first, Measured(0.5)).EvaluateAsync(Candidate(), Context());
        await new LlmJudgeProgramFitnessEvaluator(second, Measured(0.5)).EvaluateAsync(Candidate(), Context());

        Assert.Equal(first.LastOptions?.Seed, second.LastOptions?.Seed);
    }

    [Fact]
    public void VersionHashTracksTheInnerEvaluatorAndTheBlend()
    {
        var client = new FakeChatClient("ignored");
        string baseline = new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5)).VersionHash;

        Assert.Equal(baseline, new LlmJudgeProgramFitnessEvaluator(client, Measured(0.5)).VersionHash);
        Assert.NotEqual(baseline, new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null, new LlmFeedbackOptions { CombinedBlend = 0.9 }).VersionHash);
        Assert.NotEqual(baseline, new LlmJudgeProgramFitnessEvaluator(
            client, Measured(0.5), null,
            new LlmFeedbackOptions { Criteria = new List<string> { "novelty" } }).VersionHash);
        Assert.NotEqual(baseline, new LlmJudgeProgramFitnessEvaluator(
            client,
            new DelegateProgramFitnessEvaluator(_ => 1.0, "other-evaluator", "other-v1")).VersionHash);
    }

    [Fact]
    public void OptionsAreValidatedAndCopied()
    {
        var client = new FakeChatClient("ignored");
        IProgramFitnessEvaluator inner = Measured(0.5);

#pragma warning disable CS8600, CS8625
        Assert.Throws<ArgumentNullException>(() => new LlmJudgeProgramFitnessEvaluator(null, inner));
        Assert.Throws<ArgumentNullException>(() => new LlmJudgeProgramFitnessEvaluator(client, null));
#pragma warning restore CS8600, CS8625
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmJudgeProgramFitnessEvaluator(
            client, inner, null, new LlmFeedbackOptions { CombinedBlend = 1.5 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmJudgeProgramFitnessEvaluator(
            client, inner, null, new LlmFeedbackOptions { Weight = -1 }));
        Assert.Throws<ArgumentException>(() => new LlmJudgeProgramFitnessEvaluator(
            client, inner, null, new LlmFeedbackOptions { Criteria = new List<string>() }));
        Assert.Throws<ArgumentException>(() => new LlmJudgeProgramFitnessEvaluator(
            client, inner, null, new LlmFeedbackOptions { Criteria = new List<string> { "a", "a" } }));

        var options = new LlmFeedbackOptions { Weight = 0.5 };
        var judge = new LlmJudgeProgramFitnessEvaluator(client, inner, null, options);
        options.Weight = 0.1;

        Assert.Equal(0.5, judge.GetOptions().Weight);
        Assert.Same(inner, judge.Inner);
    }

    [Fact]
    public void CriterionFieldNamesMatchWhatTheEvaluationPromptAsksFor()
    {
        Assert.Equal("handles_edge_cases", ProgramPromptBuilder.ToCriterionFieldName("Handles Edge Cases"));
        Assert.Equal("big_o", ProgramPromptBuilder.ToCriterionFieldName("  Big-O  "));
        Assert.Equal("score", ProgramPromptBuilder.ToCriterionFieldName("***"));

        var builder = new ProgramPromptBuilder();
        IReadOnlyList<ProgramChatMessage> messages = builder.BuildEvaluationMessages(
            Candidate(), new[] { "Handles Edge Cases" });

        Assert.Contains("\"handles_edge_cases\"", messages[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANullInnerResultBecomesAFailedResultRatherThanACrash()
    {
        var client = new FakeChatClient(JudgeAnswer(0.5, 0.5, 0.5));
        var judge = new LlmJudgeProgramFitnessEvaluator(
            client,
            new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>((EvolutionTaskResult)null!)));

        EvolutionTaskResult result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "llm_judge_inner_returned_null");
        Assert.Equal(0, client.Calls);
    }
}
