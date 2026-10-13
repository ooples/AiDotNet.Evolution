using System.Text.RegularExpressions;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// Covers meta-prompt evolution (V1-53, #166): the system instructions evolve alongside the
/// programs, scored only by the downstream quality of the programs written under them.
/// </summary>
/// <remarks>
/// AlphaEvolve credits this component in its ablations and OpenEvolve 0.3.2 declares it while its
/// own config comment says it is not implemented, so there is no upstream behaviour to match - the
/// tests pin what the paper describes: proposal, downstream scoring, an archive with lineage,
/// replay from the seed, exact restore, and a separately reported cost.
/// </remarks>
public sealed class MetaPromptEvolutionTests
{
    private const string Seed = "Improve the program. Keep it correct and short.";

    private static ProgramMetaPromptOptions Options(
        bool enabled = true,
        ProgramInstructionSelection selection = ProgramInstructionSelection.EpsilonGreedy,
        int archive = 8,
        decimal cost = 1m) => new()
        {
            Enabled = enabled,
            Selection = selection,
            MaxArchiveSize = archive,
            ProposalCostUnits = cost,
            ProposeEveryGenerations = 5,
        };

    /// <summary>
    /// The first criterion, end to end: an instruction is proposed, enters the archive with its
    /// parent, and is scored by the quality of the program written under it.
    /// </summary>
    [Fact]
    public async Task A_proposed_instruction_is_archived_with_lineage_and_scored_downstream()
    {
        const string improved = "Improve the program. Prefer a closed form over a loop, and say why in one line.";
        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(improved), Seed, Options());

        string? accepted = await evolver.ProposeAsync(generation: 5);

        Assert.Equal(improved, accepted);
        Assert.Equal(2, evolver.Archive.Count);

        ProgramInstructionCandidate child = Single(evolver, improved);
        ProgramInstructionCandidate seed = Single(evolver, Seed);
        Assert.Null(seed.ParentId);
        Assert.Equal(seed.Id, child.ParentId); // lineage: proposed from the archive's best, which is the seed
        Assert.Equal(5, child.Generation);
        Assert.Equal(0, child.Scored);

        // Scored by the program it produced, not by anything the model said about it.
        string selected = evolver.Select(new StableRandom(7), generation: 6);
        Assert.Equal(improved, selected); // unscored is unexplored, so the new instruction goes first
        evolver.Observe(generation: 6, quality: 0.8);

