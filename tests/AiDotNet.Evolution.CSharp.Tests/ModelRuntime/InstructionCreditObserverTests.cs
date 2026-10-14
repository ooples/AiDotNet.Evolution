using System.Linq;
using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// V1-53 (#166): the part that tells an evolving instruction what its programs achieved. Without it the
/// feature is inert rather than broken - instructions are proposed, used, and never judged - so these tests
/// are mostly about telling "no effect" apart from "not wired".
/// </summary>
public sealed class InstructionCreditObserverTests
{
    private const string SeedInstruction = "Improve the program. Keep it correct and short.";

    private static ProgramMetaPromptOptions Options(
        int proposeEvery = 1,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize) => new()
        {
            Enabled = true,
            ProposeEveryGenerations = proposeEvery,
            MaxArchiveSize = 4,
            Direction = direction
        };

    private static ProgramMetaPromptEvolver Evolver(string answer = "Instruction A: a reply long enough to be accepted.")
        => new(new FakeChatClient(answer), SeedInstruction, Options());

    [Fact]
    public async Task An_outcome_is_credited_only_to_a_generation_the_source_actually_routed()
    {
        // The engine evaluates seed candidates that no proposal produced, and Observe refuses a generation it
        // never routed. Crediting "anything past generation zero" would throw on the seed's own evaluation.
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver);

        await observer.OnEventAsync(Evaluated(generation: 0, quality: 0.9));
        Assert.Equal(0, observer.CreditedOutcomes);

