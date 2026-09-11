using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Optional single-owner durable external-work delivery, fencing and resource reconciliation.</summary>
/// <remarks>
/// Every dispatch and result is atomically persisted with its ledger before acknowledgement. Expiry/cancellation
/// never frees an unreported physical-work reservation: a late receipt settles the original delivery, not its retry.
/// This local-filesystem coordinator is not a network server, worker authenticator or exact search-state restorer.
/// Restoring delivery does not restore pending proposals or operator state; partial-batch search continuation is a fork.
/// Do not use network filesystems, revert its directory to an older backup, or infer power-loss durability from process-crash tests.
/// </remarks>
public sealed partial class DurableEvolutionWorkCoordinator : IDisposable
{
    private readonly object _sync = new();
    private readonly EvolutionWorkJournal _journal;
    private readonly EvolutionWorkCoordinatorOptions _options;
    private readonly EvolutionResources _limits;
    private readonly string _contractHash;
    private readonly Func<DateTimeOffset> _utcNow;
    private EvolutionWorkState _state;
    private EvolutionResourceLedger _ledger;
    private long _lastObservedUtcTicks;

    /// <summary>Creates or recovers a bounded delivery run with an exact, immutable compatibility/budget configuration.</summary>
    /// <remarks>The clock must not move backwards. Time is supplied separately to enable deterministic expiry tests;
    /// production callers should use UTC and treat clock-skew errors as an operational fault, not reset stored time.</remarks>
    public DurableEvolutionWorkCoordinator(string directory, string runId, string compatibilityHash, EvolutionResources limits,
        EvolutionWorkCoordinatorOptions? options = null, Func<DateTimeOffset>? utcNow = null)
    {
        EvolutionWorkValidation.Id(runId, nameof(runId)); EvolutionWorkValidation.Id(compatibilityHash, nameof(compatibilityHash));
        Guard.NotNull(limits);
        RunId = runId; CompatibilityHash = compatibilityHash; _limits = limits;
        _options = options ?? new EvolutionWorkCoordinatorOptions(); _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _ledger = NewLedger();
        _contractHash = EvolutionHash.Combine(new[] { "durable-external-work-v2", runId, compatibilityHash,
            Number(_options.MaximumWorkItems), Number(_options.MaximumDeliveriesPerWork), Number(_options.MaximumStateBytes),
            Number(_options.MaximumPayloadBytes), Number(_options.MaximumWorkers), Number(_options.LeaseDuration.Ticks) }
            .Concat(limits.Amounts.SelectMany(p => new[] { p.Key, p.Value.ToString(CultureInfo.InvariantCulture) })));
        _state = new EvolutionWorkState { Schema = 2, ContractHash = _contractHash, Ledger = _ledger.CaptureState(), LastUtcTicks = Now() };
        _journal = new EvolutionWorkJournal(directory, Serialize(_state), _options.MaximumStateBytes);
        try
        {
            _state = Deserialize(_journal.Payload);
            ValidateState(_state);
            _ledger = NewLedger(); _ledger.RestoreState(_state.Ledger);
            _lastObservedUtcTicks = _state.LastUtcTicks;
            _ = Now(); // Refuse clock rollback immediately on recovery, before work can be returned.
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or OverflowException)
        {
            _journal.Dispose();
            throw new InvalidDataException("Durable work state contains invalid persisted values.", ex);
        }
        catch { _journal.Dispose(); throw; }
    }

