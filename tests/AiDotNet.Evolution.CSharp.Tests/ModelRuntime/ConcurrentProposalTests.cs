using System.Globalization;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>V1-75: an LLM search can keep several model calls in flight without losing exact replay.</summary>
public sealed class ConcurrentProposalTests
{
    // Answers with a program derived from the prompt, after a delay that differs per call, so completions reorder.
    private sealed class JitteryModel(bool reverse) : IProgramChatClient
    {
        private int _inFlight, _calls;
        public int MaxInFlight;
        public string ModelId => "jittery";
        public async Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            int now = Interlocked.Increment(ref _inFlight);
            int seen;
            while ((seen = Volatile.Read(ref MaxInFlight)) < now && Interlocked.CompareExchange(ref MaxInFlight, now, seen) != seen) { }
            int seed = options?.Seed ?? 0;
            await Task.Delay(TimeSpan.FromMilliseconds(reverse ? 40 - seed % 37 : 3 + seed % 37), cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            // Content depends only on the prompt and the seed, never on timing, as a replayed model's would.
            int value = Math.Abs((messages[^1].Text.Length * 31 + seed) % 1000);
            return new ProgramChatResponse(ProgramChatMessage.Assistant("```python\nX = " + value.ToString(CultureInfo.InvariantCulture) + "\n```"));
        }
    }

    private static Task<(string StateHash, int MaxInFlight)> Run(int concurrency, bool reverse) =>
        Run(concurrency, reverse, EvolutionDispatchMode.Pipeline, maxProposals: 80, store: null, resume: false);

