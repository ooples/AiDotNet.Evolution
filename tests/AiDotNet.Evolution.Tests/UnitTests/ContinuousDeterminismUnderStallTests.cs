using System.Globalization;
using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// A remote worker that dies holds its lease until it expires (V1-61, #174), so one evaluation can finish seconds after
/// everything around it. In deterministic mode that delay must not change the run. Before the admission bound in this
/// story, a 3 s stall on evaluation 5 let continuous dispatch plan past it and produced a different state hash.
/// </summary>
public sealed class ContinuousDeterminismUnderStallTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(5L)]
    public async Task A_long_stall_on_one_evaluation_does_not_change_a_deterministic_continuous_run(long stalledEvaluation)
    {
        string prompt = await Run(stalledEvaluation: -1);
        string stalled = await Run(stalledEvaluation);
        Assert.Equal(prompt, stalled);
    }

    private static async Task<string> Run(long stalledEvaluation)
    {
        var options = new EvolutionEngineOptions
        {
            RunId = "stall",
            Seed = 20260928,
            MaxProposals = 80,
            MaxEvaluationAttempts = 88,
            MaxGenerations = 80,
            ProposalBatchSize = 8,
            MaxDegreeOfParallelism = 8,
            MaxRetries = 0,
            CheckpointInterval = 0,
            MigrationInterval = 0,
            ExecutionMode = EvolutionExecutionMode.Deterministic,
            Dispatch = EvolutionDispatchMode.Continuous,
            MaxInFlight = 16,
            EvaluationTimeout = TimeSpan.FromMinutes(1)
        };
        var engine = new EvolutionEngine<int>(new StallTask(stalledEvaluation), new StepVariation(),
            _ => new MapElitesArchive<int>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10) }), options);
        return (await engine.RunAsync(new[] { 3, 17, 41, 58 })).StateHash;
    }

    private sealed class StallTask(long stalledEvaluation) : IEvolutionTask<int>
    {
        public string Id => "stall";
        public string VersionHash => "stall-v1";
        public string EvaluatorVersionHash => "stall-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<int>> CanonicalizeAsync(int genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<int>(genome, genome.ToString(CultureInfo.InvariantCulture)));

        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<int> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(context.EvaluationId == stalledEvaluation ? 3000 : 5, cancellationToken);
            int genome = candidate.CanonicalGenome.Genome;
            return EvolutionTaskResult.Completed(genome * 7919 % 1000 / 1000.0, new Dictionary<string, double> { ["x"] = genome % 100 });
        }
    }

    private sealed class StepVariation : IVariationOperator<int>
    {
        public string Id => "step";
        public string VersionHash => "step-v1";

        public ValueTask<int> ProposeAsync(EvolutionVariationContext<int> context, CancellationToken cancellationToken = default) =>
            new(context.Parent.Candidate.CanonicalGenome.Genome + 1 + context.Random.NextInt(0, 23));
    }
}
