using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Programs.Experience;
using AiDotNet.Evolution.Programs.Novelty;
using AiDotNet.Evolution.CSharp.Tests.ModelRuntime;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.Novelty;

/// <summary>US-18: similarity is a heuristic, and experience is bounded, budgeted and never leaks final-test data.</summary>
public sealed class ExperienceAndAdvisoryNoveltyTests
{
    private static ProgramGenome A() => new("total = 0\nfor x in xs:\n    total += x\n");
    private static ProgramGenome B() => new(A().Source + "#\n");
    private static EvolutionEvaluationContext Context() => new(1, 99UL, 7UL, 1);
    private static ProgramNoveltyPolicy NotNovel() =>
        new(embeddingClient: new ConstantEmbeddingClient(), judge: new ScriptedNoveltyJudge(ProgramNoveltyVerdict.NotNovel));

    [Fact]
    public async Task Advisory_novelty_measures_a_similar_candidate_instead_of_discarding_it()
    {
        var reject = new NoveltyGatingProgramFitnessEvaluator(new RecordingProgramFitnessEvaluator(), NotNovel());
        var advise = new NoveltyGatingProgramFitnessEvaluator(new RecordingProgramFitnessEvaluator(), NotNovel(),
            enforcement: ProgramNoveltyEnforcement.Advise);
        reject.Remember(A()); advise.Remember(A());
        EvolutionTaskResult rejected = await reject.EvaluateAsync(B(), Context());
        EvolutionTaskResult advised = await advise.EvaluateAsync(B(), Context());
        Assert.Equal(EvolutionEvaluationStatus.Rejected, rejected.Status);
        Assert.NotEqual(EvolutionEvaluationStatus.Rejected, advised.Status);
        Assert.Contains(advised.Diagnostics, d => d.Code == NoveltyGatingProgramFitnessEvaluator.SimilarityCode);
        Assert.DoesNotContain(advised.Diagnostics, d => d.Code == NoveltyGatingProgramFitnessEvaluator.RejectionCode);
        Assert.Equal(1, advise.AdvisedCount);
        Assert.NotEqual(reject.VersionHash, advise.VersionHash);
        Assert.Equal(reject.VersionHash, new NoveltyGatingProgramFitnessEvaluator(new RecordingProgramFitnessEvaluator(), NotNovel()).VersionHash);
    }

    private static ProgramExperienceRecord Record(string run, string task, string version, string source, ProgramExperienceOutcome outcome,
        long sequence, ProgramEvidencePartition partition = ProgramEvidencePartition.Search) =>
        new(run, task, version, "try " + source, source, outcome, partition, 1.5, new[] { "diag " + source }, sequence);

    [Fact]
    public void Final_test_evidence_can_never_enter_or_be_restored()
    {
        var store = new ProgramExperienceStore();
        Assert.Throws<ArgumentException>(() => store.Add(Record("r", "t", "v1", "s", ProgramExperienceOutcome.Improved, 1, ProgramEvidencePartition.FinalTest)));
        Assert.Equal(0, store.Count);
        string forged = "[{\"RunId\":\"r\",\"TaskIdentity\":\"t\",\"TaskVersion\":\"v1\",\"Hypothesis\":\"h\",\"SourceHash\":\"s\",\"Outcome\":0,\"Partition\":2,\"Quality\":1,\"Diagnostics\":[],\"Sequence\":1}]";
        Assert.Throws<InvalidDataException>(() => store.RestoreState(forged));
    }

    [Fact]
    public void Retrieval_never_crosses_task_or_version_and_crosses_runs_only_on_opt_in()
    {
        var store = new ProgramExperienceStore();
        store.Add(Record("run-1", "task", "v1", "a", ProgramExperienceOutcome.Improved, 1));
        store.Add(Record("run-2", "task", "v1", "b", ProgramExperienceOutcome.Improved, 2));
        store.Add(Record("run-1", "task", "v2", "c", ProgramExperienceOutcome.Improved, 3));
        store.Add(Record("run-1", "other", "v1", "d", ProgramExperienceOutcome.Improved, 4));
        var own = store.Retrieve(new("run-1", "task", "v1", 10_000, IncludeOtherRuns: false));
        Assert.Equal(new[] { "a" }, own.Selected.Select(r => r.SourceHash));
        Assert.Equal(3, own.ExcludedIncompatible);
        var shared = store.Retrieve(new("run-1", "task", "v1", 10_000, IncludeOtherRuns: true));
        Assert.Equal(new[] { "a", "b" }, shared.Selected.Select(r => r.SourceHash).OrderBy(s => s));
    }

    [Fact]
    public void Retrieval_fits_the_budget_alternates_outcomes_and_skips_repeated_sources()
    {
        var store = new ProgramExperienceStore();
        for (int i = 0; i < 20; i++)
            store.Add(Record("r", "t", "v1", "s" + (i % 12), i % 2 == 0 ? ProgramExperienceOutcome.Improved : ProgramExperienceOutcome.Invalid, i));
        var small = store.Retrieve(new("r", "t", "v1", 120, false));
        Assert.True(small.Context.Length <= 120);
        Assert.NotEmpty(small.Selected);
        var large = store.Retrieve(new("r", "t", "v1", 100_000, false));
        Assert.Equal(large.Selected.Count, large.Selected.Select(r => r.SourceHash).Distinct().Count());
        Assert.Contains(large.Selected.Take(2), r => r.Outcome == ProgramExperienceOutcome.Improved);
        Assert.Contains(large.Selected.Take(2), r => r.Outcome == ProgramExperienceOutcome.Invalid);
        Assert.True(large.Selected[0].Sequence >= large.Selected[2].Sequence, "newest first within an outcome");
    }

    [Fact]
    public void Retention_is_bounded_and_state_round_trips()
    {
        var store = new ProgramExperienceStore(capacity: 3);
        for (int i = 0; i < 5; i++) store.Add(Record("r", "t", "v1", "s" + i, ProgramExperienceOutcome.NotImproved, i));
        Assert.Equal(3, store.Count);
        var restored = new ProgramExperienceStore(capacity: 3);
        restored.RestoreState(store.CaptureState());
        Assert.Equal(store.CaptureState(), restored.CaptureState());
        Assert.Equal(new[] { "s2", "s3", "s4" }, restored.Retrieve(new("r", "t", "v1", 10_000, false)).Selected.Select(r => r.SourceHash).OrderBy(s => s));
        Assert.Throws<InvalidDataException>(() => new ProgramExperienceStore(capacity: 2).RestoreState(store.CaptureState()));
    }
}