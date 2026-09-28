using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>
/// V1-83 (#185): the engine-level defect classes reproduced in OpenEvolve 0.3.2. D1, D3, D4 and D7 are archive-level and
/// live in <see cref="ArchiveDefectClassTests"/>; D6 is the sandbox's and lives in the program tests; D8 is the option
/// coverage test. Each asserts ours cannot exhibit the defect.
/// </summary>
public sealed class EngineDefectClassTests
{
    [Fact]
    public async Task D2_a_program_rejected_as_not_novel_leaves_nothing_behind()
    {
        // OpenEvolve adds a program its novelty check rejects to the database anyway. Here seeds 2 and 3 are within the
        // threshold of seed 1, so they are rejected before evaluation and must leave no trace anywhere a program can live.
        var options = new EvolutionEngineOptions
        {
            RunId = "d2",
            Seed = 5,
            MaxEvaluationAttempts = 10,
            MaxProposals = 3,
            MaxGenerations = 10,
            ProposalBatchSize = 1,
            MaxDegreeOfParallelism = 1,
            MigrationInterval = 0,
            CheckpointInterval = 1,
            NoveltyDistanceThreshold = 3,
            GlobalEliteCount = 10,
            HistorySize = 10
        };
        var task = new SyntheticEvolutionTask();
        var store = new InMemoryEvolutionCheckpointStore();
        EvolutionRunResult<TestGenome> result = await new EvolutionEngine<TestGenome>(task, new IncrementVariation(),
            _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp) }),
            options, checkpointStore: store, genomeCodec: new TestGenomeCodec(), genomeDistance: new AbsoluteGenomeDistance())
            .RunAsync(new[] { new TestGenome(1), new TestGenome(2), new TestGenome(3) });

        Assert.Equal(2, result.Counters.StatusCounts[EvolutionEvaluationStatus.Rejected]);
        Assert.Equal(1, task.Calls); // never evaluated
        string[] rejected = { "2", "3" };
        Assert.DoesNotContain(result.Islands.SelectMany(island => island.Entries), entry => rejected.Contains(entry.Evaluation.GenomeId));
        Assert.DoesNotContain(result.GlobalElites, record => rejected.Contains(record.Entry.Evaluation.GenomeId));
        Assert.Empty(result.PendingArtifacts);
        // Positive control: the program that was evaluated does appear in each place, so an absence above is meaningful.
        Assert.Contains(result.GlobalElites, record => record.Entry.Evaluation.GenomeId == "1");
        EvolutionCheckpoint checkpoint = await store.LoadLatestAsync("d2") ?? throw new InvalidOperationException("no checkpoint");
        using JsonDocument state = JsonDocument.Parse(checkpoint.Payload);
        // Not remembered as seen, so the same program proposed later is judged again rather than written off as a duplicate.
        JsonElement[] seen = state.RootElement.GetProperty("SeenGenomeIds").EnumerateArray().ToArray();
        Assert.Contains(seen, id => id.GetString() == "1");
        Assert.DoesNotContain(seen, id => rejected.Contains(id.GetString()));
        JsonElement[] history = state.RootElement.GetProperty("IslandHistories").EnumerateArray().SelectMany(island => island.EnumerateArray()).ToArray();
        Assert.Contains(history, entry => entry.GetProperty("GenomeId").GetString() == "1");
        Assert.DoesNotContain(history, entry => rejected.Contains(entry.GetProperty("GenomeId").GetString()));
    }

    [Fact]
    public async Task D5_the_result_does_not_depend_on_which_worker_finishes_first()
    {
        // OpenEvolve commits results as workers finish, so the same seed gives different runs. Here the evaluator's delay
        // is reversed between two runs, so evaluations finish in opposite orders, and the state must be identical.
        async Task<string> Run(Func<int, int> delayMs, EvolutionExecutionMode mode = EvolutionExecutionMode.Deterministic)
        {
            var options = new EvolutionEngineOptions
            {
                RunId = "d5",
                Seed = 13,
                MaxEvaluationAttempts = 40,
                MaxProposals = 80,
                MaxGenerations = 80,
                ProposalBatchSize = 8,
                MaxDegreeOfParallelism = 4,
                MigrationInterval = 0,
                CheckpointInterval = 0,
                ExecutionMode = mode,
                // Continuous dispatch plans each proposal from the archive as it stands, so completion order could reach
                // what later proposals see; batch dispatch would hide order by committing whole batches.
                Dispatch = EvolutionDispatchMode.Continuous,
                MaxInFlight = 4
            };
            var engine = new EvolutionEngine<TestGenome>(new OrderedDelayTask(delayMs), new IncrementVariation(),
                _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp) }),
                options);
            return (await engine.RunAsync(Enumerable.Range(0, 8).Select(i => new TestGenome(i * 5)).ToArray())).StateHash;
        }

        string fastFirst = await Run(value => 1 + value % 7 * 3);
        string slowFirst = await Run(value => 1 + (6 - value % 7) * 3);
        Assert.Equal(fastFirst, slowFirst);
        // Control: committing in completion order, as OpenEvolve does, the two schedules do give different runs, so the
        // schedules really reorder completions and the equality above is the deterministic mode's doing.
        Assert.NotEqual(await Run(value => 1 + value % 7 * 3, EvolutionExecutionMode.Opportunistic),
            await Run(value => 1 + (6 - value % 7) * 3, EvolutionExecutionMode.Opportunistic));
    }

    // Completes after a delay that depends only on the genome, so a schedule changes completion order and nothing else.
    private sealed class OrderedDelayTask(Func<int, int> delayMs) : IEvolutionTask<TestGenome>
    {
        public string Id => "ordered-delay";
        public string VersionHash => "ordered-delay-v1";
        public string EvaluatorVersionHash => "ordered-delay-evaluator-v1";

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            await Task.Delay(delayMs(value), cancellationToken);
            return EvolutionTaskResult.Completed(value % 17, new Dictionary<string, double> { ["x"] = Math.Min(100, value) });
        }
    }
}
