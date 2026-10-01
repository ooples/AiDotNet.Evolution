using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// A run stopped by its evaluation budget and resumed with a larger budget must reach exactly the state an
/// uninterrupted run with the larger budget reaches, wherever the stop falls.
/// </summary>
public sealed class BudgetStopResumeTests
{
    private sealed class CyclingVariation : IVariationOperator<TestGenome>
    {
        public string Id => "cycling";
        public string VersionHash => "cycling-v1";

        public ValueTask<TestGenome> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default) =>
            new(new TestGenome((int)(context.Generation % 60) + 1));
    }

    public static TheoryData<EvolutionDispatchMode, int> Stops()
    {
        var data = new TheoryData<EvolutionDispatchMode, int>();
        foreach (EvolutionDispatchMode dispatch in new[] { EvolutionDispatchMode.Batch, EvolutionDispatchMode.Continuous, EvolutionDispatchMode.Pipeline })
            foreach (int stop in new[] { 9, 16, 28, 136 }) data.Add(dispatch, stop);
        return data;
    }

    [Theory]
    [MemberData(nameof(Stops))]
    public async Task A_resumed_run_with_a_raised_budget_matches_an_uninterrupted_one(EvolutionDispatchMode dispatch, int stopAfter)
    {
        const int budget = 200;
        var store = new InMemoryEvolutionCheckpointStore();
        await Engine(stopAfter, store, resume: false, dispatch).RunAsync(Seeds());
        EvolutionRunResult<TestGenome> resumed = await Engine(budget, store, resume: true, dispatch).RunAsync(Seeds());
        EvolutionRunResult<TestGenome> uninterrupted = await Engine(budget, new InMemoryEvolutionCheckpointStore(), resume: false, dispatch).RunAsync(Seeds());

        Assert.Equal(uninterrupted.Counters.CompletedEvaluations, resumed.Counters.CompletedEvaluations);
        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
    }


    [Theory]
    [MemberData(nameof(Stops))]
    public async Task A_resume_that_stops_at_once_reports_the_first_runs_work_and_still_resumes_exactly(EvolutionDispatchMode dispatch, int stopAfter)
    {
        var store = new InMemoryEvolutionCheckpointStore();
        EvolutionRunResult<TestGenome> first = await Engine(stopAfter, store, resume: false, dispatch).RunAsync(Seeds());

        // The same budget, then a generation limit below what was spent: neither run may evaluate anything, and both
        // must report exactly the work the first run did, including a batch the budget cut short.
        foreach (int maxGenerations in new[] { 1000, 1 })
        {
            var idle = new SyntheticEvolutionTask();
            EvolutionRunResult<TestGenome> held = await Engine(stopAfter, store, resume: true, dispatch, idle, maxGenerations).RunAsync(Seeds());
            Assert.Equal(0, idle.Calls);
            Assert.Equal(first.Counters.CompletedEvaluations, held.Counters.CompletedEvaluations);
            Assert.Equal(first.Counters.Proposals, held.Counters.Proposals);
            Assert.Equal(first.StateHash, held.StateHash);
        }

        // Those resumes must not have turned the cut-short batch into a boundary.
        EvolutionRunResult<TestGenome> resumed = await Engine(200, store, resume: true, dispatch).RunAsync(Seeds());
        EvolutionRunResult<TestGenome> uninterrupted = await Engine(200, new InMemoryEvolutionCheckpointStore(), resume: false, dispatch).RunAsync(Seeds());
        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
    }

    private static TestGenome[] Seeds() => new[] { new TestGenome(0) };

    private static EvolutionEngine<TestGenome> Engine(int budget, IEvolutionCheckpointStore store, bool resume,
        EvolutionDispatchMode dispatch = EvolutionDispatchMode.Batch, SyntheticEvolutionTask? task = null, int maxGenerations = 1000) =>
        new(task ?? new SyntheticEvolutionTask(), new CyclingVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 4) }),
            new EvolutionEngineOptions
            {
                RunId = "budget-resume",
                Seed = 11,
                MaxEvaluationAttempts = budget,
                MaxProposals = 1000,
                MaxGenerations = maxGenerations,
                ProposalBatchSize = 4,
                MaxDegreeOfParallelism = 1,
                MigrationInterval = 0,
                CheckpointInterval = 4,
                Resume = resume,
                Dispatch = dispatch
            }, checkpointStore: store, genomeCodec: new TestGenomeCodec());
}