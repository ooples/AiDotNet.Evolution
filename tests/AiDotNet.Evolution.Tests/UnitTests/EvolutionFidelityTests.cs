using System.Text.Json;
using Xunit;

namespace AiDotNet.Evolution.Tests.UnitTests;

public sealed class EvolutionFidelityTests
{
    private static EvolutionFidelityPlan Plan(double exploration = 0.25, EvolutionOptimizationDirection direction = EvolutionOptimizationDirection.Maximize) =>
        new(new[] { new EvolutionFidelityLevel("low", 1, 1), new("medium", 3, 3), new("full", 9, 9) }, 0, 1,
            explorationFraction: exploration, direction: direction);
    private static EvolutionResourceLedger Ledger(decimal cap = 200) => new("fidelity-tests", EvolutionResources.Of("cost_units", cap));
    private static EvolutionCanonicalGenome<int>[] Candidates(int count = 8) => Enumerable.Range(0, count).Select(i => new EvolutionCanonicalGenome<int>(i, "candidate-" + i)).ToArray();

    private sealed class Evaluator
    {
        internal readonly List<(int Genome, EvolutionFidelityEvaluationContext Context)> Calls = new();
        internal string StateVersion = "state-v1";
        internal Func<int, EvolutionFidelityEvaluationContext, double>? Quality;
        internal Action<int, EvolutionFidelityEvaluationContext>? OnCall;
        internal bool Overrun;
        internal EvolutionOptimizationDirection Direction = EvolutionOptimizationDirection.Maximize;
        internal ValueTask<EvolutionFidelityEvaluationResult> Evaluate(int genome, EvolutionFidelityEvaluationContext context, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls.Add((genome, context)); OnCall?.Invoke(genome, context);
            if (context.Resume is { } resume)
            {
                byte[] payload = resume.CopyToken();
                Assert.Equal(genome, payload[0]); Assert.Equal(context.Replicate.Index, payload[1]);
                Assert.Equal(resume.SourceLevel.ResourceLevel, payload[2]);
                payload[0] = 255;
                Assert.Equal(genome, resume.CopyToken()[0]);
                Assert.Equal("candidate-" + genome, resume.GenomeId); Assert.Equal(context.Replicate.Index, resume.ReplicateIndex);
            }
            double cost = Overrun ? 2 : context.Level.ResourceLevel - (context.Resume?.SourceLevel.ResourceLevel ?? 0);
            var measurement = EvolutionTaskResult.Completed(Quality?.Invoke(genome, context) ?? genome / 10d,
                new Dictionary<string, double>(), direction: Direction, costUnits: cost);
            var bytes = new[] { (byte)genome, (byte)context.Replicate.Index, (byte)context.Level.ResourceLevel };
            var result = new EvolutionFidelityEvaluationResult(measurement, bytes, StateVersion);
            bytes[0] = 255; // The returned result must own its continuation token.
            return new(result);
        }
    }
    private static EvolutionFidelityScheduler<int> Scheduler(EvolutionResourceLedger ledger, Evaluator search, Evaluator? confirmation = null,
        EvolutionFidelityPlan? plan = null) => new(plan ?? Plan(), ledger, "search-v1", "confirm-v1", "state-v1", search.Evaluate, (confirmation ?? search).Evaluate);

