using System.Globalization;
using System.Text.Json.Nodes;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionOperatorCreditTests
{
    private const string Units = "deterministic-work-units-v1";
    private static EvolutionResourceLedger Ledger(decimal cap = 10000) => new("operator-credit-test", EvolutionResources.Of("cost_units", cap));
    private static EvolutionOperatorRewardPolicy Policy(EvolutionOperatorRewardKind kind = EvolutionOperatorRewardKind.ParentImprovement,
        EvolutionOperatorCostBasis cost = EvolutionOperatorCostBasis.ProposalAndEvaluation, double scale = 1) => new(kind, cost, Units, scale);
    private static ResourceMeteredVariationOperator<TestGenome> Meter(Source source, EvolutionResourceLedger ledger, decimal maximum = 5) =>
        new(source, ledger, EvolutionResources.Of("cost_units", maximum), Units);

    private sealed class Source(string id = "source", decimal cost = 2) : ICostedEvolutionProposalSource<TestGenome>
    {
        public string Id => id;
        public string VersionHash => "source-v1-" + cost.ToString(CultureInfo.InvariantCulture);
        public int Proposals { get; private set; }
        public int Outcomes { get; private set; }
        public Func<EvolutionVariationContext<TestGenome>, ValueTask<EvolutionResourceResult<TestGenome>>>? Work { get; set; }
        public ValueTask<EvolutionResourceResult<TestGenome>> ProposeAsync(EvolutionVariationContext<TestGenome> context, CancellationToken cancellationToken = default)
        {
            Proposals++;
            return Work is null ? new(new EvolutionResourceResult<TestGenome>(new TestGenome((int)context.Generation + 2), EvolutionResources.Of("cost_units", cost))) : Work(context);
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) => Outcomes++;
        public string CaptureState() => Proposals.ToString(CultureInfo.InvariantCulture) + ":" + Outcomes.ToString(CultureInfo.InvariantCulture);
        public void RestoreState(string state)
        {
            var fields = state.Split(':');
            if (fields.Length != 2 || !int.TryParse(fields[0], out int proposed) || !int.TryParse(fields[1], out int observed) || observed < 0 || proposed < observed)
                throw new InvalidDataException("Invalid test source state.");
            Proposals = proposed; Outcomes = observed;
        }
    }

    private static EvolutionEvaluation Evaluation(long generation, double quality, double cost = 2,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize,
        EvolutionEvaluationStatus status = EvolutionEvaluationStatus.Completed, EvolutionCacheStatus cache = EvolutionCacheStatus.Miss,
        int attempts = 1, bool infeasible = false, string? diagnostic = null) =>
        new(generation, "g" + generation, status, quality, direction, new Dictionary<string, double> { ["x"] = 1 }, Array.Empty<double>(),
            infeasible ? new[] { 1d } : Array.Empty<double>(), new EvolutionEvaluationCost(TimeSpan.Zero, attempts, cost),
            new EvolutionLineage(null, null, "test", null, generation, 0, (ulong)generation), cache,
            diagnostic is null ? Array.Empty<EvolutionDiagnostic>() : new[] { new EvolutionDiagnostic(diagnostic, "test") }, "task", "evaluator", "config");
    private static EvolutionVariationContext<TestGenome> Context(long generation, double parentQuality = 0,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize)
    {
        var measured = Evaluation(0, parentQuality, direction: direction);
        var parent = new EvolutionArchiveEntry<TestGenome>(new EvolutionCellKey(new[] { 0 }),
            new EvolutionCandidate<TestGenome>(0, new EvolutionCanonicalGenome<TestGenome>(new TestGenome(1), "g0"), measured.Lineage), measured);
        return new(parent, Array.Empty<EvolutionArchiveEntry<TestGenome>>(), StableRandom.CreateStream(42, (ulong)generation), generation, 0);
    }

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize, EvolutionOperatorRewardKind.ParentImprovement, 0.1)]
    [InlineData(EvolutionOptimizationDirection.Minimize, EvolutionOperatorRewardKind.ParentImprovement, 0.1)]
    [InlineData(EvolutionOptimizationDirection.Maximize, EvolutionOperatorRewardKind.ArchiveSuccess, 0.25)]
    public async Task Explicit_gain_and_both_cost_stages_determine_bounded_credit(EvolutionOptimizationDirection direction, EvolutionOperatorRewardKind kind, double expected)
    {
        var source = new Source(); var ledger = Ledger(); var metered = Meter(source, ledger);
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { metered }, rewardPolicy: Policy(kind));
        await portfolio.ProposeAsync(Context(1, direction: direction));
        var cost = metered.GetProposalCost(1);
        Assert.Equal(2, cost.Charged["cost_units"]); Assert.False(cost.IsUnknown);
        portfolio.Observe(Evaluation(1, direction == EvolutionOptimizationDirection.Maximize ? 0.4 : -0.4, direction: direction), EvolutionArchiveInsertionResult.Replaced);
        Assert.Equal(expected, portfolio.Statistics[0].RewardSum, 12);
        var credit = portfolio.LastCredit!;
        Assert.Equal(metered.Id, credit.OperatorId); Assert.Equal(metered.VersionHash, credit.OperatorVersionHash);
        Assert.Equal(1, credit.Generation); Assert.Equal(expected, credit.Reward, 12);
        Assert.Equal(0, credit.ParentQuality); Assert.Equal(2, credit.ProposalCost!.Charged["cost_units"]);
        Assert.Contains("PolicyVersionHash", System.Text.Json.JsonSerializer.Serialize(credit));
        Assert.Equal(1, source.Outcomes); Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
        Assert.Throws<InvalidOperationException>(() => metered.GetProposalCost(1));
        Assert.Throws<InvalidOperationException>(() => portfolio.Observe(Evaluation(1, 1), EvolutionArchiveInsertionResult.Inserted));
    }

    [Theory]
    [InlineData("worse")]
    [InlineData("same")]
    [InlineData("not-inserted")]
    [InlineData("cache")]
    [InlineData("failed")]
    [InlineData("infeasible")]
    [InlineData("no-attempt")]
    [InlineData("unknown-evaluation-cost")]
    [InlineData("direction")]
    public async Task Invalid_or_unproductive_outcomes_receive_no_credit_but_keep_proposal_costs(string scenario)
    {
        var ledger = Ledger(); var metered = Meter(new Source(), ledger);
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { metered }, rewardPolicy: Policy());
        await portfolio.ProposeAsync(Context(1));
        portfolio.Observe(Evaluation(1, scenario == "worse" ? -1 : scenario == "same" ? 0 : 1,
            direction: scenario == "direction" ? EvolutionOptimizationDirection.Minimize : EvolutionOptimizationDirection.Maximize,
            status: scenario == "failed" ? EvolutionEvaluationStatus.Failed : EvolutionEvaluationStatus.Completed,
            cache: scenario == "cache" ? EvolutionCacheStatus.Hit : EvolutionCacheStatus.Miss,
            attempts: scenario == "no-attempt" ? 0 : 1, infeasible: scenario == "infeasible",
            diagnostic: scenario == "unknown-evaluation-cost" ? "resource_cost_unknown" : null),
            scenario == "not-inserted" ? null : EvolutionArchiveInsertionResult.Inserted);
        Assert.Equal(0, portfolio.Statistics[0].RewardSum); Assert.Equal(2, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task Parent_baseline_and_pending_costs_restore_before_out_of_order_feedback()
    {
        var ledger = Ledger(); var source = new Source(); var first = new AdaptiveVariationPortfolio<TestGenome>(new[] { Meter(source, ledger) }, rewardPolicy: Policy());
        await first.ProposeAsync(Context(1, 0.2)); await first.ProposeAsync(Context(2, 0.5));
        var restoredLedger = Ledger(); restoredLedger.RestoreState(ledger.CaptureState());
        var second = new AdaptiveVariationPortfolio<TestGenome>(new[] { Meter(new Source(), restoredLedger) }, rewardPolicy: Policy());
        second.RestoreState(first.CaptureState()); Assert.Equal(first.CaptureState(), second.CaptureState());
        foreach (var portfolio in new[] { first, second })
        {
            portfolio.Observe(Evaluation(2, 0.9), EvolutionArchiveInsertionResult.Replaced);
            portfolio.Observe(Evaluation(1, 0.8), EvolutionArchiveInsertionResult.Replaced);
        }
        Assert.Equal(0.25, first.Statistics[0].RewardSum, 12); Assert.Equal(first.CaptureState(), second.CaptureState());
        Assert.Equal(ledger.CaptureState(), restoredLedger.CaptureState());
    }

    [Fact]
    public async Task Same_gain_prefers_lower_total_cost_while_preserving_exploration()
    {
        var ledger = Ledger();
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { Meter(new Source("cheap", 1), ledger), Meter(new Source("expensive", 4), ledger) },
            explorationProbability: 0.2, rewardPolicy: Policy());
        for (int generation = 1; generation <= 100; generation++)
        {
            await portfolio.ProposeAsync(Context(generation)); portfolio.Observe(Evaluation(generation, 1, 1), EvolutionArchiveInsertionResult.Inserted);
        }
        Assert.True(portfolio.Statistics[0].Proposals > portfolio.Statistics[1].Proposals);
        Assert.True(portfolio.Statistics[1].Proposals > 1);
        Assert.Equal(portfolio.Statistics[0].Proposals + 4 * portfolio.Statistics[1].Proposals, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task Denied_failed_unknown_and_overrun_proposals_keep_distinct_receipts()
    {
        foreach (string scenario in new[] { "denied", "failed", "unknown", "overrun", "null" })
        {
            var ledger = Ledger(scenario == "denied" ? 0 : 100); var source = new Source();
            source.Work = _ => scenario == "unknown" ? throw new EvolutionResourceBudgetException("nested") :
                new(new EvolutionResourceResult<TestGenome>(scenario == "null" ? null! : new TestGenome(3), EvolutionResources.Of("cost_units", scenario == "overrun" ? 6 : 2),
                    scenario == "failed" ? EvolutionResourceOutcome.Failed : EvolutionResourceOutcome.Completed));
            var metered = Meter(source, ledger);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await metered.ProposeAsync(Context(1)));
            EvolutionProposalCost receipt = metered.GetProposalCost(1);
            Assert.Equal(scenario == "denied" ? 0 : scenario == "unknown" ? 5 : scenario == "overrun" ? 6 : 2, receipt.Charged["cost_units"]);
            Assert.Equal(scenario == "unknown", receipt.IsUnknown); Assert.Equal(scenario == "overrun", receipt.ExceededMaximum);
            string checkpoint = metered.CaptureState(); var copy = Meter(new Source(), ledger); copy.RestoreState(checkpoint); Assert.Equal(checkpoint, copy.CaptureState());
            metered.Observe(Evaluation(1, 0, status: EvolutionEvaluationStatus.Failed), null);
            Assert.Equal(scenario == "denied" ? 0 : 1, source.Outcomes);
            Assert.Equal(receipt.Charged["cost_units"], ledger.Snapshot().Spent["cost_units"]);
        }
    }

    [Fact]
    public async Task Mid_dispatch_checkpoint_and_concurrent_use_are_rejected_without_extra_admission()
    {
        var pending = new TaskCompletionSource<EvolutionResourceResult<TestGenome>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Source { Work = _ => new(pending.Task) }; var ledger = Ledger(); var metered = Meter(source, ledger);
        Task<TestGenome> first = metered.ProposeAsync(Context(1)).AsTask();
        Assert.Throws<InvalidOperationException>(() => metered.CaptureState());
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await metered.ProposeAsync(Context(2)));
        Assert.Equal(1, ledger.Snapshot().Admitted);
        pending.SetResult(new(new TestGenome(3), EvolutionResources.Of("cost_units", 2))); await first;
        Assert.NotEmpty(metered.CaptureState());
    }

    [Fact]
    public async Task Invalid_restored_cost_or_parent_state_is_rejected_before_mutating_learning()
    {
        var metered = Meter(new Source(), Ledger());
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { metered }, rewardPolicy: Policy());
        await portfolio.ProposeAsync(Context(1)); string saved = portfolio.CaptureState();
        var state = JsonNode.Parse(saved)!.AsObject(); state["Credit"] = new JsonObject();
        Assert.Throws<InvalidDataException>(() => portfolio.RestoreState(state.ToJsonString())); Assert.Equal(saved, portfolio.CaptureState());
        string costState = metered.CaptureState(); var bad = JsonNode.Parse(costState)!.AsObject(); bad["Pending"]!["1"]!["Charged"]!["cost_units"] = -1;
        Assert.Throws<InvalidDataException>(() => metered.RestoreState(bad.ToJsonString())); Assert.Equal(costState, metered.CaptureState());
        bad = JsonNode.Parse(costState)!.AsObject(); bad["Pending"]!["1"]!["Dispatched"] = false;
        Assert.Throws<InvalidDataException>(() => metered.RestoreState(bad.ToJsonString())); Assert.Equal(costState, metered.CaptureState());
    }

    [Fact]
    public void Policies_reject_missing_units_missing_cost_providers_and_invalid_scales()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(scale: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(scale: double.NaN));
        Assert.Throws<ArgumentException>(() => new AdaptiveVariationPortfolio<TestGenome>(new[] { new IncrementVariation() }, rewardPolicy: Policy()));
        var metered = Meter(new Source(), Ledger());
        Assert.Throws<ArgumentException>(() => new AdaptiveVariationPortfolio<TestGenome>(new[] { metered }, rewardPolicy:
            new EvolutionOperatorRewardPolicy(EvolutionOperatorRewardKind.ArchiveSuccess, EvolutionOperatorCostBasis.ProposalAndEvaluation, "different-units")));
        var original = new AdaptiveVariationPortfolio<TestGenome>(new[] { new IncrementVariation() });
        Assert.NotNull(typeof(AdaptiveVariationPortfolio<TestGenome>).GetConstructor(new[] { typeof(IEnumerable<IVariationOperator<TestGenome>>), typeof(double) }));
        Assert.DoesNotContain("Credit", original.CaptureState());
        var configured = new AdaptiveVariationPortfolio<TestGenome>(new[] { new IncrementVariation() }, rewardPolicy: Policy(cost: EvolutionOperatorCostBasis.Evaluation));
        Assert.NotEqual(original.VersionHash, configured.VersionHash); Assert.Throws<InvalidDataException>(() => configured.RestoreState(original.CaptureState()));
    }

    [Fact]
    public async Task Extreme_finite_scores_and_costs_cannot_produce_nonfinite_or_unbounded_reward()
    {
        var policy = Policy(cost: EvolutionOperatorCostBasis.Evaluation, scale: double.Epsilon);
        var portfolio = new AdaptiveVariationPortfolio<TestGenome>(new[] { new IncrementVariation() }, rewardPolicy: policy);
        await portfolio.ProposeAsync(Context(1, -double.MaxValue));
        portfolio.Observe(Evaluation(1, double.MaxValue, double.MaxValue), EvolutionArchiveInsertionResult.Replaced);
        Assert.InRange(portfolio.Statistics[0].RewardSum, 0, 1); Assert.False(double.IsNaN(portfolio.Statistics[0].RewardSum));
    }

    [Fact]
    public async Task Engine_boundary_resume_restores_credit_backend_and_ledger_together()
    {
        AdaptiveVariationPortfolio<TestGenome> Create(EvolutionResourceLedger ledger) => new(new[] { Meter(new Source(), ledger) }, rewardPolicy: Policy(scale: 10));
        var checkpoint = new InMemoryEvolutionCheckpointStore(); var firstLedger = Ledger(); var first = Create(firstLedger);
        await EvolutionVariationOutcomeTests.Run(first, EvolutionVariationOutcomeTests.Options(8), checkpoint);
        var resumedLedger = Ledger(); resumedLedger.RestoreState(firstLedger.CaptureState()); var resumed = Create(resumedLedger);
        var options = EvolutionVariationOutcomeTests.Options(16); options.Resume = true;
        var actual = await EvolutionVariationOutcomeTests.Run(resumed, options, checkpoint);
        var fullLedger = Ledger(); var full = Create(fullLedger);
        var expected = await EvolutionVariationOutcomeTests.Run(full, EvolutionVariationOutcomeTests.Options(16), new InMemoryEvolutionCheckpointStore());
        Assert.Equal(expected.StateHash, actual.StateHash); Assert.Equal(full.CaptureState(), resumed.CaptureState());
        Assert.Equal(fullLedger.CaptureState(), resumedLedger.CaptureState());
        Assert.Equal(30, resumedLedger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task Cancellation_and_missing_cost_fields_cannot_hide_admitted_proposal_work()
    {
        var ledger = Ledger(); var source = new Source { Work = _ => throw new OperationCanceledException() }; var metered = Meter(source, ledger);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await metered.ProposeAsync(Context(1), canceled.Token));
        Assert.Equal(0, ledger.Snapshot().Admitted);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await metered.ProposeAsync(Context(1)));
        Assert.True(metered.GetProposalCost(1).IsUnknown); Assert.Equal(5, ledger.Snapshot().Spent["cost_units"]);
        source.Work = _ => new(new EvolutionResourceResult<TestGenome>(new TestGenome(3), EvolutionResources.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await metered.ProposeAsync(Context(2)));
        Assert.True(metered.GetProposalCost(2).IsUnknown); Assert.Equal(10, ledger.Snapshot().Spent["cost_units"]);
        Assert.Throws<ArgumentException>(() => new EvolutionProposalCost("missing", EvolutionResources.Empty, EvolutionResourceOutcome.Completed));
    }
}