    /// <summary>Gets the run-scoped delivery and accounting identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the caller-supplied task/evaluator/canonicalizer/codec contract required of workers.</summary>
    public string CompatibilityHash { get; }
    /// <summary>Gets whether existing delivery state was recovered rather than a fresh run created.</summary>
    public bool WasRecovered => _journal.WasRecovered;
    /// <summary>Gets whether this live coordinator failed to remove its own temporary publication file.</summary>
    /// <remarks>Readable after a publication failure; diagnostic only, not proof of which state committed.
    /// The flag is not persisted and never authorizes deleting current state or resetting reservations.</remarks>
    public bool HasTemporaryCleanupFailure { get { lock (_sync) return _journal.HasTemporaryCleanupFailure; } }
    /// <summary>Always false: delivery persistence does not persist pending proposals or operator state.</summary>
    public bool SupportsExactSearchContinuation => false;
    /// <summary>Gets the explicit guarantee a caller must preserve when reporting a restarted search.</summary>
    public string SearchContinuationGuarantee => "delivery-only; partial-batch search continuation requires an explicit fork";
    /// <summary>Gets settled charges and still-reserved liabilities, including expired/canceled work without receipts.</summary>
    public EvolutionResourceSnapshot Resources { get { lock (_sync) { _journal.EnsureUsable(); return _ledger.Snapshot(); } } }
    internal Action<bool>? Publishing { set => _journal.Publishing = value; }

    /// <summary>Durably adds one engine attempt; identical submissions are idempotent and conflicting reuse is refused.</summary>
    public bool Enqueue(long evaluationId, int attempt, string canonicalGenomeId, string serializedGenome,
        EvolutionWorkRequirements requirements, EvolutionResources estimated, EvolutionResources maximum)
        => EnqueueCore(evaluationId, attempt, canonicalGenomeId, serializedGenome, requirements, estimated, maximum, null);

    private bool EnqueueCore(long evaluationId, int attempt, string canonicalGenomeId, string serializedGenome,
        EvolutionWorkRequirements requirements, EvolutionResources estimated, EvolutionResources maximum, EvolutionWorkIdentity? source)
    {
        string key = Key(evaluationId, attempt);
        Guard.NotNullOrWhiteSpace(canonicalGenomeId);
        EvolutionWorkValidation.Payload(canonicalGenomeId, _options.MaximumPayloadBytes, nameof(canonicalGenomeId));
        EvolutionWorkValidation.Payload(serializedGenome, _options.MaximumPayloadBytes, nameof(serializedGenome));
        Guard.NotNull(requirements); ValidateCost(estimated, maximum);
        var incoming = new WorkItemState
        {
            EvaluationId = evaluationId,
            Attempt = attempt,
            CanonicalGenomeId = canonicalGenomeId,
            Payload = serializedGenome,
            Tags = requirements.Tags.ToArray(),
            MinimumResources = EvolutionWorkValidation.Copy(requirements.MinimumResources),
            Estimated = EvolutionWorkValidation.Copy(estimated),
            Maximum = EvolutionWorkValidation.Copy(maximum),
            SourceLeaseId = source?.LeaseId,
        };
        lock (_sync)
        {
            _journal.EnsureUsable();
            if ((_state.SourceSessionId is null) != (source is null) || (source is not null && source.RunId != RunId))
                throw new InvalidOperationException("Attached sessions require their original source work identity; unbound work cannot be mixed in.");
            if (_state.Work.TryGetValue(key, out WorkItemState? prior))
            {
                if (!SameRequest(prior, incoming)) throw new InvalidOperationException("A conflicting request cannot reuse an engine attempt identity.");
                return false;
            }
            if (_state.Work.Count >= _options.MaximumWorkItems) throw new InvalidOperationException("The retained work-item limit was reached.");
            var next = Next(); next.State.Work.Add(key, incoming); Publish(next.State, next.Ledger, Now()); return true;
        }
    }