    [Fact]
    public async Task Promotions_resume_matching_replicates_and_confirmation_always_restarts()
    {
        var evaluator = new Evaluator(); var ledger = Ledger();
        var report = await Scheduler(ledger, evaluator).RunAsync("run", Candidates(), 42);
        Assert.True(report.IsComplete); Assert.Equal(EvolutionFidelityStopReason.Completed, report.StopReason);
        Assert.Equal(16, report.Batches.Count); Assert.Equal(6, report.Promotions.Count); Assert.Equal(32, evaluator.Calls.Count);
        Assert.Equal(92, report.ChargedCostUnits); Assert.Equal(92, ledger.Snapshot().Spent["cost_units"]);
        Assert.Equal(12, evaluator.Calls.Count(call => call.Context.Resume is not null));
        var finalCalls = evaluator.Calls.Where(call => call.Context.Replicate.Purpose == EvolutionReplicationPurpose.Confirmation).ToArray();
        Assert.Equal(4, finalCalls.Length); Assert.All(finalCalls, call => { Assert.Null(call.Context.Resume); Assert.Equal(9, call.Context.Level.ResourceLevel); });
        Assert.Equal(32, evaluator.Calls.Select(call => call.Context.Replicate.SampleIdentity).Distinct().Count());
        Assert.Equal("candidate-7", report.BestConfirmed!.Candidate.Id);
        Assert.True(report.BestConfirmed.Measurements.Plan.Confidence > report.Plan.Confidence);
        Assert.All(report.Batches, batch => Assert.Equal(batch.Measurements.Samples.Count, batch.ResumedFromSampleIdentities.Count));
        Assert.All(ledger.Snapshot().Reserved.Values, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task Exploration_preserves_a_non_greedy_survivor_and_the_best_candidate()
    {
        var report = await Scheduler(Ledger(), new Evaluator()).RunAsync("run", Candidates(), 42);
        var first = report.Promotions.Where(p => p.FromLevel == "low").ToArray();
        Assert.Equal(4, first.Length); Assert.Contains(first, p => p.GenomeId == "candidate-7" && !p.IsExploration);
        Assert.Contains(first, p => p.Rank > 4 && p.IsExploration);
        var second = report.Promotions.Where(p => p.FromLevel == "medium").ToArray();
        Assert.Equal(2, second.Length); Assert.Contains(second, p => p.Rank > 2 && p.IsExploration);
        var greedy = await Scheduler(Ledger(), new Evaluator(), plan: Plan(0)).RunAsync("run", Candidates(), 42);
        Assert.DoesNotContain(greedy.Promotions, p => p.IsExploration);
        Assert.Equal(new[] { 1, 2, 3, 4 }, greedy.Promotions.Where(p => p.FromLevel == "low").Select(p => p.Rank));
    }

    [Fact]
    public async Task Exploration_can_preserve_late_improvers_that_greedy_halving_always_discards()
    {
        Evaluator Late() => new() { Quality = (genome, context) => context.Level.ResourceLevel == 9 && genome < 4 ? 0.99 : genome / 10d };
        var greedy = await Scheduler(Ledger(), Late(), plan: Plan(0)).RunAsync("late", Candidates(), 0);
        Assert.Equal(0.7, greedy.BestConfirmed!.Measurements.MeanQuality);
        bool recovered = false;
        for (ulong seed = 0; seed < 16; seed++)
        {
            var explored = await Scheduler(Ledger(), Late()).RunAsync("late", Candidates(), seed);
            Assert.Equal(greedy.ChargedCostUnits, explored.ChargedCostUnits);
            if (explored.BestConfirmed!.Measurements.MeanQuality == 0.99) { recovered = true; break; }
        }
        Assert.True(recovered); // Protection is possible and audited, not guaranteed for every candidate or seed.
    }

    [Fact]
    public async Task Unmatched_state_versions_restart_without_reusing_or_undercharging_old_work()
    {
        var evaluator = new Evaluator { StateVersion = "other-state-version" };
        var report = await Scheduler(Ledger(), evaluator).RunAsync("run", Candidates(), 42);
        Assert.True(report.IsComplete); Assert.Equal(112, report.ChargedCostUnits);
        Assert.All(evaluator.Calls, call => Assert.Null(call.Context.Resume));
        Assert.Equal(28, report.Batches.Sum(batch => batch.RejectedContinuationTokens));
        Assert.Equal(0, report.Batches.Sum(batch => batch.AcceptedContinuationTokens));
    }

    [Fact]
    public async Task Invalid_low_fidelity_scores_cannot_promote_and_costs_remain_visible()
    {
        var search = new Evaluator { Quality = (genome, _) => genome == 0 ? 2 : genome / 10d };
        var report = await Scheduler(Ledger(), search).RunAsync("run", Candidates(), 42);
        var failed = report.Batches.Single(batch => batch.Candidate.Id == "candidate-0");
        Assert.False(failed.Measurements.IsComplete); Assert.Single(failed.Measurements.Samples);
        Assert.Equal(1, failed.Measurements.ChargedCostUnits); Assert.Equal(0, failed.AcceptedContinuationTokens);
        Assert.DoesNotContain(report.Promotions, promotion => promotion.GenomeId == "candidate-0");
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task Full_search_scores_cannot_substitute_for_independent_confirmation()
    {
        var search = new Evaluator(); var confirmation = new Evaluator { Quality = (genome, _) => genome == 7 ? 0 : 1 };
        var report = await Scheduler(Ledger(), search, confirmation).RunAsync("run", Candidates(), 42);
        Assert.True(report.IsComplete); Assert.NotEqual("candidate-7", report.BestConfirmed!.Candidate.Id);
        Assert.Equal(4, confirmation.Calls.Count); Assert.All(confirmation.Calls, call => Assert.Null(call.Context.Resume));
        confirmation = new Evaluator { Quality = (_, _) => 2 };
        report = await Scheduler(Ledger(), new Evaluator(), confirmation).RunAsync("run", Candidates(), 42);
        Assert.Equal(EvolutionFidelityStopReason.NoConfirmedCandidate, report.StopReason); Assert.False(report.IsComplete); Assert.Null(report.BestConfirmed);
    }

    [Fact]
    public async Task Budget_exhaustion_cannot_return_a_low_fidelity_winner()
    {
        var ledger = Ledger(10); var evaluator = new Evaluator();
        var report = await Scheduler(ledger, evaluator).RunAsync("run", Candidates(), 42);
        Assert.Equal(EvolutionFidelityStopReason.BudgetExhausted, report.StopReason); Assert.Null(report.BestConfirmed);
        Assert.Equal(10, report.ChargedCostUnits); Assert.Equal(10, evaluator.Calls.Count);
        Assert.Equal(Candidates().Select(candidate => candidate.Id), report.InitialCandidateIds);
        Assert.Equal("search-v1", report.SearchEvaluatorVersionHash); Assert.Equal("confirm-v1", report.ConfirmationEvaluatorVersionHash);
        Assert.True(report.Batches.Count < report.InitialCandidateIds.Count);
        Assert.DoesNotContain(report.Batches, batch => batch.Purpose == EvolutionReplicationPurpose.Confirmation);
        Assert.Equal(0, ledger.Snapshot().Unknown);
    }

    [Fact]
    public async Task Known_overrun_and_unknown_failures_retain_their_resource_evidence()
    {
        var ledger = Ledger(); var report = await Scheduler(ledger, new Evaluator { Overrun = true }).RunAsync("over", Candidates(), 42);
        Assert.Equal(EvolutionFidelityStopReason.MaximumCostExceeded, report.StopReason); Assert.Equal(2, report.ChargedCostUnits);
        Assert.True(ledger.Snapshot().MaximumViolated); Assert.Null(report.BestConfirmed);
        ledger = Ledger();
        report = await Scheduler(ledger, new Evaluator { OnCall = (_, _) => throw new InvalidOperationException("backend unavailable") }).RunAsync("failed", Candidates(), 42);
        Assert.Equal(EvolutionFidelityStopReason.NoEligibleCandidates, report.StopReason); Assert.Equal(8, ledger.Snapshot().Unknown);
        Assert.Equal(8, report.ChargedCostUnits); Assert.Equal(8, report.Batches.Count); Assert.Null(report.BestConfirmed);
    }

    [Fact]
    public async Task Cancellation_and_fatal_errors_never_authorize_promotion()
    {
        using var cancellation = new CancellationTokenSource(); var ledger = Ledger();
        var evaluator = new Evaluator { OnCall = (genome, _) => { if (genome == 1) cancellation.Cancel(); } };
        var report = await Scheduler(ledger, evaluator).RunAsync("cancel", Candidates(), 42, cancellation.Token);
        Assert.Equal(EvolutionFidelityStopReason.Canceled, report.StopReason); Assert.Equal(3, report.ChargedCostUnits); Assert.Null(report.BestConfirmed);
        var unused = Ledger();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await Scheduler(unused, new Evaluator()).RunAsync("pre", Candidates(), 42, cancellation.Token));
        Assert.Equal(0, unused.Snapshot().Spent["cost_units"]);
        var fatal = Ledger();
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await Scheduler(fatal, new Evaluator { OnCall = (_, _) => throw new OutOfMemoryException() }).RunAsync("fatal", Candidates(), 42));
        Assert.Equal(1, fatal.Snapshot().Spent["cost_units"]); Assert.Equal(1, fatal.Snapshot().Unknown);
    }

    [Fact]
    public async Task Duplicate_requests_are_not_a_fidelity_cache_hit_and_replay_is_exact()
    {
        var ledger = Ledger(); var evaluator = new Evaluator(); var scheduler = Scheduler(ledger, evaluator);
        var first = await scheduler.RunAsync("run", Candidates(), 42);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await scheduler.RunAsync("run", Candidates(), 42));
        Assert.Equal(32, evaluator.Calls.Count);
        var second = await Scheduler(Ledger(), new Evaluator()).RunAsync("run", Candidates(), 42);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        var changed = await Scheduler(Ledger(), new Evaluator(), plan: Plan(0)).RunAsync("run", Candidates(), 42);
        Assert.NotEqual(first.RunIdentity, changed.RunIdentity);
    }