        observer.Select(new StableRandom(3), generation: 1);
        await observer.OnEventAsync(Evaluated(generation: 1, quality: 0.9));
        Assert.Equal(1, observer.CreditedOutcomes);
        Assert.Equal(1, evolver.Archive.Sum(candidate => candidate.Scored));
    }

    [Fact]
    public async Task A_repeated_outcome_for_one_generation_is_credited_once()
    {
        // Observe refuses a repeated generation outright, so a duplicated event would end the run rather than
        // double-count - the route is released on the first credit.
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver);
        observer.Select(new StableRandom(3), generation: 4);

        await observer.OnEventAsync(Evaluated(generation: 4, quality: 0.5));
        await observer.OnEventAsync(Evaluated(generation: 4, quality: 0.5));

        Assert.Equal(1, observer.CreditedOutcomes);
    }

    [Fact]
    public async Task A_failed_evaluation_leaves_its_instruction_unscored_rather_than_scored_at_a_guess()
    {
        // "Not scored" and "scored zero" differ, and the difference decides the run: whatever number a
        // failure is credited with competes with the real ones, and 0.0 beats every negated cost when
        // minimizing and every negative quality when maximizing. The instruction keeps the use it was
        // charged - it was asked - and earns nothing.
        ProgramMetaPromptEvolver failing = Evolver();
        var observer = new ProgramInstructionCreditObserver(failing);
        observer.Select(new StableRandom(3), generation: 1);

        await observer.OnEventAsync(Evaluated(generation: 1, quality: null));

        Assert.Equal(0, observer.CreditedOutcomes);
        Assert.Equal(1, observer.FailedOutcomes);
        Assert.All(failing.Archive, candidate => Assert.Equal(0, candidate.Scored));
        // The seed is what was routed, and the cadence has already proposed a second instruction that has
        // not been. The seed keeps the use and earns nothing.
        ProgramInstructionCandidate seed = Assert.Single(failing.Archive, candidate => candidate.Generation == 0);
        Assert.Equal(1L, seed.Uses);
    }

    [Fact]
    public async Task An_instruction_whose_only_outcome_failed_is_not_selected_ahead_of_a_scored_one()
    {
        // The hazard of leaving a failure unscored: selection tries an unexplored instruction before any
        // scored one, so an instruction scored by nothing would be drawn first for the rest of the run.
        // Unexplored has to mean untried, not unscored.
        var options = new ProgramMetaPromptOptions
        {
            Enabled = true,
            ProposeEveryGenerations = 1,
            MaxArchiveSize = 4,
            Selection = ProgramInstructionSelection.EpsilonGreedy,
            ExplorationProbability = 0.0
        };
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: a reply long enough to be accepted."), SeedInstruction, options);

        // The seed is the only candidate, so it is drawn and scored well.
        Assert.Equal(SeedInstruction, evolver.Select(new StableRandom(3), generation: 1));
        evolver.Observe(generation: 1, quality: 0.9);

        // A second instruction joins, is drawn as the untried one, and its outcome fails.
        string? proposed = await evolver.ProposeAsync(generation: 2);
        Assert.NotNull(proposed);
        Assert.Equal(proposed, evolver.Select(new StableRandom(3), generation: 2));
        Assert.True(evolver.Abandon(2));

        // Nothing is untried now and exploration is off, so the draw is greedy - and the only instruction
        // with a measured outcome is the seed.
        Assert.Equal(SeedInstruction, evolver.Select(new StableRandom(3), generation: 3));
    }

    [Fact]
    public async Task Only_an_evaluated_event_credits_anything()
    {
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver);
        observer.Select(new StableRandom(3), generation: 1);

        await observer.OnEventAsync(new EvolutionEvent<ProgramGenome>(EvolutionEventKind.Proposed, 1));
        Assert.Equal(0, observer.CreditedOutcomes);

        await observer.OnEventAsync(Evaluated(generation: 1, quality: 0.7));
        Assert.Equal(1, observer.CreditedOutcomes);
    }

    [Fact]
    public async Task An_abandoned_generation_releases_its_route_so_a_later_outcome_cannot_credit_it()
    {
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver);
        observer.Select(new StableRandom(3), generation: 2);

        Assert.True(observer.Abandon(2));
        Assert.False(observer.Abandon(2));
        await observer.OnEventAsync(Evaluated(generation: 2, quality: 0.9));

        Assert.Equal(0, observer.CreditedOutcomes);
    }

    [Fact]
    public async Task The_proposal_cadence_is_the_evolvers_own_and_its_cost_reaches_the_ledger()
    {
        var ledger = new EvolutionResourceLedger("meta",
            new EvolutionResources(new Dictionary<string, decimal> { ["cost_units"] = 10 }));
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver, ledger);

        for (long generation = 1; generation <= 3; generation++)
        {
            observer.Select(new StableRandom(3), generation);
            await observer.OnEventAsync(Evaluated(generation, quality: 0.5));
        }

        Assert.True(observer.AcceptedProposals > 0, "the evolver was never asked for an instruction");
        // The ledger is charged per dispatched proposal call, not per accepted instruction: a refused or
        // duplicate reply still cost a model call, and collapsing that to zero is the accounting error the
        // ledger exists to prevent. Asserted against the evolver's own call counter for that reason.
        Assert.Equal((decimal)evolver.ProposalCalls, ledger.Snapshot().Spent["cost_units"]);
        Assert.True(evolver.ProposalCalls >= observer.AcceptedProposals,
            "more instructions were accepted than were asked for");
        Assert.True(evolver.Archive.Count > 1, "the archive never grew past the seed instruction");
    }

    [Fact]
    public void The_observer_is_the_instruction_source_and_carries_the_evolvers_identity()
    {
        // Both halves are one object on purpose: the source half records the route the observer half credits.
        // Its version hash is the evolver's, so an operator built on it cannot claim the identity of one
        // built on a different evolver - which a checkpoint would otherwise restore across.
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver);

        Assert.Equal(evolver.VersionHash, observer.VersionHash);
        Assert.Equal(SeedInstruction, observer.Select(new StableRandom(3), generation: 1));
        Assert.NotEqual(observer.VersionHash, new ProgramInstructionCreditObserver(
            new ProgramMetaPromptEvolver(new FakeChatClient(), "A different seed instruction entirely.", Options()))
            .VersionHash);
    }

    [Theory]
    // Minimizing: the evolver negates, so any stand-in a failure could be credited with competes with a
    // negated cost. Maximizing with a negative quality: 0.0 is better than every outcome the run measured.
    // Both orders, because a stream of credits is never revised - an early stand-in stays in the mean.
    [InlineData(EvolutionOptimizationDirection.Minimize, 5.0, true)]
    [InlineData(EvolutionOptimizationDirection.Minimize, 5.0, false)]
    [InlineData(EvolutionOptimizationDirection.Maximize, -5.0, true)]
    [InlineData(EvolutionOptimizationDirection.Maximize, -5.0, false)]
    public async Task A_failed_evaluation_contributes_nothing_to_the_instructions_mean(
        EvolutionOptimizationDirection direction, double measured, bool failureFirst)
    {
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: a reply long enough to be accepted."), SeedInstruction,
            Options(proposeEvery: 100, direction: direction));
        var observer = new ProgramInstructionCreditObserver(evolver);

        double?[] outcomes = failureFirst ? new double?[] { null, measured } : new double?[] { measured, null };
        for (int index = 0; index < outcomes.Length; index++)
        {
            long generation = index + 1;
            observer.Select(new StableRandom(3), generation);
            await observer.OnEventAsync(Evaluated(generation, outcomes[index]));
        }

        Assert.Equal(1, observer.CreditedOutcomes);
        Assert.Equal(1, observer.FailedOutcomes);
        // One scored outcome, and the mean is exactly it: the failure released its route and put no number
        // the run never measured into the archive. -5.0 either way - negated from 5.0 when minimizing, and
        // the measured value itself when maximizing.
        ProgramInstructionCandidate scored = Assert.Single(evolver.Archive, candidate => candidate.Scored > 0);
        Assert.Equal(1, scored.Scored);
        Assert.Equal(-5.0, scored.MeanQuality, 10);
    }

    [Fact]
    public async Task A_route_the_evolver_refused_is_not_recorded()
    {
        // Select throws when a generation is already routed. Recording the route first would leave this
        // observer holding one the evolver does not have, and the later outcome would then call Observe for
        // an unrouted generation - throwing inside the engine's observer path.
        ProgramMetaPromptEvolver evolver = Evolver();
        var observer = new ProgramInstructionCreditObserver(evolver);
        observer.Select(new StableRandom(3), generation: 1);

        Assert.ThrowsAny<Exception>(() => observer.Select(new StableRandom(3), generation: 1));

        // The first route still works, and the refused second call left nothing behind.
        await observer.OnEventAsync(Evaluated(generation: 1, quality: 0.5));
        Assert.Equal(1, observer.CreditedOutcomes);
        await observer.OnEventAsync(Evaluated(generation: 1, quality: 0.5));
        Assert.Equal(1, observer.CreditedOutcomes);
    }

    /// <summary>An <c>Evaluated</c> event for one generation, with the quality the engine would report.</summary>
    private static EvolutionEvent<ProgramGenome> Evaluated(long generation, double? quality)
    {
        var lineage = new EvolutionLineage(null, null, "meta", null, generation, 0, (ulong)generation);
        var evaluation = new EvolutionEvaluation(
            generation, "g" + generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            quality is null ? EvolutionEvaluationStatus.Failed : EvolutionEvaluationStatus.Completed,
            quality, EvolutionOptimizationDirection.Maximize, new Dictionary<string, double>(),
            Array.Empty<double>(), Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            lineage, EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task", "evaluator", "config");
        return new EvolutionEvent<ProgramGenome>(EvolutionEventKind.Evaluated, generation, evaluation: evaluation);
    }
}
