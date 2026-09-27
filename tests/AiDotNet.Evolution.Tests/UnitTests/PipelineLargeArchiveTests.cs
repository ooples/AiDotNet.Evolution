using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-20: pipeline dispatch fingerprints each source archive per wave. That fingerprint went through
/// EvolutionHash.Combine, which accepts at most 4096 components, and passed two per elite, so any island holding 2048 or
/// more elites made every pipeline proposal throw.
/// </summary>
public sealed class PipelineLargeArchiveTests
{
    // One cell per value: the descriptor is the genome value itself, unclamped.
    private sealed class OneCellPerValueTask : IEvolutionTask<TestGenome>
    {
        public string Id => "one-cell-per-value";
        public string VersionHash => "one-cell-per-value-v1";
        public string EvaluatorVersionHash => "one-cell-per-value-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(CultureInfo.InvariantCulture)));

        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default) =>
            new(EvolutionTaskResult.Completed(candidate.CanonicalGenome.Genome.Value,
                new Dictionary<string, double> { ["x"] = candidate.CanonicalGenome.Genome.Value }));
    }

    [Theory]
    [InlineData(2047)] // the largest archive the old fingerprint could hash: 2 + 2 * 2047 = 4096 components
    [InlineData(2600)]
    public async Task Pipeline_dispatch_proposes_from_an_archive_of_any_size(int seeds)
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "pipeline-large-archive",
            Seed = 5,
            Dispatch = EvolutionDispatchMode.Pipeline,
            MaxProposals = seeds + 8,
            MaxEvaluationAttempts = seeds + 8,
            MaxGenerations = seeds + 8,
            MaxDegreeOfParallelism = 2,
            IslandCount = 1,
            CheckpointInterval = 0,
            MigrationInterval = 0,
            Pipeline = new EvolutionPipelineOptions { WaveSize = 4, MaxProposalConcurrency = 2, ProposalQueueCapacity = 2, EvaluationQueueCapacity = 2 }
        };
        var engine = new EvolutionEngine<TestGenome>(new OneCellPerValueTask(), new IncrementVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 10_000, 10_000) }), options);

        EvolutionRunResult<TestGenome> result = await engine.RunAsync(Enumerable.Range(0, seeds).Select(value => new TestGenome(value * 3)));

        Assert.Empty(result.RetainedFailures);
        Assert.True(result.Islands[0].Entries.Count >= seeds, "every seed occupies its own cell");
        Assert.True(result.Counters.Proposals > seeds, "proposals ran against the full archive");
    }
}