    [Fact]
    public async Task Shared_ledger_work_is_not_misreported_as_this_brackets_measurement_cost()
    {
        var ledger = Ledger();
        using (var reservation = ledger.TryReserve("other-work", EvolutionResourceStage.Setup, EvolutionResources.Of("cost_units", 10), EvolutionResources.Of("cost_units", 10)))
            reservation!.Complete(EvolutionResources.Of("cost_units", 10));
        var report = await Scheduler(ledger, new Evaluator()).RunAsync("run", Candidates(), 42);
        Assert.Equal(92, report.ChargedCostUnits); Assert.Equal(102, report.Resources.Spent["cost_units"]);
    }

    [Fact]
    public async Task Minimization_and_a_small_population_obey_the_same_full_confirmation_contract()
    {
        var evaluator = new Evaluator { Direction = EvolutionOptimizationDirection.Minimize };
        var report = await Scheduler(Ledger(), evaluator, plan: Plan(direction: EvolutionOptimizationDirection.Minimize)).RunAsync("min", Candidates(2), 42);
        Assert.True(report.IsComplete); Assert.Equal("candidate-0", report.BestConfirmed!.Candidate.Id);
        Assert.DoesNotContain(report.Promotions, promotion => promotion.IsExploration);
        Assert.Equal(8, report.Batches.Count);
    }

