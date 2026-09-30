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
        // OpenEvolve commits results as workers finish, so the same seed gives different runs. Here a gate releases the
        // in-flight evaluations in an order the test chooses, reversed between two runs, and the state must be identical.
        async Task<(string Hash, IReadOnlyList<int> Order, IReadOnlyList<int> Observed)> Run(Func<int, int> priority,
            EvolutionExecutionMode mode = EvolutionExecutionMode.Deterministic)
        {
            const int inFlight = 4;
            var options = new EvolutionEngineOptions
            {
                RunId = "d5",
                Seed = 13,
                MaxEvaluationAttempts = 40,
                MaxProposals = 80,
                MaxGenerations = 80,
                ProposalBatchSize = 8,
                MaxDegreeOfParallelism = inFlight,
                MigrationInterval = 0,
                CheckpointInterval = 0,
                ExecutionMode = mode,
                // Continuous dispatch plans each proposal from the archive as it stands, so completion order could reach
                // what later proposals see; batch dispatch would hide order by committing whole batches.
                Dispatch = EvolutionDispatchMode.Continuous,
                MaxInFlight = inFlight
            };
            var task = new GatedOrderTask(priority, inFlight);
            var observer = new EvaluatedOrder();
            var engine = new EvolutionEngine<TestGenome>(task, new IncrementVariation(),
                _ => new MapElitesArchive<TestGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 10, EvolutionOutOfRangePolicy.Clamp) }),
                options, observer: observer);
            string hash = (await engine.RunAsync(Enumerable.Range(0, 8).Select(i => new TestGenome(i * 5)).ToArray())).StateHash;
            return (hash, task.CompletionOrder, observer.Values);
        }

        Func<int, int> lowFirst = value => value % 7;
        Func<int, int> highFirst = value => 6 - value % 7;
        var (lowHash, lowOrder, lowObserved) = await Run(lowFirst);
        var (highHash, highOrder, highObserved) = await Run(highFirst);
        // The schedules really do reorder completions, observed rather than assumed from timing.
        Assert.NotEqual(lowOrder, highOrder);
        Assert.Equal(lowHash, highHash);
        // What the engine reports is identical too: deterministic mode commits in identifier order.
        Assert.Equal(lowObserved, highObserved);
        // Control: committing in completion order, as OpenEvolve does, the two schedules do give different runs, so the
        // equality above is the deterministic mode's doing.
        var (lowOpportunistic, lowOpportunisticOrder, lowOpportunisticObserved) = await Run(lowFirst, EvolutionExecutionMode.Opportunistic);
        var (highOpportunistic, highOpportunisticOrder, highOpportunisticObserved) = await Run(highFirst, EvolutionExecutionMode.Opportunistic);
        Assert.NotEqual(lowOpportunisticOrder, highOpportunisticOrder);
        Assert.NotEqual(lowOpportunistic, highOpportunistic);
        // Opportunistic mode commits in the order the engine sees evaluations finish, so its Evaluated events are the
        // engine's own record of completion order, not the gate's: the two schedules must reorder that as well.
        Assert.NotEqual(lowOpportunisticObserved, highOpportunisticObserved);
    }

    // Records the genome of every Evaluated event, in the order the engine emits them.
    private sealed class EvaluatedOrder : IEvolutionObserver<TestGenome>
    {
        private readonly List<int> _values = new();

        public IReadOnlyList<int> Values
        {
            get { lock (_values) return _values.ToArray(); }
        }

        public ValueTask OnEventAsync(EvolutionEvent<TestGenome> evolutionEvent, CancellationToken cancellationToken = default)
        {
            if (evolutionEvent.Kind == EvolutionEventKind.Evaluated && evolutionEvent.Candidate is { } candidate)
                lock (_values) _values.Add(candidate.CanonicalGenome.Genome.Value);
            return default;
        }
    }

    // Holds each evaluation until the window is full, then releases the waiting one the schedule ranks first, so the
    // completion order is chosen by the test rather than by timer resolution. Near the end of a run the window cannot
    // fill; after a quiet interval the gate releases anyway, still in schedule order, so it cannot deadlock.
    private sealed class GatedOrderTask(Func<int, int> priority, int window) : IEvolutionTask<TestGenome>
    {
        private static readonly TimeSpan QuietInterval = TimeSpan.FromMilliseconds(100);
        private readonly object _gate = new();
        private readonly List<(int Value, TaskCompletionSource<bool> Release)> _waiting = new();
        private readonly List<int> _order = new();

        public string Id => "gated-order";
        public string VersionHash => "gated-order-v1";
        public string EvaluatorVersionHash => "gated-order-evaluator-v1";

        public IReadOnlyList<int> CompletionOrder
        {
            get { lock (_gate) return _order.ToArray(); }
        }

        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate, EvolutionEvaluationContext context,
            CancellationToken cancellationToken = default)
        {
            int value = candidate.CanonicalGenome.Genome.Value;
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _waiting.Add((value, release));
                if (_waiting.Count >= window) ReleaseFirst();
            }

            while (!release.Task.IsCompleted)
            {
                Task finished = await Task.WhenAny(release.Task, Task.Delay(QuietInterval, cancellationToken));
                if (finished == release.Task) break;
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    if (!release.Task.IsCompleted) ReleaseFirst();
                }
            }

            return EvolutionTaskResult.Completed(value % 17, new Dictionary<string, double> { ["x"] = Math.Min(100, value) });
        }

        // Caller holds _gate. Ties go to the smaller value, then to arrival order, so the schedule alone decides.
        private void ReleaseFirst()
        {
            int best = 0;
            for (int i = 1; i < _waiting.Count; i++)
            {
                int rank = priority(_waiting[i].Value), bestRank = priority(_waiting[best].Value);
                if (rank < bestRank || (rank == bestRank && _waiting[i].Value < _waiting[best].Value)) best = i;
            }

            (int value, TaskCompletionSource<bool> release) = _waiting[best];
            _waiting.RemoveAt(best);
            _order.Add(value);
            release.SetResult(true);
        }
    }
}