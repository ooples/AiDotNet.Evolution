using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionPolicyOptimizerTests
{
    private const string Units = "policy-test-work-v1";
    private static readonly EvolutionSearchPolicy Stable = Policy(1, 0);
    private static readonly EvolutionSearchPolicy Manual = Policy(0, 1);
    private static readonly EvolutionSearchPolicy Candidate = Policy(1, 1);
    private static EvolutionSearchPolicy Policy(int a, int b) => new(new Dictionary<string, int> { ["a"] = a, ["b"] = b });
    private static EvolutionResources Charges(decimal cost = 1, decimal evaluations = 1) => new(new Dictionary<string, decimal>
    {
        ["cost_units"] = cost,
        ["policy_inner_evaluations"] = evaluations,
        ["policy_inner_proposals"] = 1,
        ["policy_inner_restarts"] = 0
    });
    private static EvolutionPolicyObservation Receipt(double utility = 0.5, EvolutionResources? resources = null,
        bool fresh = true, bool valid = true, bool unknown = false) =>
        new(utility, resources ?? Charges(), EvolutionHash.Compute("{}"), valid, fresh, evidenceJson: "{}", unknownConsumption: unknown);

    private static EvolutionPolicyOptimizer Campaign(
        Func<bool, int, EvolutionSearchPolicy, ulong, CancellationToken, Task<EvolutionPolicyObservation>>? run = null,
        int heldOutFamilies = 6, int replicates = 2, decimal cap = 10000, TimeSpan? trialTimeout = null,
        string? heldOutPrefix = null, string baselineUnits = Units, int maximumTrials = 1024, int evidenceLimit = 16 * 1024 * 1024)
    {
        EvolutionPolicyTrial TaskDefinition(bool heldout, int index)
        {
            string id = (heldout ? "heldout-" : "development-") + index;
            string family = (heldout ? heldOutPrefix ?? "heldout-" : "development-") + index;
            return new(id, family, EvolutionHash.Compute(id), Units, (policy, _, seed, token) =>
                run is null ? Task.FromResult(Receipt(policy.Id == Candidate.Id ? 0.9 : policy.Id == Manual.Id ? 0.6 : 0.5)) :
                run(heldout, index, policy, seed, token));
        }
        var budget = new EvolutionPolicyTrialBudget(10, 10, 2, trialTimeout ?? TimeSpan.FromSeconds(10), EvolutionResources.Of("cost_units", 10));
        var options = new EvolutionPolicyOptimizationOptions(budget, EvolutionResources.Of("cost_units", cap),
            maximumDevelopmentPolicies: 3, maximumOuterProposals: 128, replicates: replicates, maximumInnerTrials: maximumTrials,
            maximumInlineEvidenceBytes: evidenceLimit);
        var baselines = new[] { Stable, Manual }.Select((policy, index) => new EvolutionPolicyBaseline("manual-" + index,
            policy, "Synthetic baseline for protocol verification, not a competitive performance claim.",
            EvolutionHash.Compute("manual-" + index), EvolutionResources.Of("cost_units", 7), baselineUnits));
        return new(new EvolutionPolicySpace(new[] { Stable, Manual, Candidate }), options,
            Enumerable.Range(0, 2).Select(index => TaskDefinition(false, index)),
            Enumerable.Range(0, heldOutFamilies).Select(index => TaskDefinition(true, index)), baselines, Stable);
    }

    [Fact]
    public void ProportionalMixturesAreOnePolicyAndInputsAreDetached()
    {
        var weights = new Dictionary<string, int> { ["a"] = 2, ["b"] = 2 };
        var policy = new EvolutionSearchPolicy(weights);
        weights["a"] = 9;
        Assert.Equal(Candidate.Id, policy.Id);
        Assert.Equal(1, policy.OperatorWeights["a"]);
        Assert.Throws<ArgumentException>(() => new EvolutionPolicySpace(new[] { Candidate, policy }));
        Assert.Throws<ArgumentException>(() => Policy(0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Policy(-1, 1));
    }

    [Fact]
    public void CatalogueFreezesAllFourDimensionsAndSamplesReproducibly()
    {
        var policies = new List<EvolutionSearchPolicy>();
        foreach (var schedule in new[] { EvolutionPolicySelectionSchedule.Uniform, EvolutionPolicySelectionSchedule.Greedy })
            foreach (int restart in new[] { 0, 4 })
                foreach (var context in new[] { EvolutionPolicyContext.ParentOnly, EvolutionPolicyContext.Inspirations })
                    foreach (int weight in new[] { 1, 2 })
                        policies.Add(new(new Dictionary<string, int> { ["a"] = weight, ["b"] = 1 }, schedule, restart, context));
        var space = new EvolutionPolicySpace(policies);
        var left = new StableRandom(14); var right = new StableRandom(14);
        for (int index = 0; index < 100; index++)
        {
            var proposed = space.Mutate(policies[0], left);
            Assert.Equal(proposed.Id, space.Mutate(policies[0], right).Id);
            Assert.True(space.Contains(proposed));
            Assert.NotEqual(policies[0].Id, proposed.Id);
        }
        policies.Clear();
        Assert.Equal(16, space.Policies.Count);
    }

    [Fact]
    public async Task IndependentFamilyWinsPassBothBaselinesAndAccountEveryTrial()
    {
        var report = await Campaign().RunAsync();
        Assert.True(report.GeneralizationPassed);
        Assert.Equal(Candidate.Id, report.SuggestedPolicy.Id);
        Assert.Equal(48, report.Trials.Count);
        Assert.Equal(48m, report.Resources.Spent["cost_units"]);
        Assert.Equal(14m, report.PriorTuningResources["cost_units"]);
        Assert.Equal(2, report.Comparisons.Count);
        Assert.All(report.Comparisons, comparison => Assert.True(comparison.Passed));
        Assert.All(report.Trials, trial => Assert.Equal(EvolutionResourceOutcome.Completed, trial.Outcome));
        Assert.Contains("heldout-5", report.ToJson());
        Assert.Contains(EvolutionHash.Compute("{}"), report.ToJson());
        Assert.True(report.Resources.Spent["policy_outer_proposals"] >= 3);
    }

    [Fact]
    public async Task ReplicatesDoNotMasqueradeAsIndependentFamilies()
    {
        var report = await Campaign(heldOutFamilies: 2, replicates: 32).RunAsync();
        Assert.Equal("GeneralizationRejected", report.Outcome);
        Assert.False(report.GeneralizationPassed);
        Assert.Equal(Stable.Id, report.SuggestedPolicy.Id);
        Assert.All(report.Comparisons, comparison => Assert.False(comparison.Passed));
    }

    [Fact]
    public async Task HoldoutCannotChangeDevelopmentChampionOrRescueOverfitPolicy()
    {
        bool holdoutStarted = false;
        var report = await Campaign((heldout, _, policy, _, _) =>
        {
            if (heldout) holdoutStarted = true;
            else Assert.False(holdoutStarted);
            return Task.FromResult(Receipt(policy.Id == Candidate.Id ? heldout ? 0.1 : 0.9 : 0.6));
        }).RunAsync();
        Assert.Equal(Candidate.Id, report.DevelopmentChampion!.Id);
        Assert.Equal("GeneralizationRejected", report.Outcome);
        Assert.Equal(Stable.Id, report.SuggestedPolicy.Id);
    }

    [Fact]
    public async Task NoDevelopmentGainSpendsNothingOnHoldout()
    {
        var report = await Campaign((heldout, _, _, _, _) =>
        {
            Assert.False(heldout);
            return Task.FromResult(Receipt());
        }).RunAsync();
        Assert.Equal("NoDevelopmentImprovement", report.Outcome);
        Assert.Equal(12, report.Trials.Count);
        Assert.Empty(report.Comparisons);
    }

    [Fact]
    public async Task PoliciesShareMatchedSeedsAndReplaysKeepPlanAndDispatchIdentity()
    {
        var first = await Campaign().RunAsync();
        var second = await Campaign().RunAsync();
        Assert.Equal(first.PlanHash, second.PlanHash);
        Assert.Equal(first.Trials.Select(value => (value.PolicyId, value.Seed)), second.Trials.Select(value => (value.PolicyId, value.Seed)));
        foreach (var block in first.Trials.GroupBy(value => (value.Phase, value.TaskId, value.Replicate)))
            Assert.Single(block.Select(value => value.Seed).Distinct());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("fractional")]
    [InlineData("undeclared")]
    [InlineData("null")]
    public async Task MalformedReceiptStopsCampaignAndChargesUnknownReservation(string kind)
    {
        var report = await Campaign((_, _, _, _, _) =>
        {
            var resources = kind == "missing" ? EvolutionResources.Of("cost_units", 1) :
                kind == "fractional" ? Charges(evaluations: 0.5m) :
                new EvolutionResources(Charges().Amounts.Concat(new[] { new KeyValuePair<string, decimal>("secret_units", 1) }));
            return Task.FromResult(kind == "null" ? null! : Receipt(resources: resources));
        }).RunAsync();
        Assert.Single(report.Trials);
        Assert.Equal(EvolutionResourceOutcome.Unknown, report.Trials[0].Outcome);
        Assert.Equal(10m, report.Resources.Spent["cost_units"]);
        Assert.Equal(Stable.Id, report.SuggestedPolicy.Id);
    }

    [Theory]
    [InlineData(false, true, false, 1)]
    [InlineData(true, false, false, 1)]
    [InlineData(true, false, true, 1)]
    [InlineData(true, true, false, 11)]
    public async Task ReusedInvalidUnknownAndOverBudgetTrialsCannotQualify(bool fresh, bool valid, bool unknown, int cost)
    {
        var report = await Campaign((_, _, _, _, _) => Task.FromResult(Receipt(resources: Charges(cost), fresh: fresh, valid: valid, unknown: unknown))).RunAsync();
        Assert.Single(report.Trials);
        Assert.False(report.GeneralizationPassed);
        Assert.Equal(Stable.Id, report.SuggestedPolicy.Id);
        Assert.Equal(unknown ? 10m : cost, report.Resources.Spent["cost_units"]);
    }

    [Fact]
    public async Task DenialHappensBeforeProviderDispatch()
    {
        int calls = 0;
        var report = await Campaign((_, _, _, _, _) => { calls++; return Task.FromResult(Receipt()); }, cap: 9).RunAsync();
        Assert.Equal(0, calls);
        Assert.Equal("BudgetDenied", report.Outcome);
        Assert.Equal(0m, report.Resources.Spent["cost_units"]);
        Assert.Single(report.Trials);
    }

    [Fact]
    public async Task UncooperativeProviderIsNotReplacedOrAdoptedAfterDeadline()
    {
        var pending = new TaskCompletionSource<EvolutionPolicyObservation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var campaign = Campaign((_, _, _, _, _) => { Interlocked.Increment(ref calls); entered.TrySetResult(true); return pending.Task; },
            trialTimeout: TimeSpan.FromSeconds(1));
        try
        {
            Task<EvolutionPolicyOptimizationReport> running = campaign.RunAsync();
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(5))));
            Assert.Same(running, await Task.WhenAny(running, Task.Delay(TimeSpan.FromSeconds(5))));
            var report = await running;
            Assert.Equal("InnerTimedOut", report.Outcome);
            Assert.True(campaign.HasOutstandingWork);
            Assert.Equal(1, calls);
            Assert.Equal(10m, report.Resources.Spent["cost_units"]);
            Assert.Equal(Stable.Id, report.SuggestedPolicy.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => campaign.RunAsync());
            pending.SetResult(Receipt(1));
            Assert.False(report.GeneralizationPassed);
            Assert.Same(report, campaign.LastReport);
        }
        finally { pending.TrySetResult(Receipt()); }
    }

    [Fact]
    public async Task FatalNestedProviderFailurePropagatesButRetainsUnknownCosts()
    {
        var fatal = new AggregateException(new OutOfMemoryException("synthetic fatal classification"));
        var campaign = Campaign((_, _, _, _, _) => Task.FromException<EvolutionPolicyObservation>(fatal));
        Assert.Same(fatal, await Assert.ThrowsAsync<AggregateException>(() => campaign.RunAsync()));
        Assert.NotNull(campaign.LastReport);
        Assert.Equal(10m, campaign.LastReport!.Resources.Spent["cost_units"]);
        Assert.Equal(Stable.Id, campaign.LastReport.SuggestedPolicy.Id);
    }

    [Fact]
    public void PreflightRejectsLeakedFamiliesDifferentUnitsAndUnfundedHoldoutPanel()
    {
        Assert.Throws<ArgumentException>(() => Campaign(heldOutPrefix: "DEVELOPMENT-"));
        Assert.Throws<ArgumentException>(() => Campaign(baselineUnits: "different-units"));
        Assert.Throws<ArgumentException>(() => Campaign(maximumTrials: 40));
    }

    [Fact]
    public void EvidenceMustBeFiniteBoundedAndHashVerified()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Receipt(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Receipt(1.1));
        Assert.Throws<ArgumentException>(() => new EvolutionPolicyObservation(0.5, Charges(), EvolutionHash.Compute("wrong"), evidenceJson: "{}"));
        Assert.Throws<ArgumentException>(() => new EvolutionPolicyObservation(null, Charges(), EvolutionHash.Compute("{}")));
        Assert.Throws<ArgumentException>(() => Receipt(unknown: true));
    }

    [Fact]
    public async Task EvidenceLimitRetainsRejectedReceiptAndKnownCosts()
    {
        string evidence = "{\"raw\":\"" + new string('x', 70000) + "\"}";
        var report = await Campaign((_, _, _, _, _) => Task.FromResult(new EvolutionPolicyObservation(0.5, Charges(),
            EvolutionHash.Compute(evidence), evidenceJson: evidence)), evidenceLimit: 128 * 1024).RunAsync();
        Assert.Equal("EvidenceLimit", report.Outcome);
        Assert.Equal(2, report.Trials.Count);
        Assert.Equal(2m, report.Resources.Spent["cost_units"]);
        Assert.False(report.GeneralizationPassed);
        Assert.All(report.Trials, value => Assert.Equal(evidence, value.Observation!.EvidenceJson));
    }

    [Fact]
    public async Task CandidateMustBeatEveryRegisteredBaseline()
    {
        var report = await Campaign((heldout, _, policy, _, _) => Task.FromResult(Receipt(
            policy.Id == Candidate.Id ? 0.8 : heldout && policy.Id == Manual.Id ? 0.9 : 0.5))).RunAsync();
        Assert.Equal("GeneralizationRejected", report.Outcome);
        Assert.Single(report.Comparisons, value => value.Passed);
        Assert.Equal(Stable.Id, report.SuggestedPolicy.Id);
    }

    [Fact]
    public async Task HeldOutInstabilityRejectsEvenPositiveFamilyMeans()
    {
        var calls = new Dictionary<string, int>();
        var report = await Campaign((heldout, index, policy, _, _) =>
        {
            string key = heldout + "/" + index + "/" + policy.Id;
            int ordinal = calls.TryGetValue(key, out int previous) ? previous : 0;
            calls[key] = ordinal + 1;
            return Task.FromResult(Receipt(policy.Id == Candidate.Id ? heldout ? ordinal == 0 ? 0.1 : 1 : 0.9 : 0.05));
        }).RunAsync();
        Assert.Equal("GeneralizationRejected", report.Outcome);
        Assert.All(report.Comparisons, value => Assert.False(value.Passed));
    }

    [Fact]
    public async Task PreAdmissionCancellationDoesNotConsumeSingleUseCampaign()
    {
        var campaign = Campaign();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => campaign.RunAsync(canceled.Token));
        Assert.Null(campaign.LastReport);
        Assert.True((await campaign.RunAsync()).GeneralizationPassed);
    }
}