    /// <summary>Durably reserves and leases one compatible pending item, or returns null without dispatching.</summary>
    /// <remarks>Expired deliveries continue to occupy accounting and worker capacity until their final receipts arrive.
    /// A restarted process uses a new worker incarnation; external supervisors must enforce declared hardware isolation.</remarks>
    public EvolutionWorkLease? Claim(EvolutionWorkerProfile worker)
    {
        Guard.NotNull(worker);
        lock (_sync)
        {
            var next = Next(); long now = Now(); bool changed = Expire(next.State, now);
            if (worker.CompatibilityHash != CompatibilityHash)
            {
                if (changed) Publish(next.State, next.Ledger, now);
                return null;
            }
            var declared = new WorkerState
            {
                CompatibilityHash = worker.CompatibilityHash,
                Tags = worker.Tags.ToArray(),
                Capacity = EvolutionWorkValidation.Copy(worker.Capacity),
                MaximumConcurrentWork = worker.MaximumConcurrentWork
            };
            if (!next.State.Workers.TryGetValue(worker.WorkerId, out WorkerState? prior) || !SameWorker(prior, declared))
            {
                if (Unsettled(next.State, worker.WorkerId).Any()) throw new InvalidOperationException("A worker with unsettled deliveries cannot change its profile.");
                if (prior is null && next.State.Workers.Count >= _options.MaximumWorkers) throw new InvalidOperationException("The retained worker-profile limit was reached.");
                next.State.Workers[worker.WorkerId] = declared; changed = true;
            }
            WorkItemState[] occupied = Unsettled(next.State, worker.WorkerId).ToArray();
            foreach (WorkItemState job in next.State.Work.Values.OrderBy(w => w.EvaluationId).ThenBy(w => w.Attempt))
            {
                if (job.Status != WorkItemStatus.Pending || occupied.Length >= worker.MaximumConcurrentWork
                    || !job.Tags.All(tag => worker.Tags.Contains(tag, StringComparer.Ordinal))
                    || job.MinimumResources.Any(p => p.Value + occupied.Sum(w => Amount(w.MinimumResources, p.Key)) > worker.Capacity[p.Key])) continue;
                string leaseId = Guid.NewGuid().ToString("N");
                var reservation = next.Ledger.TryReserve(leaseId, EvolutionResourceStage.Evaluation,
                    new EvolutionResources(job.Estimated), new EvolutionResources(job.Maximum), job.Attempt);
                changed = true; // Denial counters are durable too.
                if (reservation is null) continue;
                var lease = new WorkLeaseState { LeaseId = leaseId, WorkerId = worker.WorkerId, ExpiresUtcTicks = Deadline(now) };
                job.Leases.Add(lease); job.Status = WorkItemStatus.Leased;
                Publish(next.State, next.Ledger, now);
                return Lease(job, lease);
            }
            if (changed) Publish(next.State, next.Ledger, now);
            return null;
        }
    }

    /// <summary>Renews a still-live lease or tells its worker to stop; it never revives expired/canceled work.</summary>
    public EvolutionWorkHeartbeat Heartbeat(EvolutionWorkIdentity identity, string workerId)
    {
        ValidateTicket(identity, workerId);
        lock (_sync)
        {
            var next = Next(); long now = Now(); bool changed = Expire(next.State, now);
            EvolutionWorkHeartbeat result;
            if (!Find(next.State, identity, workerId, out _, out WorkLeaseState? lease)) result = EvolutionWorkHeartbeat.UnknownLease;
            else if (lease.Actual is not null) result = EvolutionWorkHeartbeat.Completed;
            else if (lease.CancelRequested) result = EvolutionWorkHeartbeat.Canceled;
            else if (lease.Expired) result = EvolutionWorkHeartbeat.Expired;
            else { lease.ExpiresUtcTicks = Deadline(now); changed = true; result = EvolutionWorkHeartbeat.Renewed; }
            if (changed) Publish(next.State, next.Ledger, now);
            return result;
        }
    }

    /// <summary>Persists cancellation without refunding dispatched work or pretending its process has stopped.</summary>
    public bool Cancel(long evaluationId, int attempt)
    {
        string key = Key(evaluationId, attempt);
        lock (_sync)
        {
            var next = Next();
            if (!next.State.Work.TryGetValue(key, out WorkItemState? job) || job.Status is WorkItemStatus.Completed or WorkItemStatus.Canceled) return false;
            job.Status = WorkItemStatus.Canceled;
            foreach (WorkLeaseState lease in job.Leases.Where(l => l.Actual is null)) lease.CancelRequested = true;
            Publish(next.State, next.Ledger, Now()); return true;
        }
    }

