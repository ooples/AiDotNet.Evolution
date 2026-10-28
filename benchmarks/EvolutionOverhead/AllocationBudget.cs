using System.Text.Json;
using AiDotNet.Evolution;

/// <summary>
/// The workload of <c>AllocationBudgetTests</c> (a null task over four real parameters, a 64-bin MAP-Elites archive,
/// batches of 8, one worker, no checkpoints), run outside a test host so it can be measured over as many evaluations
/// as a criterion asks for. Keep the two in step: the test's committed figure is justified by this harness's output.
/// </summary>
internal static class AllocationBudget
{
    public static async Task<int> RunAsync(int evaluations)
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

        await Run(500);
        var (bytes, completed) = await Run(evaluations);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Evaluations = completed,
            AllocatedBytes = bytes,
            BytesPerEvaluation = (double)bytes / completed
        }));
        return completed >= evaluations ? 0 : 1;
    }
}
