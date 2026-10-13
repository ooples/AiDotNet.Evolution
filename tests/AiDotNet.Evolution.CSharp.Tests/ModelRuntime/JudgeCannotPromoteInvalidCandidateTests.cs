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
    /// <summary>
    /// The documented formula, pinned numerically so the documentation on
    /// <c>LlmFeedbackOptions.Weight</c> cannot drift away from the code (#167's first criterion).
    /// </summary>
    /// <remarks>
    /// Two criteria scoring 1.0 and 0.5 with <c>Weight = 0.8</c> give
    /// <c>judgeUtility = (1.0*0.8 + 0.5*0.8) / 2 = 0.6</c>. Maximizing, with
    /// <c>CombinedBlend = 0.7</c> and a measured 0.5:
    /// <c>0.7*0.5 + 0.3*0.6 = 0.53</c>.
    /// </remarks>
    [Fact]
    public async Task The_documented_blend_formula_is_what_the_code_computes()
    {
        var feasible = Measured(0.5, EvolutionOptimizationDirection.Maximize);
        var options = new LlmFeedbackOptions
        {
            Criteria = new[] { "a", "b" },
            MaxJudgeRetries = 0,
            Weight = 0.8,
            CombinedBlend = 0.7,
        };

        var judge = new LlmJudgeProgramFitnessEvaluator(
            new FakeChatClient("{\"a\":1.0,\"b\":0.5}"), feasible, options: options);

        var result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.NotNull(result);
        Assert.Equal(0.53, result!.Quality!.Value, 10);
    }

    /// <summary>
    /// Minimizing inverts the judge's utility, because a judge always scores "better is higher".
    /// </summary>
    /// <remarks>
    /// Same scores and knobs, minimizing: <c>judgeQuality = 1 - 0.6 = 0.4</c>, so
    /// <c>0.7*0.5 + 0.3*0.4 = 0.47</c>.
    /// </remarks>
    [Fact]
    public async Task Minimizing_inverts_the_judges_utility()
    {
        var feasible = Measured(0.5, EvolutionOptimizationDirection.Minimize);
        var options = new LlmFeedbackOptions
        {
            Criteria = new[] { "a", "b" },
            MaxJudgeRetries = 0,
            Weight = 0.8,
            CombinedBlend = 0.7,
        };

        var judge = new LlmJudgeProgramFitnessEvaluator(
            new FakeChatClient("{\"a\":1.0,\"b\":0.5}"), feasible, options: options);

        var result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.NotNull(result);
        Assert.Equal(0.47, result!.Quality!.Value, 10);
    }

    /// <summary>
    /// The feedback cost is charged separately as well as totalled: CostUnits carries measured plus
    /// judge because that is what a budget honours, and the judge's own share is a descriptor so a
    /// consumer can split one evaluation without a cumulative counter (#167).
    /// </summary>
    [Fact]
    public async Task The_judges_own_cost_is_reported_separately_from_the_measured_cost()
    {
        var feasible = Measured(0.5, EvolutionOptimizationDirection.Maximize);
        var options = new LlmFeedbackOptions
        {
            Criteria = new[] { "a" },
            MaxJudgeRetries = 0,
            JudgeCallCostUnits = 3,
        };

        var judge = new LlmJudgeProgramFitnessEvaluator(
            new FakeChatClient("{\"a\":1.0}"), feasible, options: options);

        var result = await judge.EvaluateAsync(Candidate(), Context());

        Assert.NotNull(result);
        string key = options.MetricPrefix + LlmFeedbackOptions.CostUnitsMetricSuffix;
        Assert.True(result!.Descriptors.ContainsKey(key), $"no {key} descriptor was reported.");

        // One judge call at three units each, and the measured evaluator's one unit stays in the
        // total, so the split is recoverable: total - judge = measured.
        double judgeCost = result.Descriptors[key];
        Assert.Equal(3.0, judgeCost, 10);
        Assert.Equal(1.0, result.CostUnits - judgeCost, 10);
    }

    private static IProgramFitnessEvaluator Measured(
        double quality, EvolutionOptimizationDirection direction) =>
        new DelegateProgramFitnessEvaluator((_, _, _) =>
            new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed,
                quality,
                direction,
                descriptors: new Dictionary<string, double>(),
                objectives: Array.Empty<double>(),
                constraintViolations: Array.Empty<double>(),
                costUnits: 1)));

}
