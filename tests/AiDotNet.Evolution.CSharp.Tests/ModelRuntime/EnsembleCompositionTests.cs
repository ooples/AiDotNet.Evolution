using AiDotNet.Evolution;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.CSharp.Tests.ModelRuntime;

/// <summary>
/// V1-50 (#163) criterion 4: how the weighted ensemble composes with the escalation ladder (#139) and with
/// the adaptive portfolio, and which layer decides what.
/// </summary>
/// <remarks>
/// <para>
/// <b>The precedence, stated once.</b> An ensemble is an <see cref="IProgramChatClient"/> and a ladder or a
/// portfolio is built from <see cref="IVariationOperator{TGenome}"/>s, so they sit at different levels and
/// cannot contend: the outer operator chooses the <i>tier or member operator</i>, and the ensemble inside
/// the chosen operator chooses the <i>model</i>. There is no case where one overrides the other, and
/// nothing has to arbitrate between them.
/// </para>
/// <para>
/// That is worth a test rather than a sentence, because the sentence would stay true-looking if a future
/// change made the outer operator reach past its tier - for example by sharing one client across tiers, or
/// by re-seeding per tier - and the only visible symptom would be which member answered.
/// </para>
/// </remarks>
public sealed class EnsembleCompositionTests
{
    private const string Parent = "def solve(x):\n    return x\n";

