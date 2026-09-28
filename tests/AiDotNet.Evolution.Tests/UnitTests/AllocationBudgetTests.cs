using Xunit;

namespace AiDotNet.Evolution.Tests;

[CollectionDefinition(nameof(AllocationBudgetCollection), DisableParallelization = true)]
public sealed class AllocationBudgetCollection { }

/// <summary>
/// V1-71: the engine's allocation per evaluation on a null task. The committed figure is 19.3 KB on net10.0 (down from
/// 34.8 KB); the test fails if a change raises it more than 20%, so a regression is caught where it is introduced.
/// </summary>
[Collection(nameof(AllocationBudgetCollection))]
public sealed class AllocationBudgetTests
{
    private const double CommittedBytesPerEvaluation = 19_300;

    [Fact]
    public async Task A_null_evaluation_stays_within_its_allocation_budget()
    {
        var builder = new EvolutionSearchSpaceBuilder();
        for (int i = 0; i < 4; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
        EvolutionSearchSpace space = builder.Build();
        var task = new EvolutionSearchTask(space, "allocation", "v1", "null-v1", (genome, _, _) =>
            new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(-genome.Number("x0") * genome.Number("x0"),
                new Dictionary<string, double> { ["x"] = genome.Number("x0") }, costUnits: 1)));
        async Task<(long Bytes, long Evaluations)> Run(int budget)
        {
            var engine = new EvolutionEngine<EvolutionSearchGenome>(task, new SearchSpaceRestart(space),
                _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 64) }),
                new EvolutionEngineOptions
                {
                    RunId = "allocation",
                    Seed = 42,
                    MaxEvaluationAttempts = budget,
                    MaxProposals = budget * 2,
                    MaxGenerations = budget,
                    ProposalBatchSize = 8,
                    MaxDegreeOfParallelism = 1,
                    MigrationInterval = 0,
                    CheckpointInterval = 0
                });
            EvolutionSearchGenome[] seeds = Enumerable.Range(0, 8).Select(i => space.Sample(StableRandom.CreateStream(42, (ulong)i))).ToArray();
            long before = GC.GetTotalAllocatedBytes(precise: true);
            EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(seeds);
            return (GC.GetTotalAllocatedBytes(precise: true) - before, result.Counters.CompletedEvaluations);
        }

        await Run(500); // warm up the JIT and pools so the measured run reflects steady state
        var (bytes, evaluations) = await Run(4_000);
        double perEvaluation = (double)bytes / evaluations;
        Assert.True(perEvaluation <= CommittedBytesPerEvaluation * 1.2,
            $"Allocation per evaluation rose to {perEvaluation:F0} bytes, more than 20% above the committed {CommittedBytesPerEvaluation}.");
    }
}