    /// <summary>Atomically settles the original physical reservation and, only for the current lease, its logical result.</summary>
    /// <remarks>Identical result/provenance/receipt replay is idempotent, including stale results. Conflicting duplicates
    /// are rejected. Expired results may settle their own cost but cannot overwrite a replacement. No receipt is refunded
    /// because the search rejected it. Reported maximum violations are charged and stop further admission.</remarks>
    public EvolutionWorkCommitDisposition Commit(EvolutionWorkIdentity identity, string workerId, string serializedResult,
        string provenance, EvolutionResources actual, EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Completed)
    {
        ValidateTicket(identity, workerId);
        EvolutionWorkValidation.Payload(serializedResult, _options.MaximumPayloadBytes, nameof(serializedResult));
        EvolutionWorkValidation.Payload(provenance, _options.MaximumPayloadBytes, nameof(provenance));
        Guard.NotNull(actual);
        if (!Enum.IsDefined(typeof(EvolutionResourceOutcome), outcome) || outcome == EvolutionResourceOutcome.Unknown)
            throw new ArgumentOutOfRangeException(nameof(outcome), "A worker must report an actual receipt, not guess an unknown amount.");
        lock (_sync)
        {
            var next = Next(); long now = Now(); bool changed = Expire(next.State, now);
            if (!Find(next.State, identity, workerId, out WorkItemState? job, out WorkLeaseState? lease))
            {
                if (changed) Publish(next.State, next.Ledger, now);
                return EvolutionWorkCommitDisposition.UnknownLease;
            }
            if (lease.Actual is not null)
            {
                if (lease.Result != serializedResult || lease.Provenance != provenance || lease.Outcome != outcome
                    || !Equal(lease.Actual, EvolutionWorkValidation.Copy(actual)))
                    throw new InvalidOperationException("A conflicting receipt/result cannot replace a committed delivery.");
                if (changed) Publish(next.State, next.Ledger, now);
                return lease.Accepted ? EvolutionWorkCommitDisposition.Duplicate : EvolutionWorkCommitDisposition.DuplicateStale;
            }
            next.Ledger.GetPendingReservation(lease.LeaseId).Complete(actual, outcome);
            bool violation = actual.Amounts.Any(p => p.Value > Amount(job.Maximum, p.Key));
            lease.Actual = EvolutionWorkValidation.Copy(actual); lease.Result = serializedResult;
            lease.Provenance = provenance; lease.Outcome = outcome;
            lease.Accepted = !violation && job.Status == WorkItemStatus.Leased && !lease.Expired && !lease.CancelRequested
                && ReferenceEquals(job.Leases[job.Leases.Count - 1], lease);
            if (lease.Accepted) job.Status = WorkItemStatus.Completed;
            else if (violation && job.Status == WorkItemStatus.Leased && ReferenceEquals(job.Leases[job.Leases.Count - 1], lease))
                job.Status = WorkItemStatus.Canceled;
            Publish(next.State, next.Ledger, now);
            return violation ? EvolutionWorkCommitDisposition.BudgetViolation
                : lease.Accepted ? EvolutionWorkCommitDisposition.Accepted : EvolutionWorkCommitDisposition.Stale;
        }
    }

    /// <summary>Gets the persisted logical result, or null until a current lease commits one. Reading does not consume it.</summary>
    public EvolutionCommittedWork? GetResult(long evaluationId, int attempt)
    {
        string key = Key(evaluationId, attempt);
        lock (_sync)
        {
            _journal.EnsureUsable();
            if (!_state.Work.TryGetValue(key, out WorkItemState? job)) return null;
            WorkLeaseState? lease = job.Leases.SingleOrDefault(l => l.Accepted);
            return lease is null ? null : Result(job, lease);
        }
    }

    /// <summary>Gets a delivery's persisted evidence even when stale, canceled or over budget.</summary>
    public EvolutionCommittedWork? GetDeliveryResult(EvolutionWorkIdentity identity, string workerId)
    {
        ValidateTicket(identity, workerId);
        lock (_sync)
        {
            _journal.EnsureUsable();
            return Find(_state, identity, workerId, out WorkItemState? job, out WorkLeaseState? lease) && lease.Actual is not null
                ? Result(job, lease) : null;
        }
    }

