using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionReplicationTests
{
    private static EvolutionCanonicalGenome<int> Candidate => new(7, "candidate-7");
    private static EvolutionEvaluationContext Context(int attempt = 1) => new(12, 42, 71, attempt);
    private static EvolutionResourceLedger Ledger(decimal cap = 1000) => new("replication", EvolutionResources.Of("cost_units", cap));
    private static EvolutionTaskResult Value(double quality, double cost = 1) =>
        EvolutionTaskResult.Completed(quality, new Dictionary<string, double>(), costUnits: cost);
    private static EvolutionReplicationPlan Plan(int maximum = 4, double target = 0) => new(2, maximum, 0, 1, 1, normalizedWidthTarget: target);

    [Fact]
    public async Task FixedReplicatesReportMeanVarianceAndFreshCost()
    {
        var ledger = Ledger();
        var runner = new EvolutionReplicateRunner<int>("evaluator-v1", Plan(), ledger, (_, context, _) =>
            new ValueTask<EvolutionTaskResult>(Value(context.Index % 2)));
        var report = await runner.RunAsync(Candidate, Context(), "batch-1");
        Assert.True(report.IsComplete); Assert.Equal(EvolutionReplicationStopReason.Completed, report.StopReason);
        Assert.Equal(4, report.Samples.Count); Assert.Equal(0.5, report.MeanQuality);
        Assert.Equal(1d / 3, report.NormalizedSampleVariance!.Value, 12);
        Assert.Equal(Math.Sqrt(1d / 12), report.StandardError!.Value, 12);
        Assert.Equal(4, report.ChargedCostUnits); Assert.Equal(4, ledger.Snapshot().Spent["cost_units"]);
        Assert.All(report.Samples, sample => Assert.False(sample.UnknownCost));
    }

    [Fact]
    public async Task IdenticalObservationsDoNotManufactureZeroUncertainty()
    {
        var runner = new EvolutionReplicateRunner<int>("fixed", Plan(), Ledger(), (_, _, _) => new(Value(0.5)));
        var report = await runner.RunAsync(Candidate, Context(), "fixed");
        Assert.Equal(0, report.StandardError); Assert.True(report.LowerBound < report.MeanQuality);
        Assert.True(report.UpperBound > report.MeanQuality);
    }

    [Fact]
    public async Task PrecisionStoppingUsesThePredeclaredFiniteLookAdjustment()
    {
        var plan = Plan(128, 0.5);
        var report = await new EvolutionReplicateRunner<int>("fixed", plan, Ledger(), (_, _, _) => new(Value(0.5)))
            .RunAsync(Candidate, Context(), "precision");
        int expected = Enumerable.Range(2, 127).First(n => 2 * plan.Radius(n) <= 0.5);
        Assert.Equal(expected, report.Samples.Count);
        Assert.Equal(EvolutionReplicationStopReason.PrecisionReached, report.StopReason);
        Assert.True(report.UpperBound - report.LowerBound <= 0.5);
        Assert.True(plan.Radius(expected) > Plan(128).Radius(expected));
    }

    [Fact]
    public async Task SearchAndConfirmationNeverReuseSamplesOrCacheCredit()
    {
        var contexts = new List<EvolutionReplicateContext>();
        var ledger = Ledger();
        var runner = new EvolutionReplicateRunner<int>("fresh-v1", Plan(), ledger, (_, context, _) =>
        { contexts.Add(context); return new(Value(context.EvaluationContext.CreateRandom().NextDouble())); });
        var search = await runner.RunAsync(Candidate, Context(), "batch", EvolutionReplicationPurpose.Search);
        var confirmation = await runner.RunAsync(Candidate, Context(), "batch", EvolutionReplicationPurpose.Confirmation);
        Assert.Equal(8, contexts.Count); Assert.Equal(8, contexts.Select(c => c.SampleIdentity).Distinct().Count());
        Assert.All(contexts, c => { Assert.Equal(12, c.EvaluationContext.EvaluationId); Assert.Equal(42UL, c.EvaluationContext.RootSeed); });
        Assert.NotEqual(search.BatchIdentity, confirmation.BatchIdentity);
        Assert.NotEqual(search.MeanQuality, confirmation.MeanQuality);
        Assert.Equal(4, ledger.Snapshot().Receipts.Count(r => r.Stage == EvolutionResourceStage.Confirmation));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync(Candidate, Context(), "batch"));
        Assert.Equal(8, contexts.Count); Assert.Equal(8, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task ReplayAndAttemptNamespacesAreDeterministic()
    {
        EvolutionReplicateRunner<int> Runner() => new("fresh", Plan(), Ledger(), (_, context, _) =>
            new(Value(context.EvaluationContext.CreateRandom().NextDouble())));
        var first = await Runner().RunAsync(Candidate, Context(), "batch");
        var second = await Runner().RunAsync(Candidate, Context(), "batch");
        Assert.Equal(first.BatchIdentity, second.BatchIdentity);
        Assert.Equal(first.Samples.Select(s => s.Quality), second.Samples.Select(s => s.Quality));
        var retry = await Runner().RunAsync(Candidate, Context(2), "batch");
        Assert.NotEqual(first.BatchIdentity, retry.BatchIdentity);
    }

    [Fact]
    public async Task BudgetExhaustionRetainsWorkButDoesNotExposeASuccessfulPartialMean()
    {
        var ledger = Ledger(2);
        var report = await new EvolutionReplicateRunner<int>("fixed", Plan(), ledger, (_, _, _) => new(Value(1)))
            .RunAsync(Candidate, Context(), "batch");
        Assert.Equal(EvolutionReplicationStopReason.BudgetExhausted, report.StopReason);
        Assert.Equal(2, report.Samples.Count); Assert.Equal(2, report.ChargedCostUnits);
        Assert.False(report.IsComplete); Assert.Null(report.MeanQuality); Assert.Null(report.LowerBound);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public async Task OutOfSupportScoresFailClosedWithTheirReportedCost(double quality)
    {
        var report = await new EvolutionReplicateRunner<int>("bad", Plan(), Ledger(), (_, _, _) => new(Value(quality)))
            .RunAsync(Candidate, Context(), "batch");
        Assert.Equal(EvolutionReplicationStopReason.InvalidMeasurement, report.StopReason);
        Assert.Single(report.Samples); Assert.Equal(quality, report.Samples[0].Quality);
        Assert.Equal(1, report.ChargedCostUnits); Assert.Null(report.MeanQuality);
    }

    [Fact]
    public async Task FailedInfeasibleAndWrongDirectionResultsNeverEarnSuccess()
    {
        var invalid = new[]
        {
            new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, costUnits: 0.75),
            new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 0.5, constraintViolations: new[] { 1d }, costUnits: 0.75),
            new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, 0.5, EvolutionOptimizationDirection.Minimize, costUnits: 0.75)
        };
        foreach (var result in invalid)
        {
            var ledger = Ledger();
            var report = await new EvolutionReplicateRunner<int>("invalid", Plan(), ledger, (_, _, _) => new(result))
                .RunAsync(Candidate, Context(), "batch");
            Assert.False(report.IsComplete); Assert.Equal(0.75m, report.ChargedCostUnits);
            Assert.Equal(EvolutionResourceOutcome.Failed, ledger.Snapshot().Receipts.Single().Outcome);
        }
    }

    [Fact]
    public async Task ExceptionsAndNullResultsChargeUnknownMaximaWithoutLeakingDiagnostics()
    {
        foreach (bool throwException in new[] { true, false })
        {
            var ledger = Ledger();
            var report = await new EvolutionReplicateRunner<int>("bad", Plan(), ledger, (_, _, _) =>
                throwException ? throw new InvalidOperationException("secret") : new((EvolutionTaskResult)null!))
                .RunAsync(Candidate, Context(), "batch");
            Assert.Equal(EvolutionReplicationStopReason.UnknownCost, report.StopReason);
            Assert.Equal(1, report.ChargedCostUnits); Assert.Equal(1, ledger.Snapshot().Unknown);
            Assert.True(report.Samples.Single().UnknownCost); Assert.Null(report.MeanQuality);
        }
    }

    [Fact]
    public async Task AnActualOverrunIsChargedAndStopsFutureLedgerAdmission()
    {
        var ledger = Ledger();
        var report = await new EvolutionReplicateRunner<int>("overrun", Plan(), ledger, (_, _, _) => new(Value(0.5, 2)))
            .RunAsync(Candidate, Context(), "batch");
        Assert.Equal(EvolutionReplicationStopReason.MaximumCostExceeded, report.StopReason);
        Assert.Equal(2, report.ChargedCostUnits); Assert.True(ledger.Snapshot().MaximumViolated);
        Assert.False(report.IsComplete);
    }

    [Theory]
    [InlineData(1e25)]
    [InlineData(1e-50)]
    public async Task UnrepresentableCostsAreNotSilentlyClippedOrRoundedToZero(double cost)
    {
        var report = await new EvolutionReplicateRunner<int>("unrepresentable", Plan(), Ledger(), (_, _, _) => new(Value(0.5, cost)))
            .RunAsync(Candidate, Context(), "batch");
        Assert.Equal(EvolutionReplicationStopReason.UnknownCost, report.StopReason);
        Assert.Equal(cost, report.Samples.Single().ReportedCostUnits);
        Assert.True(report.Samples.Single().UnknownCost); Assert.Equal(1, report.ChargedCostUnits);
    }

    [Fact]
    public async Task PreCancellationDoesNotDispatchAndInFlightCancellationRetainsUnknownWork()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        int calls = 0;
        var runner = new EvolutionReplicateRunner<int>("canceled", Plan(), Ledger(), (_, _, _) =>
        { calls++; throw new OperationCanceledException(); });
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await runner.RunAsync(Candidate, Context(), "batch", cancellationToken: cancellation.Token));
        Assert.Equal(0, calls);
        var report = await runner.RunAsync(Candidate, Context(), "batch");
        Assert.Equal(1, calls); Assert.Equal(EvolutionReplicationStopReason.Canceled, report.StopReason);
        Assert.True(report.Samples.Single().UnknownCost);
    }

    [Fact]
    public async Task WideQualityRangesPreserveEndpointsWithoutVarianceOverflow()
    {
        var plan = new EvolutionReplicationPlan(2, 2, -1e16, 1, 1);
        var report = await new EvolutionReplicateRunner<int>("endpoint", plan, Ledger(), (_, _, _) => new(Value(1)))
            .RunAsync(Candidate, Context(), "batch");
        Assert.Equal(1, report.MeanQuality); Assert.Equal(1, report.UpperBound); Assert.Equal(0, report.StandardError);
    }

    [Fact]
    public async Task WideDeclaredBoundsDoNotEraseSmallMeasuredDifferences()
    {
        var plan = new EvolutionReplicationPlan(2, 2, -1e16, 1, 1);
        var report = await new EvolutionReplicateRunner<int>("wide", plan, Ledger(), (_, context, _) => new(Value(context.Index)))
            .RunAsync(Candidate, Context(), "batch");
        Assert.Equal(0.5, report.MeanQuality);
        Assert.Equal(0.5, report.StandardError!.Value, 12);
    }

    [Fact]
    public async Task TinyVarianceRetainsARepresentableStandardError()
    {
        var report = await new EvolutionReplicateRunner<int>("tiny", Plan(maximum: 2), Ledger(),
            (_, context, _) => new(Value(context.Index == 0 ? 0 : 1e-300))).RunAsync(Candidate, Context(), "tiny");
        Assert.Equal(5e-301, report.MeanQuality);
        Assert.Equal(5e-301, report.StandardError);
    }

    [Fact]
    public async Task ConfidenceBoundsDoNotCollapseAtTheFloatingPointResolutionLimit()
    {
        var plan = new EvolutionReplicationPlan(2, 64, 1e16, 1e16 + 2, 1, normalizedWidthTarget: 0.01);
        var report = await new EvolutionReplicateRunner<int>("adjacent", plan, Ledger(), (_, _, _) => new(Value(1e16)))
            .RunAsync(Candidate, Context(), "adjacent");
        Assert.True(report.UpperBound > report.LowerBound);
        Assert.Equal(64, report.Samples.Count);
    }

    [Fact]
    public async Task LaterFailureAndBetweenSampleCancellationRetainEarlierChargesWithoutPartialCredit()
    {
        var failed = await new EvolutionReplicateRunner<int>("later-failure", Plan(), Ledger(), (_, context, _) =>
            new(context.Index == 0 ? Value(1) : new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, costUnits: 0.5)))
            .RunAsync(Candidate, Context(), "failure");
        Assert.False(failed.IsComplete); Assert.Equal(2, failed.Samples.Count); Assert.Equal(1.5m, failed.ChargedCostUnits);
        using var cancellation = new CancellationTokenSource();
        var canceled = await new EvolutionReplicateRunner<int>("between", Plan(), Ledger(), (_, _, _) =>
        { cancellation.Cancel(); return new(Value(0.5)); }).RunAsync(Candidate, Context(), "between", cancellationToken: cancellation.Token);
        Assert.Equal(EvolutionReplicationStopReason.Canceled, canceled.StopReason);
        Assert.Single(canceled.Samples); Assert.False(canceled.Samples[0].UnknownCost); Assert.Equal(1, canceled.ChargedCostUnits);
    }

    [Fact]
    public async Task FatalProducerExceptionsPropagateButStillSettleUnknownReservations()
    {
        var ledger = Ledger();
        var runner = new EvolutionReplicateRunner<int>("fatal", Plan(), ledger, (_, _, _) => throw new OutOfMemoryException());
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await runner.RunAsync(Candidate, Context(), "fatal"));
        Assert.Equal(1, ledger.Snapshot().Unknown); Assert.Equal(1, ledger.Snapshot().Spent["cost_units"]);
    }

    [Fact]
    public async Task SupportClippingKeepsExtremeConfidenceBoundsFinite()
    {
        foreach (var bounds in new[] { (0d, double.MaxValue), (-1e16 - 2, -1e16), (0d, double.Epsilon) })
        {
            var plan = new EvolutionReplicationPlan(2, 2, bounds.Item1, bounds.Item2, 1);
            var report = await new EvolutionReplicateRunner<int>("extreme", plan, Ledger(), (_, _, _) => new(Value(bounds.Item2)))
                .RunAsync(Candidate, Context(), "extreme");
            Assert.True(report.LowerBound >= bounds.Item1); Assert.True(report.UpperBound <= bounds.Item2);
            Assert.True(report.UpperBound > report.LowerBound);
        }
    }

    [Fact]
    public void InvalidPlansAndLedgerContractsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(257));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionReplicationPlan(1, 2, 0, 1, 1));
        Assert.Throws<ArgumentException>(() => new EvolutionReplicationPlan(2, 4, 1, 1, 1));
        Assert.Throws<ArgumentException>(() => new EvolutionReplicationPlan(2, 4, -double.MaxValue, double.MaxValue, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionReplicationPlan(2, 4, 0, 1, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionReplicationPlan(2, 4, 0, 1, 1, confidence: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Plan(target: -1));
        Assert.Throws<ArgumentException>(() => new EvolutionReplicateRunner<int>("v1", Plan(),
            new EvolutionResourceLedger("wrong", EvolutionResources.Of("tokens", 10)), (_, _, _) => new(Value(0.5))));
        Assert.NotEqual(Plan().VersionHash, Plan(8).VersionHash);
        Assert.NotEqual(Plan().VersionHash, Plan(target: 0.5).VersionHash);
    }
}
