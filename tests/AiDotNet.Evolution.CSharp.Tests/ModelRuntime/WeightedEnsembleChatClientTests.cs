using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>V1-50: OpenEvolve's weighted llm.models and AlphaEvolve's fast-plus-strong ensemble.</summary>
public sealed class WeightedEnsembleChatClientTests
{
    private sealed class NamedClient(string id, Exception? failure = null) : IProgramChatClient
    {
        public int Calls;
        public string ModelId => id;
        public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (failure is not null) throw failure;
            return Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant("from " + id)));
        }
    }

    [Fact]
    public void Draws_follow_the_weights_within_two_points()
    {
        var ensemble = new WeightedEnsembleChatClient(new[]
        {
            new WeightedChatModel(new NamedClient("flash"), 0.8), new WeightedChatModel(new NamedClient("pro"), 0.2)
        });
        int[] counts = new int[2];
        for (int seed = 0; seed < 10_000; seed++) counts[ensemble.Choose(seed)]++;
        Assert.InRange(counts[0] / 10_000.0, 0.78, 0.82);
        Assert.InRange(counts[1] / 10_000.0, 0.18, 0.22);
    }

    [Fact]
    public async Task The_seed_decides_the_model_so_a_replay_routes_identically_and_records_it()
    {
        WeightedEnsembleChatClient Make() => new(new[]
        {
            new WeightedChatModel(new NamedClient("flash"), 3), new WeightedChatModel(new NamedClient("pro"), 1)
        });
        WeightedEnsembleChatClient first = Make(), second = Make();
        var messages = new[] { ProgramChatMessage.User("x") };
        for (int seed = 0; seed < 50; seed++)
        {
            ProgramChatResponse a = await first.GetResponseAsync(messages, new ProgramChatOptions { Seed = seed });
            ProgramChatResponse b = await second.GetResponseAsync(messages, new ProgramChatOptions { Seed = seed });
            Assert.Equal(a.ModelId, b.ModelId);
            Assert.Equal("from " + a.ModelId, a.Text);
        }
        Assert.Equal(first.GetMemberStatistics().Select(s => s.Calls), second.GetMemberStatistics().Select(s => s.Calls));
        Assert.All(first.GetMemberStatistics(), member => Assert.True(member.Calls > 0));
    }

    [Fact]
    public async Task A_failing_model_is_charged_and_its_call_is_never_rerouted()
    {
        var failing = new NamedClient("broken", new HttpRequestException("down"));
        var healthy = new NamedClient("ok");
        var ensemble = new WeightedEnsembleChatClient(new[] { new WeightedChatModel(failing, 1), new WeightedChatModel(healthy, 1) });
        int seed = Enumerable.Range(0, 100).First(s => ensemble.Choose(s) == 0);
        await Assert.ThrowsAsync<HttpRequestException>(() => ensemble.GetResponseAsync(new[] { ProgramChatMessage.User("x") },
            new ProgramChatOptions { Seed = seed }));
        Assert.Equal(0, healthy.Calls);
        Assert.Equal((1L, 1L), (ensemble.GetMemberStatistics()[0].Calls, ensemble.GetMemberStatistics()[0].Failures));
    }

    [Fact]
    public async Task A_provider_failure_is_counted_even_when_the_caller_has_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ensemble = new WeightedEnsembleChatClient(new[] { new WeightedChatModel(new NamedClient("broken", new HttpRequestException("down")), 1) });
        await Assert.ThrowsAsync<HttpRequestException>(() => ensemble.GetResponseAsync(new[] { ProgramChatMessage.User("x") }, null, cancellation.Token));
        Assert.Equal(1, ensemble.GetMemberStatistics()[0].Failures);
    }

    [Fact]
    public void Invalid_ensembles_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new WeightedEnsembleChatClient(Array.Empty<WeightedChatModel>()));
        Assert.Throws<ArgumentException>(() => new WeightedEnsembleChatClient(new[] { new WeightedChatModel(new NamedClient("a"), 0) }));
        Assert.Throws<ArgumentException>(() => new WeightedEnsembleChatClient(new[] { new WeightedChatModel(new NamedClient("a"), double.NaN) }));
        Assert.Throws<ArgumentException>(() => new WeightedEnsembleChatClient(new[]
        {
            new WeightedChatModel(new NamedClient("a"), double.MaxValue), new WeightedChatModel(new NamedClient("b"), double.MaxValue)
        }));
        Assert.Throws<ArgumentException>(() => new WeightedEnsembleChatClient(
            Enumerable.Repeat(new WeightedChatModel(new NamedClient("a"), 1), int.MaxValue)));
        // Distinct configurations never share an identity, even when ids contain the separators.
        Assert.NotEqual(
            new WeightedEnsembleChatClient(new[] { new WeightedChatModel(new NamedClient("a:1,b"), 1) }).ModelId,
            new WeightedEnsembleChatClient(new[] { new WeightedChatModel(new NamedClient("a"), 1), new WeightedChatModel(new NamedClient("b"), 1) }).ModelId);
    }

    [Fact]
    public async Task The_variation_operator_routes_each_proposal_through_the_ensemble()
    {
        // The ensemble is a client, so it composes with the operator and with any ladder or portfolio built from operators.
        var flash = new FakeChatClient("```python\ndef solve(x):\n    return x + 1\n```");
        var pro = new FakeChatClient("```python\ndef solve(x):\n    return x + 2\n```");
        var ensemble = new WeightedEnsembleChatClient(new[] { new WeightedChatModel(flash, 1), new WeightedChatModel(pro, 1) });
        var variation = new LlmProgramVariationOperator(ensemble, new ProgramProposalOptions { Language = ProgramLanguage.Python },
            new LlmProgramVariationOptions { Mode = ProgramEvolutionMode.FullRewrite, MaxProposalRetries = 0, Seed = 11 });
        var parent = new ProgramGenome("def solve(x):\n    return x\n", ProgramLanguage.Python);
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new EvolutionCanonicalGenome<ProgramGenome>(parent, parent.Id), lineage);
        var evaluation = new EvolutionEvaluation(0, parent.Id, EvolutionEvaluationStatus.Completed, 0.5, EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double> { ["x"] = 0.5 }, Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "config-v1");
        var entry = new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 1 }), candidate, evaluation);
        ProgramGenome child = await variation.ProposeAsync(new EvolutionVariationContext<ProgramGenome>(entry,
            Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(), StableRandom.CreateStream(3, 1), 1, 0));

        // With a fixed operator seed the first sample's seed is 11, so exactly the member it routes to answers.
        int routed = ensemble.Choose(11);
        Assert.Equal(routed == 0 ? 1 : 0, flash.Calls);
        Assert.Equal(routed == 1 ? 1 : 0, pro.Calls);
        Assert.Contains(routed == 0 ? "x + 1" : "x + 2", child.Source);
    }
}