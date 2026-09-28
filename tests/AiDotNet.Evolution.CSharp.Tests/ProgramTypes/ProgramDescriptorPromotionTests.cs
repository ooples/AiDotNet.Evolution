using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ProgramTypes;

/// <summary>V1-55: OpenEvolve's "score" and custom-metric feature dimensions, as archive descriptors.</summary>
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
    public async System.Threading.Tasks.Task Named_metrics_and_the_score_become_descriptors()
    {
        var options = new ProgramTaskOptions { MetricDescriptors = { "runtime_ms" }, QualityDescriptorName = "score" };
        EvolutionTaskResult result = await Task(options, new Dictionary<string, double> { ["runtime_ms"] = 12.5, ["other"] = 3 })
            .EvaluateAsync(Candidate(new ProgramGenome("x")), Context);
        Assert.Equal(12.5, result.Descriptors["runtime_ms"]);
        Assert.Equal(0.75, result.Descriptors["score"]);
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
        var options = new ProgramTaskOptions { MetricDescriptors = { "runtime_ms" } };
        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            Task(options, new Dictionary<string, double>()).EvaluateAsync(Candidate(new ProgramGenome("x")), Context).AsTask());
        Assert.Contains("runtime_ms", error.Message);
    }

    [Fact]
    public void Descriptor_names_are_validated_and_enter_the_task_identity()
    {
        Assert.Throws<ArgumentException>(() => new ProgramTaskOptions { MetricDescriptors = { " " } }.Validate());
        Assert.Throws<ArgumentException>(() => new ProgramTaskOptions { MetricDescriptors = { "a", "a" } }.Validate());
        Assert.Throws<ArgumentException>(() => new ProgramTaskOptions { MetricDescriptors = { "score" }, QualityDescriptorName = "score" }.Validate());
        var metrics = new Dictionary<string, double> { ["a"] = 1 };
        string plain = Task(new ProgramTaskOptions(), metrics).VersionHash;
        Assert.NotEqual(plain, Task(new ProgramTaskOptions { MetricDescriptors = { "a" } }, metrics).VersionHash);
        Assert.NotEqual(plain, Task(new ProgramTaskOptions { QualityDescriptorName = "score" }, metrics).VersionHash);
        Assert.Equal(plain, Task(new ProgramTaskOptions(), metrics).VersionHash);
    }
}