    /// <summary>Gets this incarnation's unresolved deliveries after reconnecting; this does not create new reservations.</summary>
    /// <remarks>For reconciling an already-started physical operation, not authorization to execute it a second time.
    /// Check heartbeat/cancellation before continuing. Worker-side durable operation identity is required to avoid duplicate
    /// physical execution; coordinator idempotency alone cannot provide that guarantee.</remarks>
    public IReadOnlyList<EvolutionWorkLease> GetUnsettledDeliveries(string workerId)
    {
        EvolutionWorkValidation.Id(workerId, nameof(workerId));
        lock (_sync)
        {
            _journal.EnsureUsable();
            return Array.AsReadOnly(_state.Work.Values.SelectMany(job => job.Leases
                .Where(l => l.WorkerId == workerId && l.Actual is null).Select(l => Lease(job, l))).ToArray());
        }
    }

    /// <summary>Releases coordinator ownership, leaving every work item and reservation durable and unresolved as appropriate.</summary>
    public void Dispose() { lock (_sync) _journal.Dispose(); }

    private EvolutionResourceLedger NewLedger() => new(RunId, _limits, 1024, _options.MaximumWorkItems * _options.MaximumDeliveriesPerWork);
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Key(long evaluationId, int attempt)
    {
        if (evaluationId < 0) throw new ArgumentOutOfRangeException(nameof(evaluationId));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        return Number(evaluationId) + ":" + Number(attempt);
    }
    private long Now()
    {
        long now = _utcNow().UtcDateTime.Ticks;
        if (now < _lastObservedUtcTicks) throw new InvalidOperationException("Coordinator UTC clock moved backwards; dispatch is refused.");
        _lastObservedUtcTicks = now; return now;
    }
    private long Deadline(long now)
    {
        if (now > DateTime.MaxValue.Ticks - _options.LeaseDuration.Ticks) throw new InvalidOperationException("Lease deadline exceeds the supported clock range.");
        return now + _options.LeaseDuration.Ticks;
    }
    private static string Serialize(EvolutionWorkState state) => JsonSerializer.Serialize(state, EvolutionWorkJsonContext.Default.EvolutionWorkState);
    private static EvolutionWorkState Deserialize(string json) => JsonSerializer.Deserialize(json, EvolutionWorkJsonContext.Default.EvolutionWorkState)
        ?? throw new InvalidDataException("Missing durable work state.");
    private (EvolutionWorkState State, EvolutionResourceLedger Ledger) Next()
    {
        _journal.EnsureUsable();
        EvolutionWorkState state = Deserialize(_journal.Payload);
        EvolutionResourceLedger ledger = NewLedger(); ledger.RestoreState(state.Ledger); return (state, ledger);
    }
    private void Publish(EvolutionWorkState state, EvolutionResourceLedger ledger, long now)
    {
        state.Ledger = ledger.CaptureState(); state.LastUtcTicks = now;
        _journal.Commit(Serialize(state)); _state = state; _ledger = ledger;
    }
    private bool Expire(EvolutionWorkState state, long now)
    {
        bool changed = false;
        foreach (WorkItemState job in state.Work.Values)
        {
            if (job.Status != WorkItemStatus.Leased) continue;
            WorkLeaseState lease = job.Leases[job.Leases.Count - 1];
            if (lease.ExpiresUtcTicks > now) continue;
            lease.Expired = true;
            job.Status = job.Leases.Count < _options.MaximumDeliveriesPerWork ? WorkItemStatus.Pending : WorkItemStatus.DeliveryLimitReached;
            changed = true;
        }
        return changed;
    }
    private static IEnumerable<WorkItemState> Unsettled(EvolutionWorkState state, string workerId) => state.Work.Values
        .SelectMany(job => job.Leases.Where(lease => lease.WorkerId == workerId && lease.Actual is null).Select(_ => job));
    private static decimal Amount(Dictionary<string, decimal> values, string key) => values.TryGetValue(key, out decimal value) ? value : 0;
    private static bool Equal(Dictionary<string, decimal> a, Dictionary<string, decimal> b) => a.Count == b.Count && a.All(p => b.TryGetValue(p.Key, out decimal value) && p.Value == value);
    private static bool SameWorker(WorkerState a, WorkerState b) => a.CompatibilityHash == b.CompatibilityHash
        && a.MaximumConcurrentWork == b.MaximumConcurrentWork && a.Tags.SequenceEqual(b.Tags) && Equal(a.Capacity, b.Capacity);
    private static bool SameRequest(WorkItemState a, WorkItemState b) => a.SourceLeaseId == b.SourceLeaseId && a.CanonicalGenomeId == b.CanonicalGenomeId && a.Payload == b.Payload
        && a.Tags.SequenceEqual(b.Tags) && Equal(a.MinimumResources, b.MinimumResources) && Equal(a.Estimated, b.Estimated) && Equal(a.Maximum, b.Maximum);
    private void ValidateCost(EvolutionResources estimated, EvolutionResources maximum)
    {
        Guard.NotNull(estimated); Guard.NotNull(maximum);
        if (estimated.Amounts.Keys.Concat(maximum.Amounts.Keys).Any(key => !_limits.Amounts.ContainsKey(key))
            || estimated.Amounts.Any(p => p.Value > maximum[p.Key])) throw new ArgumentException("Costs need declared resource limits and estimate <= maximum.");
    }
    private static void ValidateTicket(EvolutionWorkIdentity identity, string workerId)
    { Guard.NotNull(identity); EvolutionWorkValidation.Id(workerId, nameof(workerId)); }
    private bool Find(EvolutionWorkState state, EvolutionWorkIdentity identity, string workerId,
        out WorkItemState job, out WorkLeaseState lease)
    {
        job = null!; lease = null!;
        if (identity.RunId != RunId || !state.Work.TryGetValue(Key(identity.EvaluationId, identity.Attempt), out job!)) return false;
        lease = job.Leases.SingleOrDefault(l => l.LeaseId == identity.LeaseId && l.WorkerId == workerId)!;
        return lease is not null;
    }
    private EvolutionWorkIdentity Identity(WorkItemState job, WorkLeaseState lease) => new(RunId, job.EvaluationId, job.Attempt, lease.LeaseId);
    private EvolutionWorkLease Lease(WorkItemState job, WorkLeaseState lease) => new(Identity(job, lease), lease.WorkerId,
        job.CanonicalGenomeId, job.Payload, job.Leases.IndexOf(lease) + 1, lease.ExpiresUtcTicks);
    private EvolutionCommittedWork Result(WorkItemState job, WorkLeaseState lease) => new(Identity(job, lease), lease.Result!, lease.Provenance!,
        new EvolutionResources(lease.Actual!), lease.Outcome!.Value, lease.Accepted);

