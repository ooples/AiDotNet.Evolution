using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// V1-54 (#167), the criterion the story spells out as "(test)": with LLM feedback enabled, a
/// candidate the correctness gate rejects must not be able to win on the judge's opinion.
/// </summary>
/// <remarks>
/// The blend raises <c>Quality</c>, and that is the whole point of feedback — so the guarantee
/// cannot come from the score. It comes from what the judge does NOT touch: the measured
/// constraint violations and a failed status pass through unchanged, and those are what every
/// downstream gate reads. These tests pin that, so a future change to the blend cannot quietly
/// make a judged score sufficient on its own.
/// </remarks>
public sealed class JudgeCannotPromoteInvalidCandidateTests
{
    private static ProgramGenome Candidate() => new("return 1", ProgramLanguage.Python);

    private static EvolutionEvaluationContext Context() => new(1, 99UL, 7UL, 1);

    private static LlmFeedbackOptions Options() => new()
    {
        Criteria = new[] { "score" },
        MaxJudgeRetries = 0,
    };

    /// <summary>
    /// An infeasible measurement keeps its violations after a perfect judged score, so the gate
    /// still sees an invalid candidate however much the judge liked it.
    /// </summary>
    [Fact]
    public async Task A_perfect_judged_score_cannot_clear_a_constraint_violation()
    {
        var infeasible = new DelegateProgramFitnessEvaluator((_, _, _) =>
            new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed,
                quality: 0.5,
                direction: EvolutionOptimizationDirection.Maximize,
                descriptors: new Dictionary<string, double>(),
                objectives: Array.Empty<double>(),
                constraintViolations: new[] { 3.0 },
                costUnits: 1)));

        var judge = new LlmJudgeProgramFitnessEvaluator(
            new FakeChatClient("{\"score\":1}"), infeasible, options: Options());

        var result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.NotNull(result);
        Assert.Equal(new[] { 3.0 }, result!.ConstraintViolations);
        Assert.True(
            result.ConstraintViolations.Any(violation => violation > 0),
            "the judge cleared the violation, so an invalid candidate could reach the archive on its score.");
    }

    /// <summary>
    /// Control arm: the judge really is raising quality on this path, so the test above is not
    /// passing because feedback did nothing at all.
    /// </summary>
    [Fact]
    public async Task The_judge_does_raise_quality_on_the_same_path()
    {
        var feasible = new DelegateProgramFitnessEvaluator((_, _, _) =>
            new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed,
                quality: 0.5,
                direction: EvolutionOptimizationDirection.Maximize,
                descriptors: new Dictionary<string, double>(),
                objectives: Array.Empty<double>(),
                constraintViolations: Array.Empty<double>(),
                costUnits: 1)));

        var options = Options();
        options.CombinedBlend = 0.5;
        var judge = new LlmJudgeProgramFitnessEvaluator(
            new FakeChatClient("{\"score\":1}"), feasible, options: options);

        var result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.NotNull(result);
        Assert.True(result!.Quality > 0.5,
            $"the blend left quality at {result.Quality}, so this control arm proves nothing about the gate.");
        Assert.Empty(result.ConstraintViolations);
    }

    /// <summary>
    /// A failed measurement stays failed: feedback does not run on it by default, so a judged score
    /// cannot turn a candidate that did not even evaluate into a completed one.
    /// </summary>
    [Fact]
    public async Task A_failed_measurement_is_not_rescued_by_the_judge()
    {
        var failed = new DelegateProgramFitnessEvaluator((_, _, _) =>
            new ValueTask<EvolutionTaskResult>(
                EvolutionTaskResult.Failed("program_crashed", "The candidate threw.")));

        var judge = new LlmJudgeProgramFitnessEvaluator(
            new FakeChatClient("{\"score\":1}"), failed, options: Options());

        var result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.NotNull(result);
        Assert.Equal(EvolutionEvaluationStatus.Failed, result!.Status);
    }
}
