using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-60: OpenEvolve applies results in completion order, so one seed does not replay a run once it has more than one
/// worker. Here the seed alone must decide the result, whatever the worker count and whichever evaluation finishes first.
/// </summary>
public sealed class ParallelReplayDeterminismTests
{
    private static async Task<string> RunAsync(int workers, EvolutionDispatchMode dispatch, ulong seed = 20260928,
        List<double>? completions = null)
    {
        var builder = new EvolutionSearchSpaceBuilder();
        for (int i = 0; i < 3; i++) builder.Add(EvolutionParameter.Real("x" + i, -5, 5));
        EvolutionSearchSpace space = builder.Build();
        // Delays vary per genome, so evaluations started together complete in a different order from the one they began in.
        var task = new EvolutionSearchTask(space, "replay", "v1", "delay-v1", async (genome, _, cancellation) =>
        {
            double x = genome.Number("x0");
            await Task.Delay(TimeSpan.FromMilliseconds(1 + Math.Abs(x * 7) % 9), cancellation).ConfigureAwait(false);
            if (completions is not null) lock (completions) completions.Add(x);
            return EvolutionTaskResult.Completed(-genome.Values.Values.Sum(v => v.Number * v.Number),
                new Dictionary<string, double> { ["x"] = x });
        });
        var options = new EvolutionEngineOptions
        {
            RunId = "replay",
            Seed = seed,
            MaxEvaluationAttempts = 96,
            MaxProposals = 192,
            MaxGenerations = 96,
            ProposalBatchSize = 8,
            MaxDegreeOfParallelism = workers,
            IslandCount = 2,
            MigrationInterval = 0,
            CheckpointInterval = 0,
            Dispatch = dispatch
        };
        var engine = new EvolutionEngine<EvolutionSearchGenome>(task, new SearchSpaceRestart(space),
            _ => new MapElitesArchive<EvolutionSearchGenome>(new[] { new EvolutionDescriptorDefinition("x", -5, 5, 16) }), options);
        EvolutionSearchGenome[] seeds = Enumerable.Range(0, 8).Select(i => space.Sample(StableRandom.CreateStream(7, (ulong)i))).ToArray();
        EvolutionRunResult<EvolutionSearchGenome> result = await engine.RunAsync(seeds);
        return result.StateHash;
    }

    [Theory]
    [InlineData(EvolutionDispatchMode.Batch)]
    [InlineData(EvolutionDispatchMode.Pipeline)]
    public async Task The_seed_alone_decides_the_run_whatever_the_worker_count(EvolutionDispatchMode dispatch)
    {
        string single = await RunAsync(1, dispatch);
        var completions = new List<double>();
        Assert.Equal(single, await RunAsync(4, dispatch, completions: completions));
        Assert.Equal(single, await RunAsync(8, dispatch));
        // Not vacuous: the seed matters, and with 4 workers completions really arrive out of their start order.
        Assert.NotEqual(single, await RunAsync(4, dispatch, seed: 1));
        var sequential = new List<double>();
        await RunAsync(1, dispatch, completions: sequential);
        Assert.Equal(sequential.Count, completions.Count);
        Assert.NotEqual(sequential, completions);
    }
}
