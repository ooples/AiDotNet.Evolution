using AiDotNet.Evolution.Programs;
using AiDotNet.Evolution.Prompts;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

public sealed class ModelRuntimeAdversarialTests
{
    [Fact]
    public async Task Retry_budget_is_checked_before_another_provider_call()
    {
        var provider = new Provider { Text = new string('z', 500) };
        var prompt = new ProgramPromptBuilder(new ProgramEvolutionPromptOptions
        {
            MaxPromptChars = 256,
            SystemMessageMode = ProgramPromptSystemMessageMode.Literal,
            SystemMessage = "Edit the program.",
            TemplateOverrides = new Dictionary<ProgramPromptTemplateKey, string>
            {
                [ProgramPromptTemplateKey.DiffUser] = "{current_program}"
            }
        });
        var variation = new LlmProgramVariationOperator(provider, promptBuilder: prompt);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await variation.ProposeAsync(Context()));
        Assert.Equal(1, provider.Calls);
    }

    private static EvolutionVariationContext<ProgramGenome> Context()
    {
        var genome = new ProgramGenome("x = 1", ProgramLanguage.Python);
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new(genome, genome.Id), lineage);
        var evaluation = new EvolutionEvaluation(0, genome.Id, EvolutionEvaluationStatus.Completed, 1,
            EvolutionOptimizationDirection.Maximize, new Dictionary<string, double>(), Array.Empty<double>(),
            Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1), lineage,
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "evaluator", "config");
        return new(new(new EvolutionCellKey(new[] { 0 }), candidate, evaluation),
            Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(), new StableRandom(7, 3), 0, 0);
    }

    [Fact]
    public async Task Provider_identity_changes_invalidate_compatibility_and_dispatch()
    {
        var provider = new Provider();
        var first = new LlmProgramVariationOperator(provider);
        provider.ModelId = "changed";
        Assert.NotEqual(first.VersionHash, new LlmProgramVariationOperator(provider).VersionHash);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await first.ProposeAsync(Context()));
        Assert.Equal(0, provider.Calls);
        provider.ChangeDuringCall = true;
        var changing = new LlmProgramVariationOperator(provider);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await changing.ProposeAsync(Context()));
    }

    [Fact]
    public async Task Provider_cannot_mutate_the_dispatched_conversation()
    {
        var provider = new Provider { ProbeMutation = true };
        var variation = new LlmProgramVariationOperator(provider);
        await variation.ProposeAsync(Context());
        Assert.True(provider.MutationRejected);
    }

    [Fact]
    public async Task Oversized_full_rewrite_is_rejected_before_fence_parsing()
    {
        var provider = new Provider { Text = new string('x', 65) };
        var options = new ProgramProposalOptions { Diff = new ProgramDiffOptions { MaxResponseChars = 64 } };
        var variation = new LlmProgramVariationOperator(provider, options,
            new LlmProgramVariationOptions { Mode = ProgramEvolutionMode.FullRewrite, MaxProposalRetries = 0 });
        var child = await variation.ProposeAsync(Context());
        Assert.Equal("x = 1", child.Source);
        Assert.Equal(ProgramProposalOutcome.ParseFailed, variation.GetRecentAttempts()[0].Outcome);
        options.Diff.MaxResponseChars++;
        Assert.NotEqual(variation.VersionHash, new LlmProgramVariationOperator(provider, options,
            new LlmProgramVariationOptions { Mode = ProgramEvolutionMode.FullRewrite, MaxProposalRetries = 0 }).VersionHash);
    }

    [Fact]
    public void Usage_totals_do_not_overflow_and_state_is_bounded_before_deserialization()
    {
        Assert.Equal(2L * int.MaxValue, new ProgramChatUsage(int.MaxValue, int.MaxValue).TotalTokens);
        Assert.Equal(2L * int.MaxValue, new ProgramProposalAttempt("parent", 1, ProgramProposalOutcome.Accepted,
            inputTokens: int.MaxValue, outputTokens: int.MaxValue).TotalTokens);
        Assert.Throws<InvalidDataException>(() => new LlmProgramVariationOperator(new Provider()).RestoreState(new string(' ', 1024 * 1024 + 1)));
        Assert.Throws<ArgumentException>(() => new ProgramProposalAttempt(new string('a', 257), 1, ProgramProposalOutcome.Accepted));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgramChatUsage(-1, 0));
    }

    private sealed class Provider : IProgramChatClient
    {
        public string ModelId { get; set; } = "fixture";
        public int Calls { get; private set; }
        public bool ChangeDuringCall { get; set; }
        public bool ProbeMutation { get; set; }
        public bool MutationRejected { get; private set; }
        public string Text { get; set; } = "<<<<<<< SEARCH\nx = 1\n=======\nx = 2\n>>>>>>> REPLACE\n";
        public Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages,
            ProgramChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (ProbeMutation)
            {
                try { ((IList<ProgramChatMessage>)messages)[0] = ProgramChatMessage.System("injected"); }
                catch (NotSupportedException) { MutationRejected = true; }
            }
            if (ChangeDuringCall) ModelId = "mutated";
            return Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant(Text)));
        }
    }
}