    private void ValidateState(EvolutionWorkState state)
    {
        if (state.Schema != 2 || state.ContractHash != _contractHash || state.Work is null || state.Workers is null
            || state.Work.Count > _options.MaximumWorkItems || state.Workers.Count > _options.MaximumWorkers
            || state.LastUtcTicks < 0 || state.LastUtcTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("Durable work state/configuration is incompatible or invalid.");
        EvolutionResourceLedger ledger = NewLedger(); ledger.RestoreState(state.Ledger);
        EvolutionResourceLedger.State receipts = JsonSerializer.Deserialize(state.Ledger, EvolutionWorkJsonContext.Default.ResourceLedgerState)!;
        var operations = receipts.Operations!.ToDictionary(o => o.Id, StringComparer.Ordinal);
        if (state.SourceSessionId is not null) _ = new EvolutionWorkIdentity(RunId, 0, 1, state.SourceSessionId);
        foreach (var worker in state.Workers)
        {
            var validated = new EvolutionWorkerProfile(worker.Key, worker.Value.CompatibilityHash, worker.Value.Tags,
                new EvolutionResources(worker.Value.Capacity), worker.Value.MaximumConcurrentWork);
            if (validated.CompatibilityHash != CompatibilityHash) throw new InvalidDataException("Worker compatibility mismatch.");
        }
        int count = 0;
        var leaseIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in state.Work)
        {
            WorkItemState job = entry.Value;
            if (entry.Key != Key(job.EvaluationId, job.Attempt) || job.Leases is null || job.Leases.Count > _options.MaximumDeliveriesPerWork
                || !Enum.IsDefined(typeof(WorkItemStatus), job.Status)) throw new InvalidDataException("Invalid work identity/state.");
            Guard.NotNullOrWhiteSpace(job.CanonicalGenomeId);
            EvolutionWorkValidation.Payload(job.CanonicalGenomeId, _options.MaximumPayloadBytes, nameof(job.CanonicalGenomeId));
            if ((state.SourceSessionId is null) != (job.SourceLeaseId is null) ||
                (job.SourceTellAccepted.HasValue && (job.SourceLeaseId is null || job.Status != WorkItemStatus.Completed)))
                throw new InvalidDataException("Invalid source-session attachment or result acknowledgement.");
            if (job.SourceLeaseId is not null) _ = new EvolutionWorkIdentity(RunId, job.EvaluationId, job.Attempt, job.SourceLeaseId);
            EvolutionWorkValidation.Payload(job.Payload, _options.MaximumPayloadBytes, nameof(job.Payload));
            _ = new EvolutionWorkRequirements(job.Tags, new EvolutionResources(job.MinimumResources));
            ValidateCost(new EvolutionResources(job.Estimated), new EvolutionResources(job.Maximum));
            int accepted = 0;
            foreach (WorkLeaseState lease in job.Leases)
            {
                _ = Identity(job, lease); count++;
                if (!leaseIds.Add(lease.LeaseId) || !state.Workers.ContainsKey(lease.WorkerId)
                    || lease.ExpiresUtcTicks < 1 || lease.ExpiresUtcTicks > DateTime.MaxValue.Ticks
                    || !operations.TryGetValue(lease.LeaseId, out EvolutionResourceLedger.OperationState? operation)
                    || operation.Stage != EvolutionResourceStage.Evaluation || operation.Attempt != job.Attempt
                    || !Equal(operation.Estimated!, job.Estimated) || !Equal(operation.Maximum!, job.Maximum)
                    || operation.Outcome != lease.Outcome || (operation.Charged is null) != (lease.Actual is null)
                    || (lease.Actual is not null && !Equal(operation.Charged!, lease.Actual)))
                    throw new InvalidDataException("Work lease and accounting state disagree.");
                if (lease.Actual is null)
                {
                    if (lease.Result is not null || lease.Provenance is not null || lease.Outcome is not null || lease.Accepted)
                        throw new InvalidDataException("Unsettled delivery has a committed result.");
                }
                else
                {
                    EvolutionWorkValidation.Payload(lease.Result!, _options.MaximumPayloadBytes, nameof(lease.Result));
                    EvolutionWorkValidation.Payload(lease.Provenance!, _options.MaximumPayloadBytes, nameof(lease.Provenance));
                    if (lease.Outcome is null or EvolutionResourceOutcome.Unknown) throw new InvalidDataException("Invalid final worker receipt.");
                    if (lease.Accepted)
                    {
                        if (lease.Expired || lease.CancelRequested || !ReferenceEquals(job.Leases.Last(), lease)
                            || lease.Actual.Any(p => p.Value > Amount(job.Maximum, p.Key)))
                            throw new InvalidDataException("An invalid or stale lease cannot be the accepted logical result.");
                        accepted++;
                    }
                }
            }
            if (accepted != (job.Status == WorkItemStatus.Completed ? 1 : 0))
                throw new InvalidDataException("Logical work status disagrees with its accepted results.");
            bool deliveriesValid = job.Status switch
            {
                WorkItemStatus.Pending => job.Leases.Count < _options.MaximumDeliveriesPerWork,
                WorkItemStatus.DeliveryLimitReached => job.Leases.Count == _options.MaximumDeliveriesPerWork,
                WorkItemStatus.Leased => job.Leases.LastOrDefault() is { Actual: null, Expired: false, CancelRequested: false },
                _ => true
            };
            if (!deliveriesValid)
                throw new InvalidDataException("Logical work status disagrees with its deliveries.");
        }
        if (count != operations.Count) throw new InvalidDataException("Orphaned resource operations in durable work state.");
    }
}
