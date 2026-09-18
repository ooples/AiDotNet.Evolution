using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionNoisePolicyTests
{
    private static EvolutionResourceLedger Ledger(decimal cap = 10000) => new("noise-policy", EvolutionResources.Of("cost_units", cap));
    private static EvolutionCanonicalGenome<int> Candidate(int value) => new(value, "candidate-" + value);
    private static EvolutionEvaluationContext Context => new(10, 42, 17, 1);
    private static EvolutionTaskResult Value(double value, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize) =>
        EvolutionTaskResult.Completed(value, new Dictionary<string, double>(), direction, costUnits: 1);
    private static EvolutionIncumbentChallenge<int> Challenge(EvolutionResourceLedger ledger,
        Func<int, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> search,
        Func<int, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>> confirm,
        EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize, int slots = 2) =>
        new("search-v1", "confirm-v1", ledger, 4, 64, slots, 0, 1, .1, 1, search, confirm, direction: direction);

    [Theory]
    [InlineData(EvolutionOptimizationDirection.Maximize)]
    [InlineData(EvolutionOptimizationDirection.Minimize)]
    public async Task ChallengeUsesFourFreshBatchesAndConservativeConfirmation(EvolutionOptimizationDirection direction)
    {
        var ledger = Ledger();
        var identities = new HashSet<string>();
        ValueTask<EvolutionTaskResult> Measure(int genome, EvolutionReplicateContext context, CancellationToken _)
        {
            Assert.True(identities.Add(context.SampleIdentity));
            return new(Value(direction == EvolutionOptimizationDirection.Maximize ? genome : 1 - genome, direction));
        }
        var result = await Challenge(ledger, Measure, Measure, direction).RunAsync(0, Candidate(1), Candidate(0), Context);
        Assert.True(result.IsConfirmed);
        Assert.Equal("candidate-0", result.IncumbentId);
        Assert.Equal("candidate-1", result.CandidateId);
        Assert.True(result.LowerImprovementBound > .1);
        Assert.Equal(136, identities.Count);
        Assert.Equal(136, result.ChargedCostUnits);
        Assert.Equal(136, ledger.Snapshot().Spent["cost_units"]);
        Assert.All(result.CandidateConfirmation!.Samples, sample => Assert.Equal(EvolutionReplicationPurpose.Confirmation, sample.Context.Purpose));
        Assert.Equal(128, ledger.Snapshot().Stages.Single(stage => stage.Stage == EvolutionResourceStage.Confirmation).Spent["cost_units"]);
    }

    [Fact]
    public async Task LuckySearchCannotOverrideIndependentConfirmation()
    {
        var result = await Challenge(Ledger(), (g, _, _) => new(Value(g)), (g, _, _) => new(Value(1 - g)))
            .RunAsync(0, Candidate(1), Candidate(0), Context);
        Assert.False(result.IsConfirmed);
        Assert.Equal("not-confirmed", result.Outcome);
        Assert.True(result.LowerImprovementBound < 0);
    }

    [Fact]
    public async Task NonpromisingChallengeDoesNotSpendConfirmationBudget()
    {
        int confirmationCalls = 0;
        var result = await Challenge(Ledger(), (_, _, _) => new(Value(.5)), (_, _, _) => { confirmationCalls++; return new(Value(1)); })
            .RunAsync(0, Candidate(1), Candidate(0), Context);
        Assert.Equal("not-promising", result.Outcome);
        Assert.Equal(0, confirmationCalls);
        Assert.Equal(8, result.ChargedCostUnits);
        Assert.Null(result.CandidateConfirmation);
    }

    [Fact]
    public async Task SlotTombstoneSurvivesLedgerRestoreAndChangedCandidateCannotReuseIt()
    {
        var ledger = Ledger();
        ValueTask<EvolutionTaskResult> Measure(int g, EvolutionReplicateContext _, CancellationToken __) => new(Value(g));
        await Challenge(ledger, Measure, Measure).RunAsync(0, Candidate(1), Candidate(0), Context);
        var restored = Ledger(); restored.RestoreState(ledger.CaptureState());
        string before = restored.CaptureState();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Challenge(restored, Measure, Measure)
            .RunAsync(0, Candidate(2), Candidate(0), Context).AsTask());
        Assert.Equal(before, restored.CaptureState());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Challenge(restored, Measure, Measure)
            .RunAsync(2, Candidate(1), Candidate(0), Context).AsTask());
    }

    [Fact]
    public async Task IncompleteConfirmationCannotReplaceIncumbent()
    {
        var ledger = Ledger(10);
        var result = await Challenge(ledger, (g, _, _) => new(Value(g)), (g, _, _) => new(Value(g)))
            .RunAsync(0, Candidate(1), Candidate(0), Context);
        Assert.False(result.IsConfirmed);
        Assert.Equal("candidate-confirmation-incomplete", result.Outcome);
        Assert.Equal(10, result.ChargedCostUnits);
        Assert.Equal(2, result.CandidateConfirmation!.Samples.Count);
        Assert.Null(result.LowerImprovementBound);
    }

    [Fact]
    public async Task CancellationAtBatchBoundaryRetainsAlreadyMeasuredEvidence()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var result = await Challenge(Ledger(), (g, _, _) =>
        {
            if (++calls == 4) cancellation.Cancel();
            return new(Value(g));
        }, (g, _, _) => new(Value(g))).RunAsync(0, Candidate(1), Candidate(0), Context, cancellation.Token);
        Assert.Equal("canceled", result.Outcome);
        Assert.Equal(4, result.CandidateSearch.Samples.Count);
        Assert.Equal(4, result.ChargedCostUnits);
        Assert.Null(result.IncumbentSearch);
    }

    [Fact]
    public async Task FailedSearchHasUnknownChargeAndNoConfirmation()
    {
        var ledger = Ledger();
        var result = await Challenge(ledger, (_, _, _) => throw new InvalidOperationException("no receipt"),
            (_, _, _) => throw new InvalidOperationException("must not execute"))
            .RunAsync(0, Candidate(1), Candidate(0), Context);
        Assert.False(result.IsConfirmed);
        Assert.Equal(1, result.ChargedCostUnits);
        Assert.Equal(1, ledger.Snapshot().Unknown);
    }

    [Theory]
    [InlineData(0, .1)]
    [InlineData(1025, .1)]
    [InlineData(2, -1)]
    [InlineData(2, 1)]
    public void InvalidChallengePolicyCannotDispatch(int slots, double gain)
    {
        Assert.ThrowsAny<ArgumentException>(() => new EvolutionIncumbentChallenge<int>("s", "c", Ledger(), 4, 64,
            slots, 0, 1, gain, 1, (_, _, _) => new(Value(0)), (_, _, _) => new(Value(0))));
    }

    private static EvolutionRejectionAudit<int> Audit(EvolutionResourceLedger ledger, int count = 4,
        Func<int, EvolutionReplicateContext, CancellationToken, ValueTask<EvolutionTaskResult>>? callback = null) =>
        new("full-v1", ledger, count, 64, 0, 1, .5, 1, callback ?? ((g, _, _) => new(Value(g % 2))));

    [Fact]
    public async Task FullCensusIdentifiesUsefulRejectsWithoutSamplingRadius()
    {
        var ledger = Ledger();
        var result = await Audit(ledger).RunAsync("audit", Enumerable.Range(0, 4).Select(Candidate).ToArray(), 37);
        Assert.Equal(2, result.Entries.Count(row => row.DefinitelyUseful));
        Assert.Equal(.5, result.FalseRejectionRateLower);
        Assert.Equal(.5, result.FalseRejectionRateUpper);
        Assert.Equal(256, result.ChargedCostUnits);
        Assert.Equal(256, ledger.Snapshot().Stages.Single(stage => stage.Stage == EvolutionResourceStage.Confirmation).Spent["cost_units"]);
        Assert.All(result.Entries, row => Assert.False(row.Unresolved));
    }

    [Fact]
    public async Task PreselectionIsInvariantToInputOrderAndHasNoReplacement()
    {
        var population = Enumerable.Range(0, 12).Select(Candidate).ToArray();
        var first = await Audit(Ledger()).RunAsync("audit", population, 37);
        var second = await Audit(Ledger()).RunAsync("audit", Enumerable.Reverse(population).ToArray(), 37);
        Assert.Equal(first.PopulationHash, second.PopulationHash);
        Assert.Equal(first.Entries.Select(row => row.CandidateId), second.Entries.Select(row => row.CandidateId));
        Assert.Equal(4, first.Entries.Select(row => row.CandidateId).Distinct().Count());
        Assert.True(first.FalseRejectionRateUpper > first.FalseRejectionRateLower);
    }

    [Fact]
    public async Task UnresolvedBudgetShortAuditsCannotBeCountedAsHarmlessRejects()
    {
        var ledger = Ledger(5);
        var result = await Audit(ledger).RunAsync("audit", Enumerable.Range(0, 4).Select(Candidate).ToArray(), 37);
        Assert.Equal(4, result.Entries.Count);
        Assert.All(result.Entries, row => Assert.True(row.Unresolved));
        Assert.Equal(0, result.FalseRejectionRateLower);
        Assert.Equal(1, result.FalseRejectionRateUpper);
        Assert.Equal(5, result.ChargedCostUnits);
    }

    [Fact]
    public async Task RepeatingAuditWithAnotherSeedIsRefusedBeforeMoreMeasurements()
    {
        var ledger = Ledger(); var audit = Audit(ledger);
        var population = Enumerable.Range(0, 4).Select(Candidate).ToArray();
        await audit.RunAsync("audit", population, 37);
        string before = ledger.CaptureState();
        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.RunAsync("audit", population, 999).AsTask());
        Assert.Equal(before, ledger.CaptureState());
    }

    [Fact]
    public async Task CanceledAuditRetainsUndispatchedSelectedCandidates()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await Audit(Ledger(), callback: (g, _, _) => { cancellation.Cancel(); return new(Value(g % 2)); })
            .RunAsync("audit", Enumerable.Range(0, 4).Select(Candidate).ToArray(), 37, cancellation.Token);
        Assert.Equal(4, result.Entries.Count);
        Assert.Equal(3, result.Entries.Count(row => row.FullEvaluation is null));
        Assert.All(result.Entries, row => Assert.True(row.Unresolved));
        Assert.Equal(1, result.ChargedCostUnits);
    }

    [Fact]
    public async Task DuplicateRejectedIdentitiesAreRejectedBeforeAnyAccounting()
    {
        var ledger = Ledger();
        await Assert.ThrowsAsync<ArgumentException>(() => Audit(ledger).RunAsync("audit", new[] { Candidate(1), Candidate(1) }, 37).AsTask());
        Assert.Equal(0, ledger.Snapshot().Admitted);
    }

    [Fact]
    public async Task WarmupsAreChargedButNotPooledIntoTimingFitness()
    {
        var timing = new EvolutionTimingProtocol(3, 1000);
        int calls = 0;
        var result = await timing.MeasureAsync(_ => { calls++; return default; });
        Assert.Equal(EvolutionEvaluationStatus.Completed, result.Status);
        Assert.Equal(EvolutionOptimizationDirection.Minimize, result.Direction);
        Assert.Equal(4, calls); Assert.Equal(4, result.CostUnits);
        Assert.Equal(3, result.Metrics["warmup_calls"]);
        Assert.Equal(result.Metrics["measurement_elapsed_ms"], result.Quality);
        Assert.Equal(4, timing.MaximumCostPerSample);
    }

    [Fact]
    public async Task FreshTimingReplicationChargesEveryWarmupAndMeasuredInvocation()
    {
        var timing = new EvolutionTimingProtocol(2, 1000); var ledger = Ledger(); int calls = 0;
        var runner = new EvolutionReplicateRunner<int>(timing.VersionHash,
            new EvolutionReplicationPlan(3, 3, 0, 1000, 3, direction: EvolutionOptimizationDirection.Minimize), ledger,
            (_, _, token) => timing.MeasureAsync(_ => { calls++; return default; }, token));
        var report = await runner.RunAsync(Candidate(1), Context, "timing");
        Assert.True(report.IsComplete); Assert.Equal(9, calls);
        Assert.Equal(9, report.ChargedCostUnits);
        Assert.Equal(3, report.Samples.Count); // warmup calls are not independent fitness samples
    }

    [Fact]
    public async Task WarmupFailureCannotProduceFastSuccessfulFitness()
    {
        var result = await new EvolutionTimingProtocol(2, 1000).MeasureAsync(_ => throw new InvalidOperationException("wrong output"));
        Assert.Equal(EvolutionEvaluationStatus.Failed, result.Status);
        Assert.Null(result.Quality); Assert.Equal(1, result.CostUnits);
    }

    [Fact]
    public async Task CooperativeDeadlineInvalidatesTimingWithoutInventingSuccess()
    {
        var result = await new EvolutionTimingProtocol(0, 1).MeasureAsync(async token => await Task.Delay(100, token));
        Assert.Equal(EvolutionEvaluationStatus.TimedOut, result.Status);
        Assert.Null(result.Quality); Assert.Equal(1, result.CostUnits);
    }

    [Theory]
    [InlineData(-1, 1000)]
    [InlineData(65, 1000)]
    [InlineData(0, 0)]
    [InlineData(0, 60001)]
    public void InvalidTimingProtocolIsRejected(int warmups, double maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionTimingProtocol(warmups, maximum));
    }
}
