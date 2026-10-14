using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Metrics;
using AiDotNet.Evolution.Prompts;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// Each option is set both ways and must change what the judge sends or reports, or which metric wins the
/// aggregate. Constructing an option without observing its effect would pass with the option ignored.
/// </summary>
public sealed class JudgeAndMetricOptionBehaviourTests
{
    private const string Answer =
        "{\"correctness\": 0.9, \"efficiency\": 0.6, \"readability\": 0.3, \"reasoning\": \"looks fine\"}";

    [Fact]
    public void CombinedScoreKey_names_the_metric_that_wins_the_aggregate()
    {
        Dictionary<string, ProgramMetricValue> metrics = Metrics(
            ("combined_score", ProgramMetricValue.Number(0.2)),
            ("score", ProgramMetricValue.Number(0.9)),
            ("accuracy", ProgramMetricValue.Number(0.1)));

        ProgramMetricAggregationResult standard = new ProgramMetricAggregator().Aggregate(metrics);
        Assert.Equal(0.2, standard.Value, 12);
        Assert.Equal(new[] { "combined_score" }, standard.ContributingMetrics);

        ProgramMetricAggregationResult renamed = new ProgramMetricAggregator(
            new ProgramMetricAggregationOptions { CombinedScoreKey = "score" }).Aggregate(metrics);
        Assert.True(renamed.UsedCombinedScore);
        Assert.Equal(0.9, renamed.Value, 12);
        Assert.Equal(new[] { "score" }, renamed.ContributingMetrics);
    }

    [Fact]
    public void AllowTextMetricConversion_decides_whether_a_numeric_string_counts()
    {
        Dictionary<string, ProgramMetricValue> metrics = Metrics(
            ("combined_score", ProgramMetricValue.Text("0.9")),
            ("accuracy", ProgramMetricValue.Number(0.1)));

        ProgramMetricAggregationResult converted = new ProgramMetricAggregator().Aggregate(metrics);
        Assert.True(converted.UsedCombinedScore);
        Assert.Equal(0.9, converted.Value, 12);

        ProgramMetricAggregationResult strict = new ProgramMetricAggregator(
            new ProgramMetricAggregationOptions { AllowTextMetricConversion = false }).Aggregate(metrics);
        Assert.False(strict.UsedCombinedScore);
        Assert.Equal(0.1, strict.Value, 12);
    }

