using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Optional single-owner durable external-work delivery, fencing and resource reconciliation.</summary>
/// <remarks>
/// Every dispatch and result is durably logged with its ledger change before acknowledgement. Expiry/cancellation
/// never frees an unreported physical-work reservation: a late receipt settles the original delivery, not its retry.
/// This local-filesystem coordinator is not a network server, worker authenticator or exact search-state restorer.
/// Restoring delivery does not restore pending proposals or operator state; partial-batch search continuation is a fork.
/// Do not use network filesystems, revert its directory to an older backup, or infer power-loss durability from process-crash tests.
/// <para>DURABILITY WITHOUT WHOLE-STATE REWRITES (#119). A mutation changes the live state in memory under the
/// coordinator lock, appends one record describing the change, releases the lock, and only then waits for that record
/// to reach disk. The wait is a group commit: concurrent callers share one flush. A call returns -- and a protocol
/// endpoint replies -- only after every record up to the state it observed is durable, so nothing a caller is told
/// can be lost by a crash, and a read never reports state that is not yet on disk.</para>
/// </remarks>
public sealed partial class DurableEvolutionWorkCoordinator : IDisposable
{
    /// <summary>The ledger's retained-receipt window; replay trims settled receipts to the same bound.</summary>
    private const int RetainedReceipts = 1024;
    /// <summary>How far one logged byte can grow the serialized state. A record carries work items and worker
    /// profiles exactly as the state stores them (growth at most 1x), and ledger operations as JSON that the state
    /// stores escaped inside a string (at most 2x: the escaped text is ASCII, and only quotes and backslashes double).
    /// 3 leaves margin; the bound only decides when the exact size must be measured.</summary>
    private const int StateGrowthPerRecordByte = 3;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    [ThreadStatic] private static DurabilityScope? t_durability;
    private readonly object _sync = new();
    private readonly EvolutionWorkJournal _journal;
    private readonly EvolutionWorkCoordinatorOptions _options;
    private readonly EvolutionResources _limits;
    private readonly string _contractHash;
    private readonly Func<DateTimeOffset> _utcNow;
    private EvolutionWorkState _state;
    private EvolutionResourceLedger _ledger;
    private long _lastObservedUtcTicks;
    // An upper bound on the serialized state's UTF-8 size: exact after open or compaction, then raised by
    // StateGrowthPerRecordByte per appended byte. The exact size is measured only when the bound reaches the limit.
    private long _stateBytesBound;

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
            EvolutionWorkState state = Deserialize(_journal.Payload);
            if (_journal.RecoveredRecords.Count != 0) Replay(state, _journal.RecoveredRecords);
            ValidateState(state);
            _state = state;
            _ledger = NewLedger(); _ledger.RestoreState(_state.Ledger);
            _lastObservedUtcTicks = _state.LastUtcTicks;
            _stateBytesBound = Utf8.GetByteCount(Serialize(_state));
            if (_stateBytesBound > _options.MaximumStateBytes) throw new InvalidDataException("Recovered work state exceeds the configured byte limit.");
            _ = Now(); // Refuse clock rollback immediately on recovery, before work can be returned.
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException or OverflowException or KeyNotFoundException)
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
    public EvolutionResourceSnapshot Resources => Read(() => _ledger.Snapshot());
    internal Action<bool>? Publishing { set => _journal.Publishing = value; }
    /// <summary>The underlying store, for tests that observe sequence numbers and group-commit flushes.</summary>
    internal EvolutionWorkJournal Journal => _journal;

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
        return Run(tx =>
        {
            if ((_state.SourceSessionId is null) != (source is null) || (source is not null && source.RunId != RunId))
                throw new InvalidOperationException("Attached sessions require their original source work identity; unbound work cannot be mixed in.");
            if (tx.Get(key) is { } prior)
            {
                if (!SameRequest(prior, incoming)) throw new InvalidOperationException("A conflicting request cannot reuse an engine attempt identity.");
                return false;
            }
            if (tx.WorkCount >= _options.MaximumWorkItems) throw new InvalidOperationException("The retained work-item limit was reached.");
            tx.Add(key, incoming);
            return true;
        });
    }

    /// <summary>Durably reserves and leases one compatible pending item, or returns null without dispatching.</summary>
    /// <remarks>Expired deliveries continue to occupy accounting and worker capacity until their final receipts arrive.
    /// A restarted process uses a new worker incarnation; external supervisors must enforce declared hardware isolation.</remarks>
    public EvolutionWorkLease? Claim(EvolutionWorkerProfile worker)
    {
        Guard.NotNull(worker);
        return Run(tx =>
        {
            long now = Now(); tx.Now = now; Expire(tx, now);
            if (worker.CompatibilityHash != CompatibilityHash) return null;
            var declared = new WorkerState
            {
                CompatibilityHash = worker.CompatibilityHash,
                Tags = worker.Tags.ToArray(),
                Capacity = EvolutionWorkValidation.Copy(worker.Capacity),
                MaximumConcurrentWork = worker.MaximumConcurrentWork
            };
            WorkerState? prior = tx.Worker(worker.WorkerId);
            if (prior is null || !SameWorker(prior, declared))
            {
                if (Unsettled(tx, worker.WorkerId).Any()) throw new InvalidOperationException("A worker with unsettled deliveries cannot change its profile.");
                if (prior is null && tx.WorkerCount >= _options.MaximumWorkers) throw new InvalidOperationException("The retained worker-profile limit was reached.");
                tx.SetWorker(worker.WorkerId, declared);
            }
            WorkItemState[] occupied = Unsettled(tx, worker.WorkerId).ToArray();
            foreach (KeyValuePair<string, WorkItemState> entry in tx.Jobs().OrderBy(p => p.Value.EvaluationId).ThenBy(p => p.Value.Attempt).ToArray())
            {
                WorkItemState job = entry.Value;
                if (job.Status != WorkItemStatus.Pending || occupied.Length >= worker.MaximumConcurrentWork
                    || !job.Tags.All(tag => worker.Tags.Contains(tag, StringComparer.Ordinal))
                    || job.MinimumResources.Any(p => p.Value + occupied.Sum(w => Amount(w.MinimumResources, p.Key)) > worker.Capacity[p.Key])) continue;
                string leaseId = Guid.NewGuid().ToString("N");
                // A denial changes the ledger's durable denial counter, so it is logged even when nothing is leased.
                var reservation = _ledger.TryReserve(leaseId, EvolutionResourceStage.Evaluation,
                    new EvolutionResources(job.Estimated), new EvolutionResources(job.Maximum), job.Attempt);
                if (reservation is null) continue;
                var lease = new WorkLeaseState { LeaseId = leaseId, WorkerId = worker.WorkerId, ExpiresUtcTicks = Deadline(now) };
                WorkItemState leased = tx.Edit(entry.Key);
                leased.Leases.Add(lease); leased.Status = WorkItemStatus.Leased;
                return Lease(leased, lease);
            }
            return null;
        });
    }

    /// <summary>Renews a still-live lease or tells its worker to stop; it never revives expired/canceled work.</summary>
    public EvolutionWorkHeartbeat Heartbeat(EvolutionWorkIdentity identity, string workerId)
    {
        ValidateTicket(identity, workerId);
        return Run(tx =>
        {
            long now = Now(); tx.Now = now; Expire(tx, now);
            if (Locate(tx.Get, identity, workerId) is not { } found) return EvolutionWorkHeartbeat.UnknownLease;
            WorkLeaseState lease = found.Job.Leases[found.Index];
            if (lease.Actual is not null) return EvolutionWorkHeartbeat.Completed;
            if (lease.CancelRequested) return EvolutionWorkHeartbeat.Canceled;
            if (lease.Expired) return EvolutionWorkHeartbeat.Expired;
            long deadline = Deadline(now);
            tx.Edit(found.Key).Leases[found.Index].ExpiresUtcTicks = deadline;
            return EvolutionWorkHeartbeat.Renewed;
        });
    }

    /// <summary>Persists cancellation without refunding dispatched work or pretending its process has stopped.</summary>
    public bool Cancel(long evaluationId, int attempt)
    {
        string key = Key(evaluationId, attempt);
        return Run(tx =>
        {
            if (tx.Get(key) is not { } current || current.Status is WorkItemStatus.Completed or WorkItemStatus.Canceled) return false;
            WorkItemState job = tx.Edit(key);
            job.Status = WorkItemStatus.Canceled;
            foreach (WorkLeaseState lease in job.Leases.Where(l => l.Actual is null)) lease.CancelRequested = true;
            return true;
        });
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
        return Run(tx =>
        {
            long now = Now(); tx.Now = now; Expire(tx, now);
            if (Locate(tx.Get, identity, workerId) is not { } found) return EvolutionWorkCommitDisposition.UnknownLease;
            WorkLeaseState committed = found.Job.Leases[found.Index];
            if (committed.Actual is not null)
            {
                if (committed.Result != serializedResult || committed.Provenance != provenance || committed.Outcome != outcome
                    || !Equal(committed.Actual, EvolutionWorkValidation.Copy(actual)))
                    throw new InvalidOperationException("A conflicting receipt/result cannot replace a committed delivery.");
                return committed.Accepted ? EvolutionWorkCommitDisposition.Duplicate : EvolutionWorkCommitDisposition.DuplicateStale;
            }
            WorkItemState job = tx.Edit(found.Key);
            WorkLeaseState lease = job.Leases[found.Index];
            _ledger.GetPendingReservation(lease.LeaseId).Complete(actual, outcome);
            bool violation = actual.Amounts.Any(p => p.Value > Amount(job.Maximum, p.Key));
            lease.Actual = EvolutionWorkValidation.Copy(actual); lease.Result = serializedResult;
            lease.Provenance = provenance; lease.Outcome = outcome;
            lease.Accepted = !violation && job.Status == WorkItemStatus.Leased && !lease.Expired && !lease.CancelRequested
                && ReferenceEquals(job.Leases[job.Leases.Count - 1], lease);
            if (lease.Accepted) job.Status = WorkItemStatus.Completed;
            else if (violation && job.Status == WorkItemStatus.Leased && ReferenceEquals(job.Leases[job.Leases.Count - 1], lease))
                job.Status = WorkItemStatus.Canceled;
            return violation ? EvolutionWorkCommitDisposition.BudgetViolation
                : lease.Accepted ? EvolutionWorkCommitDisposition.Accepted : EvolutionWorkCommitDisposition.Stale;
        });
    }

    /// <summary>Gets the persisted logical result, or null until a current lease commits one. Reading does not consume it.</summary>
    public EvolutionCommittedWork? GetResult(long evaluationId, int attempt)
    {
        string key = Key(evaluationId, attempt);
        return Read(() =>
        {
            if (!_state.Work.TryGetValue(key, out WorkItemState? job)) return null;
            WorkLeaseState? lease = job.Leases.SingleOrDefault(l => l.Accepted);
            return lease is null ? null : Result(job, lease);
        });
    }

    /// <summary>Gets a delivery's persisted evidence even when stale, canceled or over budget.</summary>
    public EvolutionCommittedWork? GetDeliveryResult(EvolutionWorkIdentity identity, string workerId)
    {
        ValidateTicket(identity, workerId);
        return Read(() =>
        {
            if (Locate(Live, identity, workerId) is not { } found) return null;
            WorkLeaseState lease = found.Job.Leases[found.Index];
            return lease.Actual is not null ? Result(found.Job, lease) : null;
        });
    }

    /// <summary>Gets this incarnation's unresolved deliveries after reconnecting; this does not create new reservations.</summary>
    /// <remarks>For reconciling an already-started physical operation, not authorization to execute it a second time.
    /// Check heartbeat/cancellation before continuing. Worker-side durable operation identity is required to avoid duplicate
    /// physical execution; coordinator idempotency alone cannot provide that guarantee.</remarks>
    public IReadOnlyList<EvolutionWorkLease> GetUnsettledDeliveries(string workerId)
    {
        EvolutionWorkValidation.Id(workerId, nameof(workerId));
        return Read<IReadOnlyList<EvolutionWorkLease>>(() => Array.AsReadOnly(_state.Work.Values.SelectMany(job => job.Leases
            .Where(l => l.WorkerId == workerId && l.Actual is null).Select(l => Lease(job, l))).ToArray()));
    }

    /// <summary>Releases coordinator ownership, leaving every work item and reservation durable and unresolved as appropriate.</summary>
    /// <remarks>Records already appended are written before the store closes, so callers still waiting for them are acknowledged.</remarks>
    public void Dispose() { lock (_sync) _journal.Dispose(); }

    /// <summary>The full state a snapshot would store now, serialized; tests compare it across recovery.</summary>
    internal string CaptureDurableState() => Read(() => Serialize(Materialize(null, _state.LastUtcTicks)));

    /// <summary>Folds the log into a new snapshot now rather than when it is due.</summary>
    internal void CompactJournal()
    {
        lock (_sync) { _journal.EnsureUsable(); Compact(); }
    }

    /// <summary>Runs one mutation: change the live state under the lock, log it, then wait for durability outside it.</summary>
    /// <remarks>The ledger savepoint and the copy-on-write <see cref="Mutation"/> make a refused or failed mutation leave
    /// no trace. The durability wait covers every record appended before the lock was released, including other
    /// callers' records this mutation may have observed, and runs on the error path too, so a refusal is never reported
    /// on the strength of state that a crash could still erase.</remarks>
    private T Run<T>(Func<Mutation, T> body)
    {
        long observed = 0;
        try
        {
            lock (_sync)
            {
                try
                {
                    _journal.EnsureUsable();
                    // Compaction runs before the next mutation, never inside one: a failure here faults the store
                    // without any mutation having been applied or acknowledged.
                    if (_journal.CompactionDue) Compact();
                    EvolutionResourceLedger.Savepoint savepoint = _ledger.BeginSavepoint();
                    var tx = new Mutation(_state);
                    bool open = true;
                    try
                    {
                        T result = body(tx);
                        if (tx.Changed || savepoint.Admitted.Count != 0 || savepoint.Completed.Count != 0 || _ledger.DeniedCount != savepoint.Denied)
                            Publish(tx, savepoint);
                        else _ledger.EndSavepoint();
                        open = false;
                        return result;
                    }
                    finally
                    {
                        if (open) _ledger.RollbackSavepoint();
                    }
                }
                finally { observed = _journal.LastSequence; }
            }
        }
        finally { AwaitDurable(observed); }
    }

    /// <summary>Runs one read under the lock, then waits until the state it read is durable.</summary>
    private T Read<T>(Func<T> body)
    {
        long observed = 0;
        try
        {
            lock (_sync)
            {
                try { _journal.EnsureUsable(); return body(); }
                finally { observed = _journal.LastSequence; }
            }
        }
        finally { AwaitDurable(observed); }
    }

    private void AwaitDurable(long sequence)
    {
        if (t_durability is { } scope && scope.TryDefer(this, sequence)) return;
        _journal.WaitDurable(sequence);
    }

    private void Publish(Mutation tx, EvolutionResourceLedger.Savepoint savepoint)
    {
        long now = tx.Now ?? Now();
        var record = new EvolutionWorkRecord
        {
            LastUtcTicks = now,
            SourceSessionId = tx.SourceSessionId,
            Work = tx.Work.Count == 0 ? null : tx.Work,
            Workers = tx.Workers.Count == 0 ? null : tx.Workers,
            Denied = _ledger.DeniedCount,
            Operations = savepoint.Admitted.Concat(savepoint.Completed).Distinct(StringComparer.Ordinal)
                .Select(_ledger.CaptureOperation).ToArray(),
            Settled = savepoint.Completed.Count == 0 ? null : savepoint.Completed.ToArray(),
        };
        if (record.Operations.Length == 0) record.Operations = null;
        string json = JsonSerializer.Serialize(record, EvolutionWorkJsonContext.Default.EvolutionWorkRecord);
        long bound = _stateBytesBound + (long)StateGrowthPerRecordByte * Utf8.GetByteCount(json);
        if (bound > _options.MaximumStateBytes)
        {
            bound = Utf8.GetByteCount(Serialize(Materialize(tx, now)));
            if (bound > _options.MaximumStateBytes) throw new InvalidOperationException("Work state exceeds the configured byte limit.");
        }
        _journal.Append(json);
        // The record is now the mutation; nothing below can fail.
        _ledger.EndSavepoint();
        foreach (KeyValuePair<string, WorkItemState> pair in tx.Work) _state.Work[pair.Key] = pair.Value;
        foreach (KeyValuePair<string, WorkerState> pair in tx.Workers) _state.Workers[pair.Key] = pair.Value;
        if (tx.SourceSessionId is not null) _state.SourceSessionId = tx.SourceSessionId;
        _state.LastUtcTicks = now;
        _stateBytesBound = bound;
    }

    private void Compact()
    {
        string json = Serialize(Materialize(null, _state.LastUtcTicks));
        _journal.Commit(json);
        _stateBytesBound = Utf8.GetByteCount(json);
    }

    /// <summary>The whole state as a snapshot stores it, with an optional uncommitted mutation laid over it.</summary>
    private EvolutionWorkState Materialize(Mutation? tx, long lastUtcTicks)
    {
        var work = new Dictionary<string, WorkItemState>(_state.Work, StringComparer.Ordinal);
        var workers = new Dictionary<string, WorkerState>(_state.Workers, StringComparer.Ordinal);
        if (tx is not null)
        {
            foreach (KeyValuePair<string, WorkItemState> pair in tx.Work) work[pair.Key] = pair.Value;
            foreach (KeyValuePair<string, WorkerState> pair in tx.Workers) workers[pair.Key] = pair.Value;
        }
        return new EvolutionWorkState
        {
            SourceSessionId = tx?.SourceSessionId ?? _state.SourceSessionId,
            Schema = _state.Schema,
            ContractHash = _state.ContractHash,
            LastUtcTicks = lastUtcTicks,
            Ledger = _ledger.CaptureState(),
            Work = work,
            Workers = workers,
        };
    }

    /// <summary>Applies log records to a snapshot, producing the state the old whole-state rewrite would have stored.</summary>
    private static void Replay(EvolutionWorkState state, IReadOnlyList<string> records)
    {
        EvolutionResourceLedger.State ledger = JsonSerializer.Deserialize(state.Ledger, EvolutionWorkJsonContext.Default.ResourceLedgerState)
            ?? throw new InvalidDataException("Missing resource state.");
        if (ledger.Operations is null || ledger.ReceiptOrder is null || state.Work is null || state.Workers is null)
            throw new InvalidDataException("Durable work snapshot is incomplete.");
        var operations = new Dictionary<string, EvolutionResourceLedger.OperationState>(StringComparer.Ordinal);
        foreach (EvolutionResourceLedger.OperationState? operation in ledger.Operations)
        {
            if (operation is null) throw new InvalidDataException("Invalid operation state.");
            operations.Add(operation.Id, operation);
        }
        var receipts = new List<string>(ledger.ReceiptOrder);
        foreach (string json in records)
        {
            EvolutionWorkRecord record = JsonSerializer.Deserialize(json, EvolutionWorkJsonContext.Default.EvolutionWorkRecord)
                ?? throw new InvalidDataException("Missing work log record.");
            state.LastUtcTicks = record.LastUtcTicks;
            if (record.SourceSessionId is not null) state.SourceSessionId = record.SourceSessionId;
            foreach (KeyValuePair<string, WorkItemState> pair in record.Work ?? new())
                state.Work[pair.Key] = pair.Value ?? throw new InvalidDataException("Invalid logged work item.");
            foreach (KeyValuePair<string, WorkerState> pair in record.Workers ?? new())
                state.Workers[pair.Key] = pair.Value ?? throw new InvalidDataException("Invalid logged worker profile.");
            ledger.Denied = record.Denied;
            foreach (EvolutionResourceLedger.OperationState? operation in record.Operations ?? Array.Empty<EvolutionResourceLedger.OperationState>())
            {
                if (operation is null) throw new InvalidDataException("Invalid logged operation.");
                operations[operation.Id] = operation;
            }
            foreach (string? id in record.Settled ?? Array.Empty<string>())
                receipts.Add(id ?? throw new InvalidDataException("Invalid logged receipt."));
        }
        if (receipts.Count > RetainedReceipts) receipts.RemoveRange(0, receipts.Count - RetainedReceipts);
        ledger.Operations = operations.Values.OrderBy(operation => operation.Id, StringComparer.Ordinal).ToArray();
        ledger.ReceiptOrder = receipts.ToArray();
        state.Ledger = JsonSerializer.Serialize(ledger, EvolutionWorkJsonContext.Default.ResourceLedgerState);
    }

    private EvolutionResourceLedger NewLedger() => new(RunId, _limits, RetainedReceipts, _options.MaximumWorkItems * _options.MaximumDeliveriesPerWork);
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
    private void Expire(Mutation tx, long now)
    {
        foreach (KeyValuePair<string, WorkItemState> entry in tx.Jobs().ToArray())
        {
            if (entry.Value.Status != WorkItemStatus.Leased || entry.Value.Leases[entry.Value.Leases.Count - 1].ExpiresUtcTicks > now) continue;
            WorkItemState job = tx.Edit(entry.Key);
            job.Leases[job.Leases.Count - 1].Expired = true;
            job.Status = job.Leases.Count < _options.MaximumDeliveriesPerWork ? WorkItemStatus.Pending : WorkItemStatus.DeliveryLimitReached;
        }
    }
    private static IEnumerable<WorkItemState> Unsettled(Mutation tx, string workerId) => tx.Jobs().Select(pair => pair.Value)
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
    private WorkItemState? Live(string key) => _state.Work.TryGetValue(key, out WorkItemState? job) ? job : null;
    private (string Key, WorkItemState Job, int Index)? Locate(Func<string, WorkItemState?> get, EvolutionWorkIdentity identity, string workerId)
    {
        if (identity.RunId != RunId) return null;
        string key = Key(identity.EvaluationId, identity.Attempt);
        if (get(key) is not { } job) return null;
        int index = job.Leases.FindIndex(l => l.LeaseId == identity.LeaseId && l.WorkerId == workerId);
        return index < 0 ? null : (key, job, index);
    }
    private EvolutionWorkIdentity Identity(WorkItemState job, WorkLeaseState lease) => new(RunId, job.EvaluationId, job.Attempt, lease.LeaseId);
    private EvolutionWorkLease Lease(WorkItemState job, WorkLeaseState lease) => new(Identity(job, lease), lease.WorkerId,
        job.CanonicalGenomeId, job.Payload, job.Leases.IndexOf(lease) + 1, lease.ExpiresUtcTicks);
    private EvolutionCommittedWork Result(WorkItemState job, WorkLeaseState lease) => new(Identity(job, lease), lease.Result!, lease.Provenance!,
        new EvolutionResources(lease.Actual!), lease.Outcome!.Value, lease.Accepted);

    /// <summary>Copy-on-write view of the live state for one mutation. Reads see this mutation's own edits; the live
    /// state is replaced only after the mutation's record has been appended, so a refused mutation leaves no trace.</summary>
    private sealed class Mutation(EvolutionWorkState live)
    {
        internal Dictionary<string, WorkItemState> Work { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, WorkerState> Workers { get; } = new(StringComparer.Ordinal);
        internal string? SourceSessionId { get; private set; }
        internal long? Now { get; set; }
        internal bool Changed { get; private set; }
        internal int WorkCount => live.Work.Count + Work.Keys.Count(key => !live.Work.ContainsKey(key));
        internal int WorkerCount => live.Workers.Count + Workers.Keys.Count(key => !live.Workers.ContainsKey(key));

        internal WorkItemState? Get(string key) =>
            Work.TryGetValue(key, out WorkItemState? edited) ? edited : live.Work.TryGetValue(key, out WorkItemState? job) ? job : null;

        internal WorkItemState Edit(string key)
        {
            if (Work.TryGetValue(key, out WorkItemState? edited)) return edited;
            WorkItemState copy = Clone(live.Work[key]);
            Work.Add(key, copy); Changed = true;
            return copy;
        }

        internal void Add(string key, WorkItemState job) { Work.Add(key, job); Changed = true; }

        /// <summary>Every work item in the live state's order, with this mutation's edits and additions.</summary>
        internal IEnumerable<KeyValuePair<string, WorkItemState>> Jobs()
        {
            foreach (KeyValuePair<string, WorkItemState> pair in live.Work)
                yield return Work.TryGetValue(pair.Key, out WorkItemState? edited) ? new(pair.Key, edited) : pair;
            foreach (KeyValuePair<string, WorkItemState> pair in Work)
                if (!live.Work.ContainsKey(pair.Key)) yield return pair;
        }

        internal WorkerState? Worker(string id) =>
            Workers.TryGetValue(id, out WorkerState? edited) ? edited : live.Workers.TryGetValue(id, out WorkerState? worker) ? worker : null;

        internal void SetWorker(string id, WorkerState worker) { Workers[id] = worker; Changed = true; }

        internal void Bind(string instanceId) { SourceSessionId = instanceId; Changed = true; }

        private static WorkItemState Clone(WorkItemState job) => new()
        {
            SourceLeaseId = job.SourceLeaseId,
            SourceTellAccepted = job.SourceTellAccepted,
            EvaluationId = job.EvaluationId,
            Attempt = job.Attempt,
            CanonicalGenomeId = job.CanonicalGenomeId,
            Payload = job.Payload,
            Tags = job.Tags.ToArray(),
            MinimumResources = new Dictionary<string, decimal>(job.MinimumResources),
            Estimated = new Dictionary<string, decimal>(job.Estimated),
            Maximum = new Dictionary<string, decimal>(job.Maximum),
            Status = job.Status,
            Leases = job.Leases.Select(lease => new WorkLeaseState
            {
                LeaseId = lease.LeaseId,
                WorkerId = lease.WorkerId,
                ExpiresUtcTicks = lease.ExpiresUtcTicks,
                Expired = lease.Expired,
                CancelRequested = lease.CancelRequested,
                Accepted = lease.Accepted,
                Result = lease.Result,
                Provenance = lease.Provenance,
                Actual = lease.Actual is null ? null : new Dictionary<string, decimal>(lease.Actual),
                Outcome = lease.Outcome,
            }).ToList(),
        };
    }

    /// <summary>Lets a protocol endpoint release its own lock before waiting for durability, so requests from
    /// different connections can share one flush. The endpoint still replies only after <see cref="Wait"/>.</summary>
    internal sealed class DurabilityScope : IDisposable
    {
        private readonly DurabilityScope? _outer;
        private DurableEvolutionWorkCoordinator? _coordinator;
        private long _sequence;
        private bool _active = true;

        private DurabilityScope(DurabilityScope? outer) => _outer = outer;

        /// <summary>Starts deferring this thread's durability waits until <see cref="Wait"/>.</summary>
        internal static DurabilityScope Begin()
        {
            var scope = new DurabilityScope(t_durability);
            t_durability = scope;
            return scope;
        }

        internal bool TryDefer(DurableEvolutionWorkCoordinator coordinator, long sequence)
        {
            if (!_active || (_coordinator is not null && !ReferenceEquals(_coordinator, coordinator))) return false;
            _coordinator = coordinator;
            _sequence = Math.Max(_sequence, sequence);
            return true;
        }

        /// <summary>Stops deferring. Call <see cref="Wait"/> afterwards, outside the endpoint's lock.</summary>
        public void Dispose()
        {
            if (!_active) return;
            _active = false;
            t_durability = _outer;
        }

        /// <summary>Returns once everything the deferred calls observed is durable, or throws if it cannot be.</summary>
        internal void Wait() => _coordinator?._journal.WaitDurable(_sequence);
    }

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
