using System.Globalization;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class EvolutionPolicyEngineTests
{
    private const string Units = "integer-evaluation-work-v1";

    private sealed class NumericTask(string id, Action disposed, EvolutionOptimizationDirection direction,
        bool feedback = false) : IEvolutionTask<TestGenome>, IDisposable
    {
        public string Id => id;
        public string VersionHash => "integer-v1";
        public string EvaluatorVersionHash => "quality-v1";
        public ValueTask<EvolutionCanonicalGenome<TestGenome>> CanonicalizeAsync(TestGenome genome, CancellationToken cancellationToken = default) =>
            new(new EvolutionCanonicalGenome<TestGenome>(new TestGenome(genome.Value), genome.Value.ToString(CultureInfo.InvariantCulture)));
        public ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<TestGenome> candidate,
            EvolutionEvaluationContext context, CancellationToken cancellationToken = default) =>
            new(new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, direction == EvolutionOptimizationDirection.Maximize ?
                candidate.CanonicalGenome.Genome.Value : 100 - candidate.CanonicalGenome.Genome.Value, direction,
                new Dictionary<string, double> { ["x"] = 0.5 }, costUnits: 2,
                artifacts: feedback ? new[] { new EvolutionArtifact("feedback", "measured-feedback") } : null));
        public void Dispose() => disposed();
    }

    private sealed class Source(string id, Action disposed, Action<EvolutionVariationContext<TestGenome>>? inspect = null) :
        ICostedEvolutionProposalSource<TestGenome>, IDisposable
    {
        public string Id => id;
        public string VersionHash => "operator-v1";
        public ValueTask<EvolutionResourceResult<TestGenome>> ProposeAsync(EvolutionVariationContext<TestGenome> context,
            CancellationToken cancellationToken = default)
        {
            inspect?.Invoke(context);
            return new(new EvolutionResourceResult<TestGenome>(new TestGenome(context.Parent.Candidate.CanonicalGenome.Genome.Value + 1),
                EvolutionResources.Of("cost_units", 1)));
        }
        public void Observe(EvolutionEvaluation evaluation, EvolutionArchiveInsertionResult? insertionResult) { }
        public string CaptureState() => "stateless";
        public void RestoreState(string state) { if (state != "stateless") throw new InvalidDataException(); }
        public void Dispose() => disposed();
    }

    private static EvolutionPolicyOptimizer Campaign(Action disposedTask, Action disposedSource,
        int restart = 0, EvolutionPolicyContext context = EvolutionPolicyContext.ParentOnly,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize,
        Action<EvolutionVariationContext<TestGenome>>? inspect = null, bool excessSeeds = false, bool changedOperator = false,
        decimal costCap = 100)
    {
        var policies = new[] { EvolutionPolicySelectionSchedule.Uniform, EvolutionPolicySelectionSchedule.Greedy,
            EvolutionPolicySelectionSchedule.ExploreThenExploit }.Select(schedule => new EvolutionSearchPolicy(
                new Dictionary<string, int> { ["increment"] = 1 }, schedule, restart, context)).ToArray();
        EvolutionPolicyTrial Trial(string id) => EvolutionPolicyEngineTrial.Create(id, id, "integer-v1", "quality-v1",
            () => new NumericTask(id, disposedTask, direction, feedback: true), (_, cap) =>
                Enumerable.Range(0, excessSeeds ? cap + 1 : 1).Select(value => new TestGenome(value)),
            new[] { new EvolutionPolicyEngineOperator<TestGenome>("increment", "operator-v1", EvolutionResources.Of("cost_units", 1),
                () => new Source(changedOperator ? "changed" : "increment", disposedSource, inspect)) },
            new[] { new EvolutionDescriptorDefinition("x", 0, 1, 4) }, new TestGenomeCodec(), direction,
            direction == EvolutionOptimizationDirection.Maximize ? 0 : 100,
            direction == EvolutionOptimizationDirection.Maximize ? 10 : 90, 2, Units);
        var options = new EvolutionPolicyOptimizationOptions(new EvolutionPolicyTrialBudget(6, 6, 2, TimeSpan.FromSeconds(10),
            EvolutionResources.Of("cost_units", costCap)), EvolutionResources.Of("cost_units", 10000),
            maximumDevelopmentPolicies: 3, maximumOuterProposals: 128, replicates: 2);
        return new(new EvolutionPolicySpace(policies), options,
            new[] { Trial("dev-a"), Trial("dev-b") }, new[] { Trial("holdout-a"), Trial("holdout-b") },
            policies.Take(2).Select((policy, index) => new EvolutionPolicyBaseline("manual-" + index, policy,
                "Synthetic same-operator schedule control.", EvolutionHash.Compute("control"), EvolutionResources.Of("cost_units", 0), Units)), policies[0]);
    }

    [Theory]
    [InlineData(0, 0, 17)]
    [InlineData(2, 2, 15)]
    [InlineData(1, 2, 15)]
    public async Task RealEngineMetersAllSegmentsWithoutResettingTotalBudget(int interval, int expectedRestarts, int expectedCost)
    {
        int tasks = 0, sources = 0;
        var report = await Campaign(() => tasks++, () => sources++, restart: interval).RunAsync();
        Assert.Equal(EvolutionPolicyCampaignOutcome.NoDevelopmentImprovement, report.Outcome);
        Assert.Equal(12, report.Trials.Count);
        Assert.All(report.Trials, trial =>
        {
            Assert.Equal(EvolutionResourceOutcome.Completed, trial.Outcome);
            Assert.Equal(6m, trial.ChargedResources["policy_inner_evaluations"]);
            Assert.Equal(6m, trial.ChargedResources["policy_inner_proposals"]);
            Assert.Equal((decimal)expectedRestarts, trial.ChargedResources["policy_inner_restarts"]);
            Assert.Equal((decimal)expectedCost, trial.ChargedResources["cost_units"]);
            Assert.NotNull(trial.Observation!.EvidenceJson);
            Assert.Equal(EvolutionHash.Compute(trial.Observation.EvidenceJson!), trial.Observation.EvidenceHash);
        });
        Assert.Equal(12 * (expectedRestarts + 1), tasks);
        Assert.Equal(tasks, sources);
    }

    [Theory]
    [InlineData(EvolutionPolicyContext.ParentOnly)]
    [InlineData(EvolutionPolicyContext.Inspirations)]
    [InlineData(EvolutionPolicyContext.FeedbackAndInspirations)]
    public async Task OperatorContextCannotBypassPolicyThroughArchiveEntries(EvolutionPolicyContext context)
    {
        int calls = 0;
        var report = await Campaign(() => { }, () => { }, context: context, inspect: value =>
        {
            calls++;
            Assert.Empty(value.Parent.Evaluation.Artifacts);
            Assert.Empty(value.Parent.Evaluation.Diagnostics);
            Assert.Null(value.Archive);
            Assert.All(value.Inspirations, entry => Assert.Empty(entry.Evaluation.Artifacts));
            if (context == EvolutionPolicyContext.ParentOnly) Assert.Empty(value.Inspirations);
            if (context != EvolutionPolicyContext.FeedbackAndInspirations) Assert.Empty(value.ParentArtifacts);
            else Assert.All(value.ParentArtifacts, artifact => Assert.True(artifact.SizeBytes <= 4096));
        }).RunAsync();
        Assert.Equal(EvolutionPolicyCampaignOutcome.NoDevelopmentImprovement, report.Outcome);
        Assert.True(calls > 0);
    }

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize)]
    [InlineData(EvolutionOptimizationDirection.Minimize)]
    public async Task FixedNormalizationWorksForEitherDirection(EvolutionOptimizationDirection direction)
    {
        var report = await Campaign(() => { }, () => { }, direction: direction).RunAsync();
        Assert.Equal(EvolutionPolicyCampaignOutcome.NoDevelopmentImprovement, report.Outcome);
        Assert.All(report.Trials, trial => Assert.Equal(0.5, trial.Observation!.Utility));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task InvalidFactoriesAreDisposedAndCannotQualify(bool excessSeeds, bool changedOperator)
    {
        int tasks = 0, sources = 0;
        var report = await Campaign(() => tasks++, () => sources++, excessSeeds: excessSeeds, changedOperator: changedOperator).RunAsync();
        Assert.Equal(EvolutionPolicyCampaignOutcome.Failed, report.Outcome);
        Assert.Single(report.Trials);
        Assert.Equal(1, tasks);
        Assert.Equal(1, sources);
        Assert.Equal(100m, report.Resources.Spent["cost_units"]);
        Assert.False(report.GeneralizationPassed);
    }

    [Fact]
    public async Task InnerResourceDenialCannotUsePartialQualityAsSuccess()
    {
        var report = await Campaign(() => { }, () => { }, costCap: 2).RunAsync();
        Assert.Single(report.Trials);
        Assert.False(report.GeneralizationPassed);
        Assert.False(report.Trials[0].Observation!.IsValid);
    }

    [Fact]
    public async Task ProposalFailureCannotQualifyUsingEarlierSeedQuality()
    {
        var report = await Campaign(() => { }, () => { }, inspect: _ => throw new InvalidOperationException("synthetic proposal failure")).RunAsync();
        Assert.Single(report.Trials);
        Assert.False(report.GeneralizationPassed);
        Assert.False(report.Trials[0].Observation!.IsValid);
        Assert.True(report.Trials[0].Observation!.HasUnknownConsumption);
    }

    [Fact]
    public void MixtureImplementsEngineOutcomeDispatchAndRoundTripsSettledState()
    {
        var policy = new EvolutionSearchPolicy(new Dictionary<string, int> { ["increment"] = 1 });
        var definitions = new[] { new EvolutionPolicyEngineOperator<TestGenome>("increment", "operator-v1",
            EvolutionResources.Of("cost_units", 1), () => new Source("increment", () => { })) };
        var ledger = new EvolutionResourceLedger("state-test", EvolutionResources.Of("cost_units", 100));
        var owned = new List<IDisposable>();
        try
        {
            var mixture = new PolicyEngineMixture<TestGenome>(policy, definitions, ledger, Units, owned);
            Assert.IsAssignableFrom<IOutcomeAwareVariationOperator<TestGenome>>(mixture);
            string state = mixture.CaptureState();
            mixture.RestoreState(state);
            Assert.Equal(state, mixture.CaptureState());
            Assert.Throws<InvalidDataException>(() => mixture.RestoreState("{}"));
            Assert.Throws<InvalidDataException>(() => mixture.RestoreState(state.Replace(mixture.VersionHash, EvolutionHash.Compute("different"))));
        }
        finally { foreach (var resource in owned) resource.Dispose(); }
    }

    private sealed class BrokenCleanup(Exception error, Action called) : IDisposable
    {
        public void Dispose() { called(); throw error; }
    }

    [Fact]
    public async Task CleanupPreservesOriginalFatalFailureAndAttemptsAllOwnedDisposals()
    {
        var original = new OutOfMemoryException("synthetic fatal classification");
        var order = new List<int>();
        var thrown = await Assert.ThrowsAsync<AggregateException>(() => PolicyOwnedResources.RunAsync<int>(owned =>
        {
            owned.Add(new BrokenCleanup(new IOException("first"), () => order.Add(1)));
            owned.Add(new BrokenCleanup(new IOException("second"), () => order.Add(2)));
            return Task.FromException<int>(original);
        }));
        Assert.Same(original, thrown.InnerExceptions[0]);
        Assert.Equal(new[] { 2, 1 }, order);
        Assert.False(EvolutionExceptionPolicy.IsRecoverable(thrown));
    }
}
