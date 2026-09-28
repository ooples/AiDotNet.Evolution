using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Experience;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Novelty;

/// <summary>V1-52: evaluated programs become lessons, classified by what the evaluation and archive decided.</summary>
public sealed class ProgramExperienceRecorderTests
{
    private static EvolutionEvent<ProgramGenome> Evaluated(string source, string? description, EvolutionEvaluationStatus status,
        EvolutionArchiveInsertionResult? insertion, double[]? violations = null)
    {
        var genome = new ProgramGenome(source, ProgramLanguage.Python, description);
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(1, new EvolutionCanonicalGenome<ProgramGenome>(genome, genome.Id), lineage);
        var evaluation = new EvolutionEvaluation(1, genome.Id, status, status == EvolutionEvaluationStatus.Completed ? 0.5 : null,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double> { ["x"] = 1 }, Array.Empty<double>(),
            violations ?? Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage, EvolutionCacheStatus.Miss,
            Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "config-v1");
        return new EvolutionEvent<ProgramGenome>(EvolutionEventKind.Evaluated, 7, candidate, evaluation, insertion);
    }

    [Theory]
    [InlineData(EvolutionEvaluationStatus.Completed, EvolutionArchiveInsertionResult.Inserted, false, ProgramExperienceOutcome.Improved)]
    [InlineData(EvolutionEvaluationStatus.Completed, EvolutionArchiveInsertionResult.NotImproved, false, ProgramExperienceOutcome.NotImproved)]
    [InlineData(EvolutionEvaluationStatus.Completed, EvolutionArchiveInsertionResult.Inserted, true, ProgramExperienceOutcome.Invalid)]
    [InlineData(EvolutionEvaluationStatus.Failed, null, false, ProgramExperienceOutcome.Failed)]
    public async Task Each_evaluation_becomes_one_lesson_with_its_outcome(EvolutionEvaluationStatus status,
        EvolutionArchiveInsertionResult? insertion, bool violates, ProgramExperienceOutcome expected)
    {
        var store = new ProgramExperienceStore();
        var recorder = new ProgramExperienceRecorder(new ProgramExperienceBinding(store, "run", "task", "v1"));
        await recorder.OnEventAsync(Evaluated("x = 1\n", "cache results", status, insertion, violates ? new[] { 1.0 } : null));
        ProgramExperienceRecord lesson = Assert.Single(store.Retrieve(new ProgramExperienceQuery("run", "task", "v1", 10_000, false)).Selected);
        Assert.Equal(expected, lesson.Outcome);
        Assert.Equal("cache results", lesson.Hypothesis);
        Assert.Equal(ProgramEvidencePartition.Search, lesson.Partition);
    }

    [Fact]
    public async Task Other_events_and_cancelled_evaluations_are_not_lessons()
    {
        var store = new ProgramExperienceStore();
        var recorder = new ProgramExperienceRecorder(new ProgramExperienceBinding(store, "run", "task", "v1"));
        await recorder.OnEventAsync(Evaluated("x = 1\n", null, EvolutionEvaluationStatus.Canceled, null));
        await recorder.OnEventAsync(new EvolutionEvent<ProgramGenome>(EvolutionEventKind.Checkpointed, 9));
        Assert.Equal(0, store.Count);
    }
}