    private static FakeChatClient Answering(int value) =>
        new("```python\ndef solve(x):\n    return x + " + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n```");

    private static WeightedEnsembleChatClient Ensemble(out FakeChatClient first, out FakeChatClient second, int firstValue)
    {
        first = Answering(firstValue);
        second = Answering(firstValue + 1);
        return new WeightedEnsembleChatClient(new[] { new WeightedChatModel(first, 1), new WeightedChatModel(second, 1) });
    }

    // The ladder refuses tiers with the same identity, which is what lets an escalation record name the tier
    // it came from and the one it went to - so each tier is named here rather than left to the default.
    private static LlmProgramVariationOperator Tier(IProgramChatClient client, int seed, string id) =>
        new(client, new ProgramProposalOptions { Language = ProgramLanguage.Python },
            new LlmProgramVariationOptions { Mode = ProgramEvolutionMode.FullRewrite, MaxProposalRetries = 0, Seed = seed }, id);

    private static EvolutionVariationContext<ProgramGenome> Context(long generation)
    {
        var parent = new ProgramGenome(Parent, ProgramLanguage.Python);
        var lineage = new EvolutionLineage(null, null, "seed", null, 0, 0, 0UL);
        var candidate = new EvolutionCandidate<ProgramGenome>(0, new EvolutionCanonicalGenome<ProgramGenome>(parent, parent.Id), lineage);
        var evaluation = Failed(parent.Id, 0);
        var entry = new EvolutionArchiveEntry<ProgramGenome>(new EvolutionCellKey(new[] { 1 }), candidate, evaluation);
        return new EvolutionVariationContext<ProgramGenome>(entry, Array.Empty<EvolutionArchiveEntry<ProgramGenome>>(),
            StableRandom.CreateStream(3, (ulong)generation), generation, 0);
    }

    [Fact]
    public async Task The_ladder_chooses_the_tier_and_the_ensemble_chooses_the_model_within_it()
    {
        WeightedEnsembleChatClient cheap = Ensemble(out FakeChatClient cheapA, out FakeChatClient cheapB, 10);
        WeightedEnsembleChatClient strong = Ensemble(out FakeChatClient strongA, out FakeChatClient strongB, 20);
        var ladder = new EscalatingVariationOperator<ProgramGenome>(
            new IVariationOperator<ProgramGenome>[] { Tier(cheap, 11, "cheap"), Tier(strong, 11, "strong") }, failuresBeforeEscalation: 2);

        // Two failures at the cheap tier, so the third proposal is escalated.
        for (long generation = 1; generation <= 3; generation++)
        {
            ProgramGenome child = await ladder.ProposeAsync(Context(generation));
            Assert.Contains("return x + ", child.Source, StringComparison.Ordinal);
            ladder.Observe(Failed(child.Id, generation), null);
        }

        EvolutionEscalation escalation = Assert.Single(ladder.Escalations);
        Assert.Equal((0, 1, "cheap", "strong", 2),
            (escalation.FromTier, escalation.ToTier, escalation.FromOperatorId, escalation.ToOperatorId,
             escalation.ConsecutiveFailures));

        // The ladder picked the tier: the cheap ensemble answered twice, the strong one once, and neither
        // reached past its own tier.
        Assert.Equal(2, cheapA.Calls + cheapB.Calls);
        Assert.Equal(1, strongA.Calls + strongB.Calls);

        // The ensemble picked the model: within each tier it is exactly the member Choose() names for the
        // operator's seed, so the tier did not bypass its own client.
        FakeChatClient cheapRouted = cheap.Choose(11) == 0 ? cheapA : cheapB;
        FakeChatClient strongRouted = strong.Choose(11) == 0 ? strongA : strongB;
        Assert.Equal(2, cheapRouted.Calls);
        Assert.Equal(1, strongRouted.Calls);
    }

    [Fact]
    public async Task The_same_seeds_route_identically_through_both_layers()
    {
        async Task<(int CheapA, int CheapB, int StrongA, int StrongB, int Escalations)> RunOnce()
        {
            WeightedEnsembleChatClient cheap = Ensemble(out FakeChatClient cheapA, out FakeChatClient cheapB, 10);
            WeightedEnsembleChatClient strong = Ensemble(out FakeChatClient strongA, out FakeChatClient strongB, 20);
            var ladder = new EscalatingVariationOperator<ProgramGenome>(
                new IVariationOperator<ProgramGenome>[] { Tier(cheap, 7, "cheap"), Tier(strong, 7, "strong") }, failuresBeforeEscalation: 2);
            for (long generation = 1; generation <= 5; generation++)
            {
                ProgramGenome child = await ladder.ProposeAsync(Context(generation));
                ladder.Observe(Failed(child.Id, generation), null);
            }

            return (cheapA.Calls, cheapB.Calls, strongA.Calls, strongB.Calls, ladder.Escalations.Count);
        }

        var first = await RunOnce();
        var second = await RunOnce();

        Assert.Equal(first, second);
        // Non-vacuous: a run that never escalated, or never called a model, would compare equal to itself.
        Assert.True(first.Escalations > 0, "the ladder never escalated, so the replay compared one tier only");
        Assert.Equal(5, first.CheapA + first.CheapB + first.StrongA + first.StrongB);
    }

    [Fact]
    public async Task A_portfolio_composes_the_same_way_round()
    {
        // The portfolio selects among operators, so an ensemble inside one of them is reached only when that
        // operator is selected - the same nesting as the ladder, asserted so the precedence is not a claim
        // that happens to hold for one of the two outer operators.
        WeightedEnsembleChatClient left = Ensemble(out FakeChatClient leftA, out FakeChatClient leftB, 30);
        WeightedEnsembleChatClient right = Ensemble(out FakeChatClient rightA, out FakeChatClient rightB, 40);
        var portfolio = new AdaptiveVariationPortfolio<ProgramGenome>(
            new IVariationOperator<ProgramGenome>[] { Tier(left, 5, "left"), Tier(right, 5, "right") });

        for (long generation = 1; generation <= 8; generation++)
        {
            ProgramGenome child = await portfolio.ProposeAsync(Context(generation));
            portfolio.Observe(Failed(child.Id, generation), null);
        }

        int leftCalls = leftA.Calls + leftB.Calls, rightCalls = rightA.Calls + rightB.Calls;
        Assert.Equal(8, leftCalls + rightCalls);
        // Whichever member operators the portfolio chose, each one's calls landed on exactly the member its
        // own ensemble names for the seed - never split across both members of one ensemble.
        Assert.Equal(leftCalls, (left.Choose(5) == 0 ? leftA : leftB).Calls);
        Assert.Equal(rightCalls, (right.Choose(5) == 0 ? rightA : rightB).Calls);
    }

    /// <summary>A failed outcome at the given generation, which is what the ladder counts toward escalation.</summary>
    private static EvolutionEvaluation Failed(string genomeId, long generation) =>
        new(generation, genomeId, EvolutionEvaluationStatus.Failed, null, EvolutionOptimizationDirection.Maximize,
            new Dictionary<string, double>(), Array.Empty<double>(), Array.Empty<double>(),
            new EvolutionEvaluationCost(TimeSpan.Zero, 1, 1),
            new EvolutionLineage(null, null, "seed", null, generation, 0, 0UL),
            EvolutionCacheStatus.Miss, Array.Empty<EvolutionDiagnostic>(), "task-v1", "evaluator-v1", "config-v1");
}
