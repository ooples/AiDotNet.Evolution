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

    private static async Task<(string StateHash, int MaxInFlight)> Run(int concurrency, bool reverse)
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
            RunId = "concurrent-proposals", Seed = 5, MaxEvaluationAttempts = 40, MaxProposals = 80, MaxGenerations = 80,
            MaxDegreeOfParallelism = 4, MigrationInterval = 0, CheckpointInterval = 0, Dispatch = EvolutionDispatchMode.Pipeline
        };
        options.Pipeline.MaxProposalConcurrency = concurrency;
        options.Pipeline.WaveSize = 8;
        var engine = new EvolutionEngine<ProgramGenome>(task, variation,
            _ => new MapElitesArchive<ProgramGenome>(new[] { new EvolutionDescriptorDefinition("x", 0, 100, 20) }), options);
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