        child = Single(evolver, improved);
        Assert.Equal(1, child.Uses);
        Assert.Equal(1, child.Scored);
        Assert.Equal(0.8, child.MeanQuality, 10);
        Assert.Equal(0.0, seed.MeanQuality); // never used, so still unscored rather than scored badly
    }

    /// <summary>
    /// Replay: the same seed selects the same instructions in the same order, and a different seed
    /// does not. The second half is the control — without it, an evolver that always returned the
    /// same instruction would pass the determinism assertion.
    /// </summary>
    [Fact]
    public async Task Selection_replays_from_the_seed_and_a_different_seed_diverges()
    {
        // A fresh evolver per sequence: observing a quality mutates the archive the next selection
        // reads, so reusing one evolver would compare different starting states and prove nothing.
        string[] first = await SequenceFrom(1234);
        string[] again = await SequenceFrom(1234);
        string[] other = await SequenceFrom(9876);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);

        // The sequence has to contain more than one instruction, or equality would hold for an
        // evolver that always returned the seed.
        Assert.True(first.Distinct(StringComparer.Ordinal).Count() > 1,
            "the replayed sequence selected only one instruction, so it cannot detect divergence");
    }

    private static async Task<string[]> SequenceFrom(ulong seed)
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient(
                "Instruction A: prefer arithmetic to branching in the hot loop.",
                "Instruction B: write the simplest version first, then optimise only the measured hot path."),
            Seed, Options());

        // Two instructions beyond the seed, so there is something to choose between.
        await evolver.ProposeAsync(generation: 5);
        await evolver.ProposeAsync(generation: 10);
        Assert.Equal(3, evolver.Archive.Count);

        var random = new StableRandom(seed);
        var selected = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            long generation = 1_000 + i;
            selected.Add(evolver.Select(random, generation));

            // A fixed quality per instruction, so the archive's scores evolve identically for the
            // same seed and the only thing that can differ is the draw.
            evolver.Observe(generation, quality: selected[^1].Length / 100.0);
        }

        return selected.ToArray();
    }

    /// <summary>
    /// A state that fails validation leaves the evolver untouched. Clearing the archive before
    /// validating meant a half-applied state survived the exception, and a caller that caught it
    /// could then select or credit against an archive that never existed.
    /// </summary>
    [Fact]
    public async Task A_state_that_fails_validation_does_not_half_apply()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: fuse the two passes over the array into one."),
            Seed, Options());
        await evolver.ProposeAsync(generation: 5);
        string hash = evolver.StateHash;

        // The id is derived from the text, so editing the text alone makes the entry inconsistent -
        // and it is the SECOND entry, so a validator that mutates as it goes has already applied the
        // first by the time it refuses.
        string tampered = evolver.CaptureState().Replace(
            "Instruction A: fuse the two passes over the array into one.",
            "Instruction A: fuse the two passes over the array into one, carefully.");

        Assert.Throws<InvalidOperationException>(() => evolver.RestoreState(tampered));
        Assert.Equal(hash, evolver.StateHash);
        Assert.Equal(2, evolver.Archive.Count);
    }

    /// <summary>
    /// A restored route must name an instruction the restored archive holds, and no generation may
    /// have two routes: either would send an outcome nowhere.
    /// </summary>
    [Fact]
    public async Task A_route_to_a_missing_instruction_or_a_repeated_generation_is_refused()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: precompute the lookup table once per run."),
            Seed, Options());
        await evolver.ProposeAsync(generation: 5);
        evolver.Select(new StableRandom(8), generation: 6);
        string captured = evolver.CaptureState();

        var target = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());
        string unknownRoute = Regex.Replace(
            captured, "\"InstructionId\":\"[^\"]+\"", "\"InstructionId\":\"not-in-the-archive\"",
            RegexOptions.None, TimeSpan.FromSeconds(1));
        Assert.Throws<InvalidOperationException>(() => target.RestoreState(unknownRoute));

        string doubled = captured.Replace(
            "\"Routes\":[",
            "\"Routes\":[{\"Generation\":6,\"InstructionId\":\"" + evolver.Archive[0].Id + "\"},");
        Assert.Throws<InvalidOperationException>(() => target.RestoreState(doubled));
    }

    /// <summary>
    /// An instruction that still owes an outcome is not evicted. Evicting it would make Observe find
    /// no candidate and drop that generation's credit without saying so.
    /// </summary>
    [Fact]
    public async Task An_instruction_awaiting_an_outcome_is_not_evicted()
    {
        const string first = "Instruction A: hoist the bounds check out of the inner loop.";
        const string second = "Instruction B: replace the recursion with an explicit stack.";
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient(first, second),
            Seed, Options(selection: ProgramInstructionSelection.Greedy, archive: 2));

        // The archive holds two: the seed and `first`.
        Assert.Equal(first, await evolver.ProposeAsync(generation: 5));

        // Score both. Which one an unscored draw returns is random, so the quality follows the text
        // rather than the order: `first` ends up the leader either way.
        var random = new StableRandom(9);
        for (int i = 0; i < 2; i++)
        {
            string text = evolver.Select(random, 6 + i);
            evolver.Observe(6 + i, quality: text == first ? 0.9 : 0.1);
        }

        // Greedy now returns the leader, and this route is left owing an outcome.
        Assert.Equal(first, evolver.Select(random, generation: 8));

        // Full archive, and the only scored non-seed candidate owes an outcome, so the proposal is
        // refused rather than quietly costing a credit.
        Assert.Null(await evolver.ProposeAsync(generation: 10));
        Assert.Equal(2, evolver.Archive.Count);

        // The credit still lands, which is the point of refusing.
        evolver.Observe(generation: 8, quality: 0.9);
        Assert.Equal(0.9, Single(evolver, first).MeanQuality, 10);
    }

    /// <summary>
    /// ExplorationProbability at its extremes: zero never leaves the leader, one always draws.
    /// </summary>
    [Fact]
    public async Task Exploration_probability_bounds_the_draw()
    {
        const string better = "Instruction A: keep the allocation outside the measured region.";

        ProgramMetaPromptEvolver never =
            await Scored(ProgramInstructionSelection.EpsilonGreedy, better, exploration: 0.0);
        var random = new StableRandom(21);
        for (int i = 0; i < 25; i++) Assert.Equal(better, never.Select(random, 700 + i));

        // At 1.0 every draw is uniform, so over 40 draws the worse instruction must appear.
        ProgramMetaPromptEvolver always =
            await Scored(ProgramInstructionSelection.EpsilonGreedy, better, exploration: 1.0);
        var alwaysRandom = new StableRandom(21);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 40; i++) seen.Add(always.Select(alwaysRandom, 700 + i));
        Assert.Equal(2, seen.Count);
    }

    /// <summary>
    /// MaxInstructionChars bounds what may become the run's instruction, and MetricPrefix names the
    /// cost descriptors.
    /// </summary>
    [Fact]
    public async Task Max_instruction_chars_and_metric_prefix_are_honoured()
    {
        ProgramMetaPromptOptions options = Options();
        options.MaxInstructionChars = 80;
        options.MetricPrefix = "mp_";

        string tooLong = new string('a', 81);
        string justFits = new string('b', 80);
        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(tooLong, justFits), Seed, options);

        Assert.Null(await evolver.ProposeAsync(generation: 5));
        Assert.Equal(justFits, await evolver.ProposeAsync(generation: 10));

        var descriptors = new Dictionary<string, double>();
        evolver.WriteCostDescriptors(descriptors);
        Assert.Equal(2.0, descriptors["mp_calls"]);
        Assert.Equal(1.0, descriptors["mp_refused"]);
        Assert.DoesNotContain("meta_prompt_calls", descriptors.Keys);

        // A seed longer than the bound could never be replaced by an answer the evolver accepts, so
        // the constructor refuses it rather than leaving the meta level unable to do anything.
        Assert.Throws<ArgumentException>(
            () => new ProgramMetaPromptEvolver(new FakeChatClient(), new string('c', 81), options));
    }

    /// <summary>
    /// A minimizing run credits the instruction whose programs measured LOWER. Without the
    /// direction applied, the archive would order by raw quality and prefer the worst instruction,
    /// while looking perfectly well ordered.
    /// </summary>
    [Fact]
    public async Task A_minimizing_run_prefers_the_instruction_with_the_lower_measurements()
    {
        const string better = "Instruction A: the one whose programs measure lower.";
        ProgramMetaPromptOptions options = Options(selection: ProgramInstructionSelection.Greedy);
        options.Direction = EvolutionOptimizationDirection.Minimize;

        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(better), Seed, options);
        await evolver.ProposeAsync(generation: 5);

        var random = new StableRandom(31);
        for (int i = 0; i < 2; i++)
        {
            string text = evolver.Select(random, 300 + i);
            // Lower is better here, so the good instruction gets the SMALLER number.
            evolver.Observe(300 + i, quality: text == better ? 0.1 : 0.9);
        }

        for (int i = 0; i < 10; i++) Assert.Equal(better, evolver.Select(random, 400 + i));
        Assert.True(Single(evolver, better).MeanQuality > Single(evolver, Seed).MeanQuality);
    }

    /// <summary>
    /// A failed or abandoned proposal releases its route without crediting anything. The only way
    /// out used to be calling Observe with a made-up quality, which puts a number the run never
    /// measured into the archive's mean.
    /// </summary>
    [Fact]
    public void An_abandoned_proposal_releases_its_route_without_a_score()
    {
        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());
        evolver.Select(new StableRandom(32), generation: 3);

        Assert.True(evolver.Abandon(3));
        Assert.False(evolver.Abandon(3)); // nothing left to drop

        ProgramInstructionCandidate candidate = Assert.Single(evolver.Archive);
        Assert.Equal(1, candidate.Uses);   // it was asked, so the use stands
        Assert.Equal(0, candidate.Scored); // but it earned no score
        Assert.Equal(0.0, candidate.MeanQuality);

        // And the generation is free again, which is what a resumed or retried proposal needs.
        evolver.Select(new StableRandom(32), generation: 3);
    }

    /// <summary>
    /// The state hash must separate two costs that differ below six decimal places. Normalizing a
    /// decimal's scale is not the same as rounding it, and the first version of that fix rounded.
    /// </summary>
    [Fact]
    public async Task The_state_hash_separates_costs_that_differ_in_tiny_amounts()
    {
        async Task<string> HashFor(decimal cost)
        {
            ProgramMetaPromptOptions options = Options(cost: cost);
            var evolver = new ProgramMetaPromptEvolver(
                new FakeChatClient("Instruction A: an answer long enough to be accepted."), Seed, options);
            await evolver.ProposeAsync(generation: 5);
            return evolver.StateHash;
        }

        // Same amount, different scale: these must hash the same.
        Assert.Equal(await HashFor(3m), await HashFor(3.000m));

        // Different amounts below six decimal places: these must not. A fixed-point "F6" format
        // rounded both to 1.000000 and made this pair collide.
        Assert.NotEqual(await HashFor(1.00000001m), await HashFor(1.00000002m));
    }

    /// <summary>
    /// A checkpoint names the model that produced the archive, so restoring one model's instructions
    /// under another is refused rather than replayed as though the model were the same.
    /// </summary>
    [Fact]
    public async Task A_state_captured_against_another_model_is_refused()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: an answer long enough to be accepted."), Seed, Options());
        await evolver.ProposeAsync(generation: 5);
        string captured = evolver.CaptureState();

        var sameModel = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());
        sameModel.RestoreState(captured); // the stub reports one model id, so this is the same model

        var otherModel = new ProgramMetaPromptEvolver(new RenamedChatClient("other-model"), Seed, Options());
        Assert.Throws<InvalidOperationException>(() => otherModel.RestoreState(captured));
    }

    /// <summary>
    /// A quality total that would leave the double range is refused where it happens, rather than
    /// turning every later mean and the next checkpoint into something unusable.
    /// </summary>
    [Fact]
    public void A_quality_total_that_would_stop_being_finite_is_refused()
    {
        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());
        var random = new StableRandom(33);

        evolver.Select(random, generation: 1);
        evolver.Observe(1, quality: double.MaxValue);
        evolver.Select(random, generation: 2);

        Assert.Throws<InvalidOperationException>(() => evolver.Observe(2, quality: double.MaxValue));
        Assert.True(double.IsFinite(Assert.Single(evolver.Archive).MeanQuality));
    }

    /// <summary>
    /// The meta level's own model call carries a seed, so a replay asks for the same instruction.
    /// Selection already replays from the engine's stream; this was the one unseeded step.
    /// </summary>
    [Fact]
    public async Task The_instruction_request_carries_a_seed()
    {
        var client = new FakeChatClient("Instruction A: an answer long enough to be accepted.");
        var evolver = new ProgramMetaPromptEvolver(client, Seed, Options());

        await evolver.ProposeAsync(generation: 7);
        Assert.Equal(7, client.LastOptions?.Seed);

        var explicitly = new FakeChatClient("Instruction B: another answer long enough to be accepted.");
        var seeded = new ProgramMetaPromptEvolver(explicitly, Seed, Options());
        await seeded.ProposeAsync(generation: 7, ledger: null, seed: 4242);
        Assert.Equal(4242, explicitly.LastOptions?.Seed);
    }

    /// <summary>A chat client that reports a different model id, for the checkpoint identity test.</summary>
    private sealed class RenamedChatClient : IProgramChatClient
    {
        public RenamedChatClient(string modelId) => ModelId = modelId;

        public string ModelId { get; }

        public Task<ProgramChatResponse> GetResponseAsync(
            IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProgramChatResponse(ProgramChatMessage.Assistant("unused"), null, ModelId));
    }

    /// <summary>
    /// A ledger declares its own resource names, and nothing requires one of them to be
    /// "cost_units". The proposal is charged against the configured name, so a run metering
    /// something else is not refused by its own budget before the model is contacted.
    /// </summary>
    [Fact]
    public async Task The_proposal_is_charged_against_the_configured_resource()
    {
        ProgramMetaPromptOptions options = Options(cost: 2m);
        options.ProposalResourceName = "tokens";

        var ledger = new EvolutionResourceLedger(
            "meta-prompt-resource-test", EvolutionResources.Of("tokens", 50m));
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: an answer long enough to be accepted."), Seed, options);

        await evolver.ProposeAsync(generation: 5, ledger);

        Assert.Equal(2m, ledger.Snapshot().Spent["tokens"]);
        Assert.Equal(1, evolver.ProposalCalls);
    }

    private static ProgramInstructionCandidate Single(ProgramMetaPromptEvolver evolver, string text)
    {
        ProgramInstructionCandidate[] matches = evolver.Archive
            .Where(candidate => candidate.Text == text).ToArray();
        return Assert.Single(matches);
    }

    private static string[] SequenceFrom(ProgramMetaPromptEvolver evolver, ulong seed)
    {
        // Each sequence uses its own generation window so the routing table never sees a repeat,
        // and every selection is observed so the archive's scores advance the same way each time.
        var random = new StableRandom(seed);
        long baseGeneration = 1_000 + (long)(seed % 10) * 1_000;
        var selected = new List<string>();
        for (int i = 0; i < 40; i++)
        {
            long generation = baseGeneration + i;
            selected.Add(evolver.Select(random, generation));
            evolver.Observe(generation, quality: 0.5);
        }

        return selected.ToArray();
    }

    /// <summary>
    /// The second criterion: a restored checkpoint is the same archive, by state-hash equality.
    /// </summary>
    [Fact]
    public async Task A_restored_checkpoint_has_the_same_state_hash()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: hoist the invariant out of the loop before anything else."),
            Seed, Options());
        await evolver.ProposeAsync(generation: 5);
        var random = new StableRandom(3);
        evolver.Select(random, generation: 6);
        evolver.Observe(generation: 6, quality: 0.6);
        evolver.Select(random, generation: 7); // left outstanding on purpose: the route must survive too

        string captured = evolver.CaptureState();
        string hash = evolver.StateHash;

        var restored = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());
        restored.RestoreState(captured);

        Assert.Equal(hash, restored.StateHash);
        Assert.Equal(evolver.Archive.Select(c => (c.Id, c.ParentId, c.Generation, c.Uses, c.Scored, c.MeanQuality)),
            restored.Archive.Select(c => (c.Id, c.ParentId, c.Generation, c.Uses, c.Scored, c.MeanQuality)));

        // The outstanding route survived, so the resumed run can still credit the right instruction.
        restored.Observe(generation: 7, quality: 0.9);
    }

    /// <summary>
    /// A state hash that cannot tell two different archives apart would make the test above
    /// vacuous, so this is its negative control.
    /// </summary>
    [Fact]
    public async Task The_state_hash_changes_when_the_archive_does()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: measure before changing anything in the inner loop."),
            Seed, Options());
        string before = evolver.StateHash;

        await evolver.ProposeAsync(generation: 5);
        string afterProposal = evolver.StateHash;
        Assert.NotEqual(before, afterProposal);

        evolver.Select(new StableRandom(4), generation: 6);
        evolver.Observe(generation: 6, quality: 0.25);
        Assert.NotEqual(afterProposal, evolver.StateHash);
    }

    /// <summary>
    /// A checkpoint from different settings, a different seed instruction or with no archive is
    /// refused: restoring it would replay a selection sequence the archive never produced.
    /// </summary>
    [Fact]
    public async Task A_state_from_different_settings_or_another_seed_is_refused()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: prefer a table lookup where the domain is small."),
            Seed, Options());
        await evolver.ProposeAsync(generation: 5);
        string captured = evolver.CaptureState();

        var otherSettings = new ProgramMetaPromptEvolver(
            new FakeChatClient(), Seed, Options(selection: ProgramInstructionSelection.Uniform));
        Assert.Throws<InvalidOperationException>(() => otherSettings.RestoreState(captured));

        var otherSeed = new ProgramMetaPromptEvolver(
            new FakeChatClient(), "A different starting instruction entirely.", Options());
        Assert.Throws<InvalidOperationException>(() => otherSeed.RestoreState(captured));

        var sameSettings = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());
        Assert.Throws<InvalidOperationException>(
            () => sameSettings.RestoreState(captured.Replace("\"Archive\":[", "\"Archive\":[],\"Ignored\":[")));
    }

    /// <summary>
    /// The third criterion: the meta level's calls are charged to the run's ledger and reported as
    /// their own line, not folded into the program proposals.
    /// </summary>
    [Fact]
    public async Task The_meta_levels_cost_is_charged_to_the_ledger_and_reported_separately()
    {
        var ledger = new EvolutionResourceLedger(
            "meta-prompt-cost-test", EvolutionResources.Of("cost_units", 100m));
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: start from the failing case and generalise from it."),
            Seed, Options(cost: 3m));

        await evolver.ProposeAsync(generation: 5, ledger);

        EvolutionResourceSnapshot snapshot = ledger.Snapshot();
        Assert.Equal(3m, snapshot.Spent["cost_units"]);
        Assert.Equal(1, evolver.ProposalCalls);
        Assert.Equal(3m, evolver.ProposalCostUnits);

        var descriptors = new Dictionary<string, double>();
        evolver.WriteCostDescriptors(descriptors);
        Assert.Equal(1.0, descriptors["meta_prompt_calls"]);
        Assert.Equal(3.0, descriptors["meta_prompt_cost_units"]);
        Assert.Equal(0.0, descriptors["meta_prompt_refused"]);
        Assert.Equal(2.0, descriptors["meta_prompt_archive_size"]);
    }

    /// <summary>
    /// A budget that cannot pay for the call refuses it before the model is contacted, so the meta
    /// level cannot overspend a run's ledger.
    /// </summary>
    [Fact]
    public async Task A_proposal_the_budget_cannot_pay_for_is_refused_before_the_call()
    {
        var ledger = new EvolutionResourceLedger(
            "meta-prompt-budget-test", EvolutionResources.Of("cost_units", 2m));
        var client = new FakeChatClient("Instruction A: this answer must never be requested.");
        var evolver = new ProgramMetaPromptEvolver(client, Seed, Options(cost: 5m));

        await Assert.ThrowsAsync<EvolutionResourceBudgetException>(
            async () => await evolver.ProposeAsync(generation: 5, ledger));

        Assert.Equal(0, client.Calls);
        Assert.Equal(0, evolver.ProposalCalls);
        Assert.Single(evolver.Archive);
    }

    /// <summary>
    /// An answer that is not usable as an instruction is refused and counted, and the archive is
    /// left alone. A model asked for instructions sometimes answers with a program; without this,
    /// one bad reply would become the prompt for the rest of the run.
    /// </summary>
    [Fact]
    public async Task An_unusable_answer_is_refused_and_counted()
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient(new string('x', 5_000), "short", Seed),
            Seed, Options());

        Assert.Null(await evolver.ProposeAsync(generation: 5));  // longer than MaxInstructionChars
        Assert.Null(await evolver.ProposeAsync(generation: 10)); // too short to be instructions
        Assert.Null(await evolver.ProposeAsync(generation: 15)); // the instruction already in the archive

        Assert.Single(evolver.Archive);
        Assert.Equal(3, evolver.ProposalCalls);
        Assert.Equal(3, evolver.RefusedProposals);

        var descriptors = new Dictionary<string, double>();
        evolver.WriteCostDescriptors(descriptors);
        Assert.Equal(3.0, descriptors["meta_prompt_refused"]); // a paid-for refusal is visible, not inferred
    }

    /// <summary>
    /// An outcome for a generation the evolver did not route is refused rather than ignored:
    /// crediting the wrong instruction silently inverts the signal the archive is built on.
    /// </summary>
    [Fact]
    public void An_unknown_or_repeated_outcome_is_refused()
    {
        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(), Seed, Options());

        Assert.Throws<InvalidOperationException>(() => evolver.Observe(generation: 1, quality: 0.5));

        evolver.Select(new StableRandom(5), generation: 1);
        evolver.Observe(generation: 1, quality: 0.5);
        Assert.Throws<InvalidOperationException>(() => evolver.Observe(generation: 1, quality: 0.5));

        // Re-selecting a generation still awaiting its outcome would credit the second instruction
        // with the first one's result.
        evolver.Select(new StableRandom(5), generation: 2);
        Assert.Throws<InvalidOperationException>(() => evolver.Select(new StableRandom(5), generation: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => evolver.Observe(generation: 2, quality: double.NaN));
    }

    /// <summary>
    /// Greedy selection picks the best-scoring instruction once every instruction has been scored,
    /// and uniform selection does not — the two control arms for the credit signal.
    /// </summary>
    [Fact]
    public async Task Greedy_follows_the_score_and_uniform_ignores_it()
    {
        const string better = "Instruction A: inline the comparison and drop the allocation in the loop.";

        ProgramMetaPromptEvolver greedy = await Scored(ProgramInstructionSelection.Greedy, better);
        var random = new StableRandom(11);
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal(better, greedy.Select(random, 500 + i));
        }

        ProgramMetaPromptEvolver uniform = await Scored(ProgramInstructionSelection.Uniform, better);
        var uniformRandom = new StableRandom(11);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 40; i++)
        {
            seen.Add(uniform.Select(uniformRandom, 500 + i));
        }

        Assert.Equal(2, seen.Count); // the worse instruction is still selected, because credit is ignored
    }

    private static async Task<ProgramMetaPromptEvolver> Scored(
        ProgramInstructionSelection selection, string better, double? exploration = null)
    {
        ProgramMetaPromptOptions options = Options(selection: selection);
        if (exploration is { } probability) options.ExplorationProbability = probability;
        var evolver = new ProgramMetaPromptEvolver(new FakeChatClient(better), Seed, options);
        await evolver.ProposeAsync(generation: 5);

        // Score both: the seed badly, the new instruction well. Until an instruction is scored it
        // counts as unexplored and is selected first whatever the policy, so a test of the policy
        // has to get past that first.
        var random = new StableRandom(2);
        for (int i = 0; i < 2; i++)
        {
            string text = evolver.Select(random, 100 + i);
            evolver.Observe(100 + i, text == better ? 0.9 : 0.1);
        }

        foreach (ProgramInstructionCandidate candidate in evolver.Archive)
        {
            if (candidate.Scored != 0) continue;

            // Whichever one the draws missed, score it directly so no instruction is left unexplored.
            string text = evolver.Select(random, 200 + candidate.Generation);
            evolver.Observe(200 + candidate.Generation, text == better ? 0.9 : 0.1);
        }

        Assert.All(evolver.Archive, candidate => Assert.True(candidate.Scored > 0));
        return evolver;
    }
}
