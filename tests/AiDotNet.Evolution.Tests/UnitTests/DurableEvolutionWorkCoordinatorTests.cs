using AiDotNet.Evolution;
using Xunit;

namespace AiDotNet.Evolution.Tests;

public sealed class DurableEvolutionWorkCoordinatorTests
{
    [Fact]
    public void ImmutableOptionsProfilesAndPayloadBoundsRejectInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumWorkItems: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumWorkItems: 10001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumDeliveriesPerWork: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumDeliveriesPerWork: 17));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumStateBytes: 4095));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumPayloadBytes: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(maximumWorkers: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(leaseDuration: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkCoordinatorOptions(leaseDuration: TimeSpan.FromDays(2)));
        Assert.Throws<ArgumentException>(() => new EvolutionWorkerProfile(" ", "compat"));
        Assert.Throws<ArgumentException>(() => new EvolutionWorkerProfile("worker", " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EvolutionWorkerProfile("worker", "compat", maximumConcurrentWork: 0));
        Assert.Throws<ArgumentException>(() => new EvolutionWorkRequirements(new[] { "gpu", "gpu" }));
        Assert.Throws<ArgumentException>(() => new EvolutionWorkRequirements(new[] { new string('x', 65) }));
        Assert.Throws<ArgumentException>(() => new EvolutionWorkRequirements(Enumerable.Range(0, 33).Select(i => "tag-" + i)));
        string[] tags = { "gpu" }; var requirements = new EvolutionWorkRequirements(tags); tags[0] = "cpu";
        Assert.Equal("gpu", Assert.Single(requirements.Tags));
        using var fixture = new Fixture(); using var coordinator = fixture.Open();
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Enqueue(-1, 1, "id", "7", new(), Cost(1), Cost(5)));
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Enqueue(1, 0, "id", "7", new(), Cost(1), Cost(5)));
        Assert.Throws<ArgumentException>(() => coordinator.Enqueue(1, 1, " ", "7", new(), Cost(1), Cost(5)));
        Assert.Throws<ArgumentException>(() => coordinator.Enqueue(1, 1, "id", new string('é', 2049), new(), Cost(1), Cost(5)));
        Assert.Throws<ArgumentException>(() => coordinator.Enqueue(1, 1, "id", "7", new(), Cost(6), Cost(5)));
        Assert.Throws<ArgumentException>(() => coordinator.Enqueue(1, 1, "id", "7", new(), Cost(1), EvolutionResources.Of("undeclared", 5)));
    }

    [Fact]
    public void RetryFencesResultsWithoutLosingOrDoubleChargingOriginalPhysicalWork()
    {
        using var fixture = new Fixture();
        using var coordinator = fixture.Open(10);
        fixture.Enqueue(coordinator);
        EvolutionWorkLease first = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("worker-a")));
        fixture.Advance(11);
        Assert.Equal(EvolutionWorkHeartbeat.Expired, coordinator.Heartbeat(first.Identity, first.WorkerId));
        EvolutionWorkLease retry = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("worker-b")));
        Assert.Equal(first.Identity.EvaluationId, retry.Identity.EvaluationId);
        Assert.Equal(first.Identity.Attempt, retry.Identity.Attempt);
        Assert.NotEqual(first.Identity.LeaseId, retry.Identity.LeaseId); Assert.Equal(2, retry.DeliveryNumber);
        Assert.Equal(10, coordinator.Resources.Reserved["cost_units"]);
        Assert.Equal(EvolutionWorkCommitDisposition.Stale, coordinator.Commit(first.Identity, first.WorkerId, "old", "receipt-a", Cost(3)));
        Assert.Null(coordinator.GetResult(1, 1)); Assert.False(coordinator.GetDeliveryResult(first.Identity, first.WorkerId)!.Accepted);
        Assert.Equal(3, coordinator.Resources.Spent["cost_units"]); Assert.Equal(5, coordinator.Resources.Reserved["cost_units"]);
        Assert.Equal(EvolutionWorkCommitDisposition.DuplicateStale, coordinator.Commit(first.Identity, first.WorkerId, "old", "receipt-a", Cost(3)));
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(retry.Identity, retry.WorkerId, "new", "receipt-b", Cost(4)));
        Assert.Equal(EvolutionWorkCommitDisposition.Duplicate, coordinator.Commit(retry.Identity, retry.WorkerId, "new", "receipt-b", Cost(4)));
        Assert.Equal("new", coordinator.GetResult(1, 1)!.Payload);
        Assert.Equal(7, coordinator.Resources.Spent["cost_units"]); Assert.Equal(0, coordinator.Resources.Reserved["cost_units"]);
        Assert.Equal(2, coordinator.Resources.Settled);
        Assert.Throws<InvalidOperationException>(() => coordinator.Commit(first.Identity, first.WorkerId, "old", "receipt-a", Cost(4)));
    }

    [Fact]
    public void RestartRecoversPendingLeasesReservationsAndResultsWithoutRedispatch()
    {
        using var fixture = new Fixture();
        EvolutionWorkLease first;
        using (var coordinator = fixture.Open())
        {
            Assert.False(coordinator.WasRecovered);
            fixture.Enqueue(coordinator); fixture.Enqueue(coordinator, 2);
            first = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(fixture.Worker("worker-a")));
        }
        using (var recovered = fixture.Open())
        {
            Assert.True(recovered.WasRecovered); Assert.False(recovered.SupportsExactSearchContinuation);
            Assert.Contains("fork", recovered.SearchContinuationGuarantee);
            EvolutionWorkLease reconnect = Assert.Single(recovered.GetUnsettledDeliveries(first.WorkerId));
            Assert.Equal(first.Identity.LeaseId, reconnect.Identity.LeaseId);
            Assert.Equal(first.Payload, reconnect.Payload); Assert.Equal(5, recovered.Resources.Reserved["cost_units"]);
            Assert.Null(recovered.Claim(fixture.Worker(first.WorkerId))); // same worker still occupied
            var second = Assert.IsType<EvolutionWorkLease>(recovered.Claim(fixture.Worker("worker-b")));
            Assert.Equal(2, second.Identity.EvaluationId);
            Assert.Equal(EvolutionWorkCommitDisposition.Accepted, recovered.Commit(first.Identity, first.WorkerId, "1", "worker receipt", Cost(2)));
            Assert.Equal(EvolutionWorkCommitDisposition.Accepted, recovered.Commit(second.Identity, second.WorkerId, "2", "worker receipt", Cost(3)));
        }
        using var final = fixture.Open();
        Assert.Equal("1", final.GetResult(1, 1)!.Payload); Assert.Equal("2", final.GetResult(2, 1)!.Payload);
        Assert.Equal(5, final.Resources.Spent["cost_units"]); Assert.Equal(0, final.Resources.Reserved["cost_units"]);
        Assert.Equal(EvolutionWorkCommitDisposition.Duplicate, final.Commit(first.Identity, first.WorkerId, "1", "worker receipt", Cost(2)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaimCrashBoundaryCannotForgetPublishedReservation(bool afterPublish)
    {
        using var fixture = new Fixture();
        using (var coordinator = fixture.Open())
        {
            fixture.Enqueue(coordinator);
            coordinator.Publishing = published => { if (published == afterPublish) throw new IOException("crash at claim publication"); };
            Assert.Throws<IOException>(() => coordinator.Claim(fixture.Worker("a")));
            Assert.Throws<InvalidOperationException>(() => coordinator.Resources);
        }
        using var recovered = fixture.Open();
        if (afterPublish)
        {
            Assert.Single(recovered.GetUnsettledDeliveries("a")); Assert.Equal(5, recovered.Resources.Reserved["cost_units"]);
            Assert.Null(recovered.Claim(fixture.Worker("b")));
        }
        else
        {
            Assert.Empty(recovered.GetUnsettledDeliveries("a")); Assert.Equal(0, recovered.Resources.Reserved["cost_units"]);
            Assert.NotNull(recovered.Claim(fixture.Worker("b")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResultCrashBoundaryReconcilesReceiptExactlyOnce(bool afterPublish)
    {
        using var fixture = new Fixture();
        EvolutionWorkLease lease;
        using (var coordinator = fixture.Open())
        {
            fixture.Enqueue(coordinator); lease = coordinator.Claim(fixture.Worker("a"))!;
            coordinator.Publishing = published => { if (published == afterPublish) throw new IOException("crash at result publication"); };
            Assert.Throws<IOException>(() => coordinator.Commit(lease.Identity, "a", "result", "provenance", Cost(2)));
        }
        using var recovered = fixture.Open();
        Assert.Equal(afterPublish ? 2 : 0, recovered.Resources.Spent["cost_units"]);
        Assert.Equal(afterPublish ? EvolutionWorkCommitDisposition.Duplicate : EvolutionWorkCommitDisposition.Accepted,
            recovered.Commit(lease.Identity, "a", "result", "provenance", Cost(2)));
        Assert.Equal(2, recovered.Resources.Spent["cost_units"]); Assert.Equal(1, recovered.Resources.Settled);
    }

    [Fact]
    public async Task ConcurrentIdenticalResultRetriesCommitOnce()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open(); fixture.Enqueue(coordinator);
        var lease = coordinator.Claim(fixture.Worker("a"))!;
        EvolutionWorkCommitDisposition[] outcomes = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            coordinator.Commit(lease.Identity, "a", "result", "receipt", Cost(2)))));
        Assert.Equal(1, outcomes.Count(o => o == EvolutionWorkCommitDisposition.Accepted));
        Assert.Equal(15, outcomes.Count(o => o == EvolutionWorkCommitDisposition.Duplicate));
        Assert.Equal(2, coordinator.Resources.Spent["cost_units"]);
        Assert.Throws<InvalidOperationException>(() => coordinator.Commit(lease.Identity, "a", "other", "receipt", Cost(2)));
        Assert.Throws<InvalidOperationException>(() => coordinator.Commit(lease.Identity, "a", "result", "other", Cost(2)));
    }

    [Fact]
    public void HeartbeatsRenewOnlyLiveLeasesAndCancellationRetainsCostLiability()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open(); fixture.Enqueue(coordinator);
        var lease = coordinator.Claim(fixture.Worker("a"))!;
        fixture.Advance(9); Assert.Equal(EvolutionWorkHeartbeat.Renewed, coordinator.Heartbeat(lease.Identity, "a"));
        Assert.Equal(fixture.Now.AddSeconds(10), Assert.Single(coordinator.GetUnsettledDeliveries("a")).ExpiresAt);
        fixture.Advance(2); Assert.Null(coordinator.Claim(fixture.Worker("b"))); // original deadline passed; renewal still live
        Assert.True(coordinator.Cancel(1, 1)); Assert.False(coordinator.Cancel(1, 1));
        Assert.Equal(EvolutionWorkHeartbeat.Canceled, coordinator.Heartbeat(lease.Identity, "a"));
        Assert.Equal(5, coordinator.Resources.Reserved["cost_units"]);
        Assert.Equal(EvolutionWorkCommitDisposition.Stale, coordinator.Commit(lease.Identity, "a", "stopped", "receipt", Cost(2), EvolutionResourceOutcome.Canceled));
        Assert.Null(coordinator.GetResult(1, 1)); Assert.Equal(2, coordinator.Resources.Spent["cost_units"]);
        Assert.Equal(EvolutionWorkHeartbeat.Completed, coordinator.Heartbeat(lease.Identity, "a"));
        fixture.Enqueue(coordinator, 2); Assert.True(coordinator.Cancel(2, 1));
        Assert.Null(coordinator.Claim(fixture.Worker("b"))); Assert.False(coordinator.Cancel(999, 1));
    }

    [Fact]
    public void HardwareCapacityAndCompatibilityApplyAcrossUnsettledPhysicalDeliveries()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open();
        var requirements = new EvolutionWorkRequirements(new[] { "gpu", "cuda" }, EvolutionResources.Of("gpu_slots", 2));
        for (int id = 1; id <= 3; id++) fixture.Enqueue(coordinator, id, requirements);
        var wrong = new EvolutionWorkerProfile("wrong", "other", new[] { "gpu", "cuda" }, EvolutionResources.Of("gpu_slots", 10), 3);
        Assert.Null(coordinator.Claim(wrong));
        Assert.Null(coordinator.Claim(new EvolutionWorkerProfile("cpu", fixture.Compatibility, new[] { "cpu" }, EvolutionResources.Of("gpu_slots", 10), 3)));
        var worker = new EvolutionWorkerProfile("a", fixture.Compatibility, new[] { "gpu", "cuda" }, EvolutionResources.Of("gpu_slots", 3), 3);
        var first = Assert.IsType<EvolutionWorkLease>(coordinator.Claim(worker));
        Assert.Null(coordinator.Claim(worker)); // three concurrency slots do not make four GPU slots fit in three
        Assert.Throws<InvalidOperationException>(() => coordinator.Claim(new EvolutionWorkerProfile("a", fixture.Compatibility,
            new[] { "gpu", "cuda" }, EvolutionResources.Of("gpu_slots", 4), 3)));
        fixture.Advance(11);
        Assert.Null(coordinator.Claim(worker)); // unknown expired work still occupies this incarnation
        Assert.Equal(EvolutionWorkCommitDisposition.Stale, coordinator.Commit(first.Identity, "a", "late", "receipt", Cost(2)));
        Assert.NotNull(coordinator.Claim(worker));
    }

    [Fact]
    public void LostWorkersCannotMakeRetryBudgetFreeAndDeliveryLimitIsBounded()
    {
        using var fixture = new Fixture();
        using (var coordinator = fixture.Open(5))
        {
            fixture.Enqueue(coordinator); Assert.NotNull(coordinator.Claim(fixture.Worker("a")));
            fixture.Advance(11); Assert.Null(coordinator.Claim(fixture.Worker("b")));
            Assert.Equal(5, coordinator.Resources.Reserved["cost_units"]); Assert.Equal(0, coordinator.Resources.Spent["cost_units"]);
        }
        using var recovered = fixture.Open(5);
        Assert.Null(recovered.Claim(fixture.Worker("b"))); Assert.Equal(1, recovered.Resources.Admitted);
    }

    [Fact]
    public void MaximumViolationRetainsEvidenceAndStopsAdmission()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open(5); fixture.Enqueue(coordinator);
        var lease = coordinator.Claim(fixture.Worker("a"))!;
        Assert.Equal(EvolutionWorkCommitDisposition.BudgetViolation, coordinator.Commit(lease.Identity, "a", "invalid bill", "overrun", Cost(6)));
        Assert.True(coordinator.Resources.MaximumViolated); Assert.Equal(6, coordinator.Resources.Spent["cost_units"]);
        Assert.False(coordinator.GetDeliveryResult(lease.Identity, "a")!.Accepted); Assert.Null(coordinator.GetResult(1, 1));
        fixture.Enqueue(coordinator, 2); Assert.Null(coordinator.Claim(fixture.Worker("b")));
    }

    [Fact]
    public void WrongIdentityAndUnknownWorkersCannotCommitOrRenew()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open(); fixture.Enqueue(coordinator);
        var lease = coordinator.Claim(fixture.Worker("a"))!;
        var wrong = new[]
        {
            new EvolutionWorkIdentity("other-run", 1, 1, lease.Identity.LeaseId),
            new EvolutionWorkIdentity(fixture.RunId, 2, 1, lease.Identity.LeaseId),
            new EvolutionWorkIdentity(fixture.RunId, 1, 2, lease.Identity.LeaseId),
            new EvolutionWorkIdentity(fixture.RunId, 1, 1, Guid.NewGuid().ToString("N")),
        };
        foreach (var identity in wrong)
        {
            Assert.Equal(EvolutionWorkCommitDisposition.UnknownLease, coordinator.Commit(identity, "a", "x", "receipt", Cost(2)));
            Assert.Equal(EvolutionWorkHeartbeat.UnknownLease, coordinator.Heartbeat(identity, "a"));
            Assert.Null(coordinator.GetDeliveryResult(identity, "a"));
        }
        Assert.Equal(EvolutionWorkCommitDisposition.UnknownLease, coordinator.Commit(lease.Identity, "other-worker", "x", "receipt", Cost(2)));
        Assert.Equal(5, coordinator.Resources.Reserved["cost_units"]); Assert.Equal(0, coordinator.Resources.Settled);
    }

    [Fact]
    public void DuplicateEnqueueIsIdempotentButCannotRelabelAStoredGenome()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open();
        Assert.True(fixture.Enqueue(coordinator)); Assert.False(fixture.Enqueue(coordinator));
        Assert.Throws<InvalidOperationException>(() => coordinator.Enqueue(1, 1, "other", "same display", new(), Cost(1), Cost(5)));
        Assert.Throws<InvalidOperationException>(() => coordinator.Enqueue(1, 1, "genome-1", "other payload", new(), Cost(1), Cost(5)));
        Assert.True(fixture.Enqueue(coordinator, 2));
        var a = coordinator.Claim(fixture.Worker("a"))!; var b = coordinator.Claim(fixture.Worker("b"))!;
        Assert.NotEqual(a.CanonicalGenomeId, b.CanonicalGenomeId); Assert.NotEqual(a.Identity.LeaseId, b.Identity.LeaseId);
    }

    [Fact]
    public void RecoveryRejectsConfigurationChangesAndClockRollbackWithoutLosingOwnership()
    {
        using var fixture = new Fixture();
        using (var coordinator = fixture.Open()) fixture.Enqueue(coordinator);
        Assert.Throws<InvalidDataException>(() => fixture.Open(99));
        Assert.Throws<InvalidDataException>(() => new DurableEvolutionWorkCoordinator(fixture.Path, "other", fixture.Compatibility, Cost(100), fixture.Options, () => fixture.Now));
        Assert.Throws<InvalidDataException>(() => new DurableEvolutionWorkCoordinator(fixture.Path, fixture.RunId, "other", Cost(100), fixture.Options, () => fixture.Now));
        fixture.Advance(-1); Assert.Throws<InvalidOperationException>(() => fixture.Open());
        fixture.Advance(1); using var restored = fixture.Open();
        fixture.Advance(-1); Assert.Throws<InvalidOperationException>(() => restored.Claim(fixture.Worker("a")));
        fixture.Advance(1); Assert.NotNull(restored.Claim(fixture.Worker("a")));
    }

    [Fact]
    public void ReceiptAndPayloadValidationPreservesPendingReservations()
    {
        using var fixture = new Fixture(); using var coordinator = fixture.Open(); fixture.Enqueue(coordinator);
        var lease = coordinator.Claim(fixture.Worker("a"))!;
        Assert.Throws<ArgumentException>(() => coordinator.Commit(lease.Identity, "a", new string('x', 4097), "receipt", Cost(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Commit(lease.Identity, "a", "x", "receipt", Cost(2), EvolutionResourceOutcome.Unknown));
        Assert.Throws<ArgumentException>(() => coordinator.Commit(lease.Identity, "a", "x", "receipt", EvolutionResources.Of("undeclared", 1)));
        Assert.Equal(5, coordinator.Resources.Reserved["cost_units"]); Assert.Null(coordinator.GetResult(1, 1));
        Assert.Equal(EvolutionWorkCommitDisposition.Accepted, coordinator.Commit(lease.Identity, "a", "x", "receipt", Cost(2)));
    }

    [Fact]
    public void RetentionLimitsNeverForgetDuplicateProtectionOrDispatchPastDeliveryCap()
    {
        using var fixture = new Fixture(new EvolutionWorkCoordinatorOptions(1, 1, 256 * 1024, 4096, 1, TimeSpan.FromSeconds(10)));
        using var coordinator = fixture.Open(); fixture.Enqueue(coordinator);
        Assert.Throws<InvalidOperationException>(() => fixture.Enqueue(coordinator, 2));
        var lease = coordinator.Claim(fixture.Worker("a"))!; fixture.Advance(11);
        Assert.Equal(EvolutionWorkHeartbeat.Expired, coordinator.Heartbeat(lease.Identity, "a"));
        Assert.Null(coordinator.Claim(fixture.Worker("a")));
        Assert.Throws<InvalidOperationException>(() => coordinator.Claim(fixture.Worker("b")));
        Assert.Equal(1, coordinator.Resources.Admitted); Assert.False(fixture.Enqueue(coordinator));
    }

    private static EvolutionResources Cost(decimal value) => EvolutionResources.Of("cost_units", value);
    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "evolution-coordinator-" + Guid.NewGuid().ToString("N"));
        internal string RunId => "durable-test";
        internal string Compatibility => "task-evaluator-codec-v1";
        internal DateTimeOffset Now { get; private set; } = new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero);
        internal EvolutionWorkCoordinatorOptions Options { get; }
        internal Fixture(EvolutionWorkCoordinatorOptions? options = null) => Options = options ?? new(16, 3, 256 * 1024, 4096, 8, TimeSpan.FromSeconds(10));
        internal void Advance(int seconds) => Now = Now.AddSeconds(seconds);
        internal DurableEvolutionWorkCoordinator Open(decimal cap = 100) => new(Path, RunId, Compatibility, Cost(cap), Options, () => Now);
        internal EvolutionWorkerProfile Worker(string id) => new(id, Compatibility);
        internal bool Enqueue(DurableEvolutionWorkCoordinator coordinator, long id = 1, EvolutionWorkRequirements? requirements = null) =>
            coordinator.Enqueue(id, 1, "genome-" + id, "same display", requirements ?? new(), Cost(1), Cost(5));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