    [Fact]
    public async Task MetricPrefix_names_the_judge_metrics()
    {
        EvolutionTaskResult standard = await Judge(new LlmFeedbackOptions()).EvaluateAsync(Candidate(), Context());
        Assert.Contains("llm_correctness", standard.Descriptors.Keys);

        EvolutionTaskResult renamed = await Judge(new LlmFeedbackOptions { MetricPrefix = "judge_" })
            .EvaluateAsync(Candidate(), Context());
        Assert.Equal(0.9, renamed.Descriptors["judge_correctness"], 10);
        Assert.DoesNotContain(renamed.Descriptors.Keys, key => key.StartsWith("llm_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResponseSchema_replaces_the_schema_the_judge_is_shown()
    {
        const string Marker = "CUSTOM-SCHEMA-7f3a";
        var standard = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions(), standard).EvaluateAsync(Candidate(), Context());
        Assert.DoesNotContain(Marker, AllText(standard));
        Assert.Contains("number in [0,1]", AllText(standard));

        var custom = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions { ResponseSchema = "{\"" + Marker + "\": 1}" }, custom)
            .EvaluateAsync(Candidate(), Context());
        Assert.Contains(Marker, AllText(custom));
        Assert.DoesNotContain("number in [0,1]", AllText(custom));
    }

    [Fact]
    public async Task RequestJsonResponseFormat_decides_the_requested_response_format()
    {
        var standard = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions(), standard).EvaluateAsync(Candidate(), Context());
        Assert.Equal(ProgramChatResponseFormat.Json, standard.LastOptions?.ResponseFormat);

        var plain = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions { RequestJsonResponseFormat = false }, plain)
            .EvaluateAsync(Candidate(), Context());
        Assert.Equal(ProgramChatResponseFormat.Text, plain.LastOptions?.ResponseFormat);
    }

    [Fact]
    public async Task MaxOutputTokens_bounds_each_judge_request()
    {
        var standard = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions(), standard).EvaluateAsync(Candidate(), Context());
        var bounded = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions { MaxOutputTokens = 321 }, bounded).EvaluateAsync(Candidate(), Context());

        Assert.Equal(321, bounded.LastOptions?.MaxOutputTokens);
        Assert.Equal(new LlmFeedbackOptions().MaxOutputTokens, standard.LastOptions?.MaxOutputTokens);
        Assert.NotEqual(321, standard.LastOptions?.MaxOutputTokens);
    }

    [Fact]
    public void ResourceAccounting_on_a_task_changes_its_identity_by_cost_unit_semantics()
    {
        // The caller's evaluator owns ledger charging; the task only records which cost units its results are
        // in, so a checkpoint taken under one cost-unit semantics cannot resume under another.
        var ledger = new EvolutionResourceLedger("task-accounting",
            new EvolutionResources(new Dictionary<string, decimal> { ["cost_units"] = 10 }));
        string Hash(ProgramEvolutionResourceOptions? resources) => new ProgramEvolutionTask(
            new DelegateProgramFitnessEvaluator(_ => 1),
            options: new ProgramTaskOptions { ResourceAccounting = resources }).VersionHash;

        string plain = Hash(null);
        string first = Hash(new ProgramEvolutionResourceOptions(ledger, 1, "cost-units-v1"));
        string second = Hash(new ProgramEvolutionResourceOptions(ledger, 1, "cost-units-v2"));

        Assert.Equal(plain, Hash(null));
        Assert.NotEqual(plain, first);
        Assert.NotEqual(first, second);
        Assert.Equal(first, Hash(new ProgramEvolutionResourceOptions(ledger, 5, "cost-units-v1")));
    }

    [Fact]
    public async Task The_judge_is_told_what_the_program_is_for()
    {
        // It was not. The judge saw the program, the criteria and the response schema, and nothing about the
        // task - so it could not distinguish solving from memorising, and the feedback story's "suspicion of
        // test gaming" was unreachable rather than weak. Measured before the fix: a six-entry lookup table of
        // the visible test pairs scored 0.160 against 0.185 for an honest-but-wrong formula.
        const string Task = "Recover the hidden rule behind the examples, do not store their answers.";
        var described = new FakeChatClient(Answer);
        await new LlmJudgeProgramFitnessEvaluator(described, Measured(),
                new ProgramPromptBuilder(new ProgramEvolutionPromptOptions { TaskDescription = Task }),
                new LlmFeedbackOptions())
            .EvaluateAsync(Candidate(), Context());

        Assert.Contains(Task, AllText(described), StringComparison.Ordinal);
        Assert.Contains("has not solved the task", AllText(described), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_judge_with_no_task_description_renders_no_task_section()
    {
        // The inverted half: the section must disappear rather than leave an empty heading, which is how the
        // proposal prompts already behave, so the two paths describe a task the same way or not at all.
        var plain = new FakeChatClient(Answer);
        await Judge(new LlmFeedbackOptions(), plain).EvaluateAsync(Candidate(), Context());

        Assert.DoesNotContain("# Task Description", AllText(plain), StringComparison.Ordinal);
        Assert.Contains("has not solved the task", AllText(plain), StringComparison.Ordinal);
    }

    [Fact]
    public void A_descriptive_criterion_is_refused_against_Criteria_not_MetricPrefix()
    {
        // A criterion doubles as a metric name, so a descriptive one overflows the 64-character bound. That
        // is a real constraint, but the old message said only "bounded and canonical" and blamed
        // MetricPrefix - which sends a reader to the one field they did not set. Cost a campaign run to
        // diagnose, hence the test.
        var options = new LlmFeedbackOptions();
        options.Criteria.Clear();
        options.Criteria.Add("Correctness");
        options.Criteria.Add("Generality: does this compute the rule, or reproduce the answers it was shown? "
            + "A program that stores the example answers has not solved the task");

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Validate());

        Assert.Equal(nameof(LlmFeedbackOptions.Criteria), error.ParamName);
        Assert.Contains("characters", error.Message, StringComparison.Ordinal);
        Assert.Contains("EvaluatorSystemMessage", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_over_long_metric_prefix_is_still_attributed_to_the_prefix()
    {
        // The inverted half: when the prefix is what overflows, it must still be named - otherwise the fix
        // above would just move the blame rather than place it.
        var options = new LlmFeedbackOptions { MetricPrefix = new string('p', 64) };

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Validate());

        Assert.Equal(nameof(LlmFeedbackOptions.MetricPrefix), error.ParamName);
    }

    [Fact]
    public void A_prefix_that_fits_average_but_not_the_cost_descriptor_is_refused()
    {
        // The validator checked the prefix against "average" (7 characters) and every criterion, but never
        // against "cost_units" (10) - which the judge also emits. A 55-character prefix therefore passed
        // validation and then produced a 65-character descriptor name, three over the bound this very
        // validator enforces, so the overflow surfaced as a descriptor the archive refused rather than as a
        // configuration refused here.
        var options = new LlmFeedbackOptions { MetricPrefix = new string('p', 55) };
        Assert.Equal(62, options.MetricPrefix.Length + LlmFeedbackOptions.AverageMetricSuffix.Length);
        Assert.Equal(65, options.MetricPrefix.Length + LlmFeedbackOptions.CostUnitsMetricSuffix.Length);

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Validate());

        Assert.Equal(nameof(LlmFeedbackOptions.MetricPrefix), error.ParamName);
    }

    [Fact]
    public void A_criterion_that_normalises_to_the_cost_descriptor_is_refused()
    {
        // The judge writes what the judging cost under this name. A criterion that lands on it would make
        // one descriptor mean both a judged score and a cost, with whichever is written last winning.
        var options = new LlmFeedbackOptions();
        options.Criteria.Clear();
        options.Criteria.Add("Cost Units");

        ArgumentException error = Assert.Throws<ArgumentException>(() => options.Validate());

        Assert.Equal(nameof(LlmFeedbackOptions.Criteria), error.ParamName);
        Assert.Contains(LlmFeedbackOptions.CostUnitsMetricSuffix, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_short_criterion_token_is_accepted_so_the_bound_is_not_the_whole_story()
    {
        var options = new LlmFeedbackOptions();
        options.Criteria.Clear();
        options.Criteria.Add("Correctness");
        options.Criteria.Add("Generality");

        options.Validate();
    }

    private static LlmJudgeProgramFitnessEvaluator Judge(LlmFeedbackOptions options, FakeChatClient? client = null) =>
        new(client ?? new FakeChatClient(Answer), Measured(), null, options);

    private static string AllText(FakeChatClient client) =>
        string.Join("\n", client.Conversations.SelectMany(messages => messages).Select(message => message.Text));

    private static EvolutionEvaluationContext Context() => new(1, 99UL, 7UL, 1);

    private static ProgramGenome Candidate() => new("def solve(x):\n    return x * 2\n", ProgramLanguage.Python);

    private static IProgramFitnessEvaluator Measured() =>
        new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>(
            new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed,
                0.5,
                EvolutionOptimizationDirection.Maximize,
                new Dictionary<string, double>(StringComparer.Ordinal) { ["passRate"] = 0.5 },
                costUnits: 3)));

    private static Dictionary<string, ProgramMetricValue> Metrics(params (string Name, ProgramMetricValue Value)[] entries)
    {
        var result = new Dictionary<string, ProgramMetricValue>(StringComparer.Ordinal);
        foreach ((string name, ProgramMetricValue value) in entries) result[name] = value;
        return result;
    }
}
