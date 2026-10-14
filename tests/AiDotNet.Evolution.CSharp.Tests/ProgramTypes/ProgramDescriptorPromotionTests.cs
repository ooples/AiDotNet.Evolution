using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ProgramTypes;

/// <summary>V1-55: OpenEvolve's custom-metric feature dimensions, as archive descriptors.</summary>
public sealed class ProgramDescriptorPromotionTests
{
    private static readonly EvolutionEvaluationContext Context = new(0, 1234UL, 7UL, 1);

    private static EvolutionCandidate<ProgramGenome> Candidate(ProgramGenome genome) =>
        new(0, new EvolutionCanonicalGenome<ProgramGenome>(genome, genome.Id), new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL));

    private static ProgramEvolutionTask Task(ProgramTaskOptions options, IReadOnlyDictionary<string, double> metrics,
        IReadOnlyDictionary<string, double>? descriptors = null) =>
        new(new DelegateProgramFitnessEvaluator((_, _, _) => new ValueTask<EvolutionTaskResult>(new EvolutionTaskResult(
                EvolutionEvaluationStatus.Completed, 0.75, EvolutionOptimizationDirection.Maximize,
                descriptors ?? new Dictionary<string, double>(StringComparer.Ordinal), metrics: metrics)), "fake", "fake-v1"),
            null, options);

    [Fact]
    public async System.Threading.Tasks.Task Named_metrics_become_descriptors()
    {
        var options = new ProgramTaskOptions { MetricDescriptors = { "runtime_ms" } };
        EvolutionTaskResult result = await Task(options, new Dictionary<string, double> { ["runtime_ms"] = 12.5, ["other"] = 3 })
            .EvaluateAsync(Candidate(new ProgramGenome("x")), Context);
        Assert.Equal(12.5, result.Descriptors["runtime_ms"]);
        Assert.False(result.Descriptors.ContainsKey("other"));
    }

    [Fact]
    public async System.Threading.Tasks.Task Named_metrics_become_descriptors_under_proposal_options_too()
    {
        // Every test here used the base options, and the CLI uses the derived ProgramProposalOptions, whose
        // hand-listed Clone override dropped MetricDescriptors. ProgramEvolutionTask snapshots its options
        // through Clone, so the promotion list was empty by the first evaluation while the archive still
        // demanded those coordinates: every candidate was refused with descriptor_missing and an OpenEvolve
        // config declaring any non-reserved feature dimension ended with no candidates after one evaluation.
        var options = new ProgramProposalOptions { MetricDescriptors = { "runtime_ms" } };
        EvolutionTaskResult result = await Task(options, new Dictionary<string, double> { ["runtime_ms"] = 12.5, ["other"] = 3 })
            .EvaluateAsync(Candidate(new ProgramGenome("x")), Context);
        Assert.Equal(12.5, result.Descriptors["runtime_ms"]);
        Assert.False(result.Descriptors.ContainsKey("other"));
    }

    [Fact]
    public async System.Threading.Tasks.Task An_evaluator_descriptor_of_the_same_name_wins()
    {
        var options = new ProgramTaskOptions { MetricDescriptors = { "runtime_ms" } };
        EvolutionTaskResult result = await Task(options, new Dictionary<string, double> { ["runtime_ms"] = 12.5 },
            new Dictionary<string, double> { ["runtime_ms"] = 9.0 }).EvaluateAsync(Candidate(new ProgramGenome("x")), Context);
        Assert.Equal(9.0, result.Descriptors["runtime_ms"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task A_missing_descriptor_metric_is_refused_not_defaulted()
    {
        // Still refused, and the name is still reported - but as a failed evaluation rather than a thrown
        // InvalidDataException. The engine converts a thrown exception into "evaluator_exception: Evaluator
        // threw InvalidDataException", so the dimension's name did not survive the one path a reader reads.
        var options = new ProgramTaskOptions { MetricDescriptors = { "runtime_ms" } };
        EvolutionTaskResult result = await Task(options, new Dictionary<string, double>())
            .EvaluateAsync(Candidate(new ProgramGenome("x")), Context);

        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.False(result.Descriptors.ContainsKey("runtime_ms"));
        EvolutionDiagnostic diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("descriptor_metric_missing", diagnostic.Code);
        Assert.Equal("runtime_ms", diagnostic.Data["descriptor"]);
        Assert.False(diagnostic.IsRedacted);
        // There is no companion test for a non-finite metric: EvolutionTaskResult.CopyNamedValues refuses
        // one at construction ("Metric 'runtime_ms' must be finite"), so an evaluator cannot report it and
        // the task's IsFinite check is defence in depth rather than a reachable branch.
    }

    [Fact]
    public void Descriptor_names_are_validated_and_enter_the_task_identity()
    {
        Assert.Throws<ArgumentException>(() => new ProgramTaskOptions { MetricDescriptors = { " " } }.Validate());
        Assert.Throws<ArgumentException>(() => new ProgramTaskOptions { MetricDescriptors = { "a", "a" } }.Validate());
        var metrics = new Dictionary<string, double> { ["a"] = 1 };
        string plain = Task(new ProgramTaskOptions(), metrics).VersionHash;
        Assert.NotEqual(plain, Task(new ProgramTaskOptions { MetricDescriptors = { "a" } }, metrics).VersionHash);
        Assert.Equal(plain, Task(new ProgramTaskOptions(), metrics).VersionHash);
    }
}
