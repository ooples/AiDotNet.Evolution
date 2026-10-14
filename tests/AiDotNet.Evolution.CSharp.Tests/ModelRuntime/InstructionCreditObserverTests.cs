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
    public async Task A_failed_evaluation_is_credited_as_the_worst_outcome_rather_than_skipped()
    {
        // Skipping would let an instruction whose programs never parse keep a clean record and stay
        // selectable. Asserted against a scored instruction, because "not scored" and "scored zero" differ.
        ProgramMetaPromptEvolver failing = Evolver();
        var observer = new ProgramInstructionCreditObserver(failing);
        observer.Select(new StableRandom(3), generation: 1);

        await observer.OnEventAsync(Evaluated(generation: 1, quality: null));

        Assert.Equal(1, observer.CreditedOutcomes);
        ProgramInstructionCandidate scored = Assert.Single(failing.Archive, candidate => candidate.Scored > 0);
        Assert.Equal(0.0, scored.MeanQuality);
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

    [Fact]
    public async Task A_failed_evaluation_never_outranks_a_measured_one_when_minimizing()
    {
        // The bug this test exists for: ProgramMetaPromptEvolver.Observe negates quality when minimizing, so
        // crediting a plain 0.0 for a failure becomes -0.0 there - better than any real positive cost, which
        // scores a broken instruction as the BEST one and makes it the most selectable. A failure is credited
        // at the worst cost the run has actually observed instead.
        var evolver = new ProgramMetaPromptEvolver(
            new FakeChatClient("Instruction A: a reply long enough to be accepted."), SeedInstruction,
            Options(proposeEvery: 100, direction: EvolutionOptimizationDirection.Minimize));
        var observer = new ProgramInstructionCreditObserver(
            evolver, null, EvolutionOptimizationDirection.Minimize);

        // A measured cost of 5.0 first, so the run has an observed worst case, then a failure.
        observer.Select(new StableRandom(3), generation: 1);
        await observer.OnEventAsync(Evaluated(generation: 1, quality: 5.0));
        observer.Select(new StableRandom(3), generation: 2);
        await observer.OnEventAsync(Evaluated(generation: 2, quality: null));

        Assert.Equal(2, observer.CreditedOutcomes);
        // Minimizing: the evolver negates, so a credited 5.0 becomes -5.0. Both outcomes landed on the same
        // instruction here, so its mean must be -5.0 - the failure was treated as 5.0, not as 0.0, which
        // would have pulled the mean up to -2.5 and made a failing instruction look better.
        ProgramInstructionCandidate scored = Assert.Single(evolver.Archive, candidate => candidate.Scored > 0);
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

    [Fact]
    public void A_direction_that_is_not_defined_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProgramInstructionCreditObserver(
            Evolver(), null, (EvolutionOptimizationDirection)99));
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