    [Fact]
    public async Task Invalid_plans_candidates_and_tokens_fail_before_dispatch()
    {
        Assert.Throws<ArgumentException>(() => new EvolutionFidelityPlan(new[] { new EvolutionFidelityLevel("same", 1, 1), new("same", 2, 2) }, 0, 1));
        Assert.Throws<ArgumentException>(() => new EvolutionFidelityPlan(new[] { new EvolutionFidelityLevel("a", 2, 1), new("b", 1, 2) }, 0, 1));
        Assert.Throws<ArgumentException>(() => new EvolutionFidelityPlan(Enumerable.Range(1, 9).Select(i => new EvolutionFidelityLevel("level" + i, i, i)), 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionFidelityPlan(Plan().Levels, 0, 1, replicates: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionFidelityPlan(Plan().Levels, 0, 1, explorationFraction: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionFidelityLevel("bad", 0, 1));
        var measured = EvolutionTaskResult.Completed(0, new Dictionary<string, double>());
        Assert.Throws<ArgumentException>(() => new EvolutionFidelityEvaluationResult(measured, new byte[4097], "state"));
        Assert.Throws<ArgumentException>(() => new EvolutionFidelityEvaluationResult(measured, null, "state"));
        var result = new EvolutionFidelityEvaluationResult(measured); Assert.Null(result.CopyContinuationToken());
        var ledger = Ledger(); var scheduler = Scheduler(ledger, new Evaluator());
        await Assert.ThrowsAsync<ArgumentException>(async () => await scheduler.RunAsync("duplicate", new[] { Candidates()[0], Candidates()[0] }, 42));
        await Assert.ThrowsAsync<ArgumentException>(async () => await scheduler.RunAsync("empty", Array.Empty<EvolutionCanonicalGenome<int>>(), 42));
        Assert.Equal(0, ledger.Snapshot().Spent["cost_units"]);
    }
}