    private static async Task<(string StateHash, int MaxInFlight)> Run(int concurrency, bool reverse, EvolutionDispatchMode dispatch,
        int maxProposals, IEvolutionCheckpointStore? store, bool resume)
    {
        var model = new JitteryModel(reverse);
        var variation = new LlmProgramVariationOperator(model, new ProgramProposalOptions { Language = ProgramLanguage.Python },
            new LlmProgramVariationOptions { Mode = ProgramEvolutionMode.FullRewrite, MaxProposalRetries = 0, ConcurrentProposals = true });
        var task = new ProgramEvolutionTask(new DelegateProgramFitnessEvaluator((genome, _, _) =>
        {
            int x = int.Parse(genome.Source.Split('=')[1].Trim(), CultureInfo.InvariantCulture);
            return new ValueTask<EvolutionTaskResult>(EvolutionTaskResult.Completed(x, new Dictionary<string, double> { ["x"] = x % 100 }));
        }, "x-score", "x-score-v1"));
        var options = new EvolutionEngineOptions
        {
            RunId = "concurrent-proposals",
            Seed = 5,
            MaxEvaluationAttempts = 40,
            MaxProposals = maxProposals,
            MaxGenerations = 80,
            MaxDegreeOfParallelism = 4,
            MigrationInterval = 0,
            CheckpointInterval = store is null ? 0 : 4,
            Dispatch = dispatch,
            Resume = resume
        };
        options.Pipeline.MaxProposalConcurrency = concurrency;
        if (dispatch == EvolutionDispatchMode.Pipeline) options.Pipeline.WaveSize = 8;
        else options.MaxInFlight = 8;
        var engine = new EvolutionEngine<ProgramGenome>(task, variation,
            _ => new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 20) }), options,
            checkpointStore: store, genomeCodec: store is null ? null : new ProgramGenomeCodec());
        EvolutionRunResult<ProgramGenome> result = await engine.RunAsync(new[] { new ProgramGenome("X = 1\n", ProgramLanguage.Python) });
        return (result.StateHash, model.MaxInFlight);
    }

    [Fact]
    public async Task Several_model_calls_run_at_once_and_the_run_replays_exactly_whatever_order_they_finish_in()
    {
        // MaxProposalConcurrency is part of the pipeline's semantic identity, so serial and concurrent runs are different
        // runs by design. Replay means a concurrent run is identical however its model calls happen to finish.
        var serial = await Run(concurrency: 1, reverse: false);
        var concurrent = await Run(concurrency: 4, reverse: false);
        var reordered = await Run(concurrency: 4, reverse: true);
        var again = await Run(concurrency: 4, reverse: false);
        Assert.Equal(1, serial.MaxInFlight);
        Assert.True(concurrent.MaxInFlight > 1, "proposals never overlapped");
        Assert.True(reordered.MaxInFlight > 1, "proposals never overlapped");
        Assert.Equal(concurrent.StateHash, reordered.StateHash);
        Assert.Equal(concurrent.StateHash, again.StateHash);
    }

    [Fact]
    public async Task Continuous_dispatch_overlaps_model_calls_and_the_proposal_cap_changes_only_the_schedule()
    {
        // Continuous dispatch plans each proposal when the commit a window earlier lands, whatever the timing, and
        // starts model calls in planning order, so the number of overlapping calls is a budget setting: one call at a
        // time and four give the same run.
        var serial = await Run(1, false, EvolutionDispatchMode.Continuous, 80, null, false);
        var concurrent = await Run(4, false, EvolutionDispatchMode.Continuous, 80, null, false);
        var reordered = await Run(4, true, EvolutionDispatchMode.Continuous, 80, null, false);
        Assert.Equal(1, serial.MaxInFlight);
        Assert.True(concurrent.MaxInFlight > 1, "proposals never overlapped");
        Assert.True(reordered.MaxInFlight > 1, "proposals never overlapped");
        Assert.Equal(serial.StateHash, concurrent.StateHash);
        Assert.Equal(serial.StateHash, reordered.StateHash);
    }

    [Fact]
    public async Task A_continuous_run_with_overlapping_model_calls_resumes_to_the_uninterrupted_state()
    {
        // With one seed, a window of eight and a checkpoint every four commits, the first checkpoint drains the window
        // after eleven proposals (ids 0-10). A run capped at eleven proposals stops at exactly that checkpoint, so its
        // resume must reproduce the uninterrupted run. The drain covers outstanding model calls as well as
        // evaluations; a checkpoint written mid-call would record proposals whose outcome it never saw.
        var uninterrupted = await Run(4, false, EvolutionDispatchMode.Continuous, 80, new InMemoryEvolutionCheckpointStore(), false);
        var store = new InMemoryEvolutionCheckpointStore();
        await Run(4, true, EvolutionDispatchMode.Continuous, 11, store, false);
        var resumed = await Run(4, false, EvolutionDispatchMode.Continuous, 80, store, true);
        Assert.Equal(uninterrupted.StateHash, resumed.StateHash);
    }

    [Fact]
    public async Task Checkpointed_runs_with_overlapping_model_calls_agree_under_contention()
    {
        // Admission reads the cache, the duplicate set and the archive, which commits change. Admitting whenever a model
        // call returned let a duplicate be a cache hit in one run and a duplicate in the next; twelve runs at once make
        // that timing vary enough to show it. Each checkpoint drains the window, which exercises the refill path too.
        var runs = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            Run(4, index % 2 == 0, EvolutionDispatchMode.Continuous, 80, new InMemoryEvolutionCheckpointStore(), false))));
        Assert.Single(runs.Select(run => run.StateHash).Distinct());
    }

    [Fact]
    public void Concurrency_is_opt_in_and_changes_the_operator_identity_only_when_set()
    {
        var model = new JitteryModel(false);
        var off = new LlmProgramVariationOperator(model, new ProgramProposalOptions(), new LlmProgramVariationOptions());
        var on = new LlmProgramVariationOperator(model, new ProgramProposalOptions(), new LlmProgramVariationOptions { ConcurrentProposals = true });
        Assert.False(off.SupportsDeterministicConcurrency);
        Assert.True(on.SupportsDeterministicConcurrency);
        Assert.NotEqual(off.VersionHash, on.VersionHash);
        Assert.Equal(off.VersionHash, new LlmProgramVariationOperator(model, new ProgramProposalOptions(), new LlmProgramVariationOptions()).VersionHash);
    }
}
