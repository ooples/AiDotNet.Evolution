using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>A thread-safe, bounded, multi-resource reservation ledger with exactly-once reconciliation.</summary>
/// <remarks>
/// Reserve a producer's enforceable maximum before dispatch, then reconcile its actual receipt even on failure.
/// Disposing an unsettled reservation charges the maximum and marks the amount unknown; it never refunds unobserved work.
/// Producers must enforce their own token/process/device limits. The ledger cannot interrupt an external process or
/// reconstruct an unreported bill. A reported maximum violation is retained and stops further reservations.
/// The ledger is deliberately outside engine transaction rollback: cancelled search work still costs resources.
/// Concurrent admission follows reservation order; callers requiring deterministic budget cutoffs must reserve in
/// deterministic dispatch order. A snapshot is not a distributed lease service or automatic durable write-ahead log.
/// </remarks>
public sealed class EvolutionResourceLedger
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Operation> _operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _spent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, decimal> _reserved = new(StringComparer.Ordinal);
    private readonly Queue<EvolutionResourceReceipt> _receipts = new();
    private long _settled;
    private long _denied;
    private long _unknown;
    private bool _maximumViolated;

    /// <summary>Creates a run-scoped ledger. All metered resource names must have explicit limits, including zero limits.</summary>
    public EvolutionResourceLedger(string runId, EvolutionResources limits, int retainedReceiptLimit = 1024,
        int maximumOperations = 100_000)
    {
        ValidateId(runId, nameof(runId));
        Guard.NotNull(limits);
        if (limits.Amounts.Count == 0) throw new ArgumentException("At least one resource limit is required.", nameof(limits));
        if (retainedReceiptLimit < 0 || retainedReceiptLimit > 100_000) throw new ArgumentOutOfRangeException(nameof(retainedReceiptLimit));
        if (maximumOperations < 1 || maximumOperations > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maximumOperations));
        RunId = runId;
        Limits = limits;
        RetainedReceiptLimit = retainedReceiptLimit;
        MaximumOperations = maximumOperations;
        foreach (string key in limits.Amounts.Keys) { _spent.Add(key, 0); _reserved.Add(key, 0); }
        ConfigurationHash = EvolutionHash.Combine(new[] { "resource-ledger-v1", runId,
            retainedReceiptLimit.ToString(CultureInfo.InvariantCulture), maximumOperations.ToString(CultureInfo.InvariantCulture) }
            .Concat(limits.Amounts.Keys));
    }

    /// <summary>Gets the run or campaign identity.</summary>
    public string RunId { get; }
    /// <summary>Gets the explicit per-resource caps. These are not necessarily monetary amounts.</summary>
    public EvolutionResources Limits { get; }
    /// <summary>Gets the receipt-detail retention bound; totals and operation tombstones are independent of retention.</summary>
    public int RetainedReceiptLimit { get; }
    /// <summary>Gets the operation-identity bound. Admission stops at this bound rather than forgetting duplicate protection.</summary>
    public int MaximumOperations { get; }
    /// <summary>Gets the accounting-schema fingerprint, excluding caps so a fresh restored ledger may raise or lower them.</summary>
    public string ConfigurationHash { get; }

    /// <summary>Atomically reserves a maximum vector, returning null if any cap or the operation bound prevents admission.</summary>
    /// <remarks>The estimate must not exceed the maximum. Undeclared resources and duplicate operation IDs are errors.
    /// A denied operation consumes nothing and may be attempted again after other reservations reconcile.</remarks>
    public EvolutionResourceReservation? TryReserve(string operationId, EvolutionResourceStage stage,
        EvolutionResources estimated, EvolutionResources maximum, int attempt = 1)
    {
        ValidateId(operationId, nameof(operationId));
        ValidateAmounts(estimated);
        ValidateAmounts(maximum);
        if (!Enum.IsDefined(typeof(EvolutionResourceStage), stage)) throw new ArgumentOutOfRangeException(nameof(stage));
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt));
        if (estimated.Amounts.Any(pair => pair.Value > maximum[pair.Key]))
            throw new ArgumentException("The estimate exceeds the reserved maximum.", nameof(estimated));
        lock (_sync)
        {
            if (_operations.ContainsKey(operationId)) throw new InvalidOperationException("The operation identity was already reserved.");
            if (_maximumViolated || _operations.Count >= MaximumOperations ||
                Limits.Amounts.Any(pair => _spent[pair.Key] + _reserved[pair.Key] + maximum[pair.Key] > pair.Value))
            {
                if (_denied < long.MaxValue) _denied++;
                return null;
            }
            _operations.Add(operationId, new Operation(operationId, stage, attempt, estimated, maximum));
            foreach (string key in Limits.Amounts.Keys) _reserved[key] += maximum[key];
            return new EvolutionResourceReservation(this, operationId);
        }
    }

    /// <summary>Rebinds a pending reservation after restoring the ledger; settled or unknown identities are refused.</summary>
    public EvolutionResourceReservation GetPendingReservation(string operationId)
    {
        lock (_sync)
        {
            if (!_operations.TryGetValue(operationId, out Operation? operation) || operation.Receipt is not null)
                throw new InvalidOperationException("The operation is not pending.");
            return new EvolutionResourceReservation(this, operationId);
        }
    }

    /// <summary>Returns a detached accounting snapshot; complete counters survive receipt-detail truncation.</summary>
    public EvolutionResourceSnapshot Snapshot()
    {
        lock (_sync)
            return new EvolutionResourceSnapshot(_spent, _reserved, _receipts.ToArray(), _operations.Count,
                _settled, _denied, _unknown, _maximumViolated);
    }

    // Checkpoint validators need settled tombstones even when the diagnostic receipt queue was truncated.
    internal EvolutionResourceReceipt? FindReceipt(string operationId)
    {
        lock (_sync) return _operations.TryGetValue(operationId, out var operation) ? operation.Receipt : null;
    }

    internal bool Complete(string operationId, EvolutionResources actual, EvolutionResourceOutcome outcome, bool onlyIfPending = false)
    {
        ValidateAmounts(actual);
        if (!Enum.IsDefined(typeof(EvolutionResourceOutcome), outcome)) throw new ArgumentOutOfRangeException(nameof(outcome));
        lock (_sync)
        {
            Operation operation = _operations[operationId];
            if (operation.Receipt is { } prior)
            {
                if (onlyIfPending || (prior.Outcome == outcome && EqualAmounts(prior.Charged, actual))) return false;
                throw new InvalidOperationException("A conflicting receipt cannot replace a settled operation.");
            }
            EvolutionResources charged = outcome == EvolutionResourceOutcome.Unknown ? operation.Maximum : actual;
            if (outcome == EvolutionResourceOutcome.Unknown && !onlyIfPending && !EqualAmounts(actual, operation.Maximum))
                throw new ArgumentException("An unknown receipt must charge the reserved maximum.", nameof(actual));
            var receipt = new EvolutionResourceReceipt(operationId, operation.Stage, operation.Attempt,
                operation.Estimated, operation.Maximum, charged, outcome);
            foreach (string key in Limits.Amounts.Keys)
            {
                _reserved[key] -= operation.Maximum[key];
                _spent[key] += charged[key];
            }
            operation.Receipt = receipt;
            _settled++;
            if (outcome == EvolutionResourceOutcome.Unknown) _unknown++;
            _maximumViolated |= receipt.ExceededMaximum;
            if (RetainedReceiptLimit > 0)
            {
                if (_receipts.Count == RetainedReceiptLimit) _receipts.Dequeue();
                _receipts.Enqueue(receipt);
            }
            return true;
        }
    }

    internal void Abandon(string operationId) => Complete(operationId, EvolutionResources.Empty, EvolutionResourceOutcome.Unknown, true);

    /// <summary>Captures identities, pending reservations, receipt evidence and complete totals for explicit durable storage.</summary>
    public string CaptureState()
    {
        lock (_sync)
        {
            var state = new State
            {
                ConfigurationHash = ConfigurationHash,
                Denied = _denied,
                ReceiptOrder = _receipts.Select(receipt => receipt.OperationId).ToArray(),
                Operations = _operations.Values.OrderBy(operation => operation.Id, StringComparer.Ordinal).Select(operation => new OperationState
                {
                    Id = operation.Id,
                    Stage = operation.Stage,
                    Attempt = operation.Attempt,
                    Estimated = Copy(operation.Estimated.Amounts),
                    Maximum = Copy(operation.Maximum.Amounts),
                    Charged = operation.Receipt is { } receipt ? Copy(receipt.Charged.Amounts) : null,
                    Outcome = operation.Receipt?.Outcome
                }).ToArray()
            };
            string json = JsonSerializer.Serialize(state);
            if (json.Length > 64 * 1024 * 1024) throw new InvalidOperationException("Resource checkpoint exceeds 64 MiB.");
            return json;
        }
    }

    /// <summary>Transactionally restores into a fresh ledger with matching run/schema. Current caps remain authoritative.</summary>
    /// <remarks>Unsettled work remains reserved, not free. Recover its receipt or explicitly abandon it; do not redispatch it
    /// merely because the coordinator restarted. External exactly-once delivery requires a separate lease protocol.</remarks>
    public void RestoreState(string json)
    {
        Guard.NotNull(json);
        if (json.Length > 64 * 1024 * 1024) throw new ArgumentException("Resource checkpoint exceeds 64 MiB.", nameof(json));
        State state = JsonSerializer.Deserialize<State>(json) ?? throw new ArgumentException("Missing resource state.", nameof(json));
        if (state.ConfigurationHash != ConfigurationHash || state.Denied < 0 || state.Operations is null ||
            state.Operations.Length > MaximumOperations || state.ReceiptOrder is null || state.ReceiptOrder.Length > RetainedReceiptLimit)
            throw new ArgumentException("Incompatible or invalid resource state.", nameof(json));
        var restored = new EvolutionResourceLedger(RunId, Limits, RetainedReceiptLimit, MaximumOperations);
        foreach (OperationState item in state.Operations)
        {
            if (item is null || item.Estimated is null || item.Maximum is null) throw new ArgumentException("Invalid operation state.", nameof(json));
            ValidateId(item.Id, nameof(json));
            var estimate = new EvolutionResources(item.Estimated);
            var maximum = new EvolutionResources(item.Maximum);
            ValidateAmounts(estimate);
            ValidateAmounts(maximum);
            if (!Enum.IsDefined(typeof(EvolutionResourceStage), item.Stage) || item.Attempt < 1 ||
                estimate.Amounts.Any(pair => pair.Value > maximum[pair.Key]) || restored._operations.ContainsKey(item.Id) ||
                item.Outcome.HasValue != (item.Charged is not null))
                throw new ArgumentException("Invalid operation state.", nameof(json));
            var operation = new Operation(item.Id, item.Stage, item.Attempt, estimate, maximum);
            restored._operations.Add(item.Id, operation);
            foreach (string key in Limits.Amounts.Keys) restored._reserved[key] += maximum[key];
            if (item.Charged is not null) restored.Complete(item.Id, new EvolutionResources(item.Charged), item.Outcome!.Value);
        }
        restored._receipts.Clear();
        var retainedIds = new HashSet<string>(StringComparer.Ordinal);
        if (state.ReceiptOrder.Length != Math.Min(restored._settled, RetainedReceiptLimit))
            throw new ArgumentException("Invalid retained receipt count.", nameof(json));
        foreach (string id in state.ReceiptOrder)
        {
            if (id is null || !retainedIds.Add(id) || !restored._operations.TryGetValue(id, out Operation? operation) || operation.Receipt is null)
                throw new ArgumentException("Invalid retained receipt identity.", nameof(json));
            restored._receipts.Enqueue(operation.Receipt);
        }
        lock (_sync)
        {
            if (_operations.Count != 0 || _denied != 0) throw new InvalidOperationException("Restore requires a fresh ledger.");
            foreach (KeyValuePair<string, Operation> pair in restored._operations) _operations.Add(pair.Key, pair.Value);
            foreach (string key in Limits.Amounts.Keys) { _spent[key] = restored._spent[key]; _reserved[key] = restored._reserved[key]; }
            foreach (EvolutionResourceReceipt receipt in restored._receipts) _receipts.Enqueue(receipt);
            _settled = restored._settled;
            _unknown = restored._unknown;
            _maximumViolated = restored._maximumViolated;
            _denied = state.Denied;
        }
    }

    private void ValidateAmounts(EvolutionResources amounts)
    {
        Guard.NotNull(amounts);
        if (amounts.Amounts.Keys.Any(key => !Limits.Amounts.ContainsKey(key)))
            throw new ArgumentException("Every charged resource must have an explicit limit.", nameof(amounts));
    }

    private bool EqualAmounts(EvolutionResources first, EvolutionResources second) => Limits.Amounts.Keys.All(key => first[key] == second[key]);
    private static Dictionary<string, decimal> Copy(IReadOnlyDictionary<string, decimal> source) => source.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    private static void ValidateId(string id, string argument)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || id.Any(char.IsControl))
            throw new ArgumentException("Identity must be nonblank, printable and at most 256 characters.", argument);
    }

    private sealed class Operation(string id, EvolutionResourceStage stage, int attempt, EvolutionResources estimated, EvolutionResources maximum)
    {
        public string Id { get; } = id;
        public EvolutionResourceStage Stage { get; } = stage;
        public int Attempt { get; } = attempt;
        public EvolutionResources Estimated { get; } = estimated;
        public EvolutionResources Maximum { get; } = maximum;
        public EvolutionResourceReceipt? Receipt { get; set; }
    }

    private sealed class State
    {
        public string ConfigurationHash { get; set; } = string.Empty;
        public long Denied { get; set; }
        public OperationState[]? Operations { get; set; }
        public string[]? ReceiptOrder { get; set; }
    }

    private sealed class OperationState
    {
        public string Id { get; set; } = string.Empty;
        public EvolutionResourceStage Stage { get; set; }
        public int Attempt { get; set; }
        public Dictionary<string, decimal>? Estimated { get; set; }
        public Dictionary<string, decimal>? Maximum { get; set; }
        public Dictionary<string, decimal>? Charged { get; set; }
        public EvolutionResourceOutcome? Outcome { get; set; }
    }
}

/// <summary>A handle for reconciling one admitted operation exactly once.</summary>
public sealed class EvolutionResourceReservation : IDisposable
{
    private readonly EvolutionResourceLedger _ledger;
    internal EvolutionResourceReservation(EvolutionResourceLedger ledger, string operationId) { _ledger = ledger; OperationId = operationId; }
    /// <summary>Gets the stable operation identity.</summary>
    public string OperationId { get; }
    /// <summary>Settles actual consumption. An identical duplicate returns false; a conflicting receipt is rejected.</summary>
    public bool Complete(EvolutionResources actual, EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Completed) => _ledger.Complete(OperationId, actual, outcome);
    /// <summary>Charges an unsettled reservation's maximum as unknown consumption; already settled work is unchanged.</summary>
    public void Dispose() => _ledger.Abandon(OperationId);
}

/// <summary>A detached ledger snapshot whose totals include receipts omitted by retention.</summary>
public sealed class EvolutionResourceSnapshot
{
    internal EvolutionResourceSnapshot(IDictionary<string, decimal> spent, IDictionary<string, decimal> reserved,
        EvolutionResourceReceipt[] receipts, long admitted, long settled, long denied, long unknown, bool maximumViolated)
    {
        Spent = new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>(spent, StringComparer.Ordinal));
        Reserved = new ReadOnlyDictionary<string, decimal>(new Dictionary<string, decimal>(reserved, StringComparer.Ordinal));
        Receipts = Array.AsReadOnly(receipts);
        Admitted = admitted; Settled = settled; Denied = denied; Unknown = unknown; MaximumViolated = maximumViolated;
    }
    /// <summary>Gets actual charges plus explicitly unknown conservative charges.</summary>
    public IReadOnlyDictionary<string, decimal> Spent { get; }
    /// <summary>Gets in-flight upper bounds not yet charged.</summary>
    public IReadOnlyDictionary<string, decimal> Reserved { get; }
    /// <summary>Gets retained terminal receipt details, oldest first.</summary>
    public IReadOnlyList<EvolutionResourceReceipt> Receipts { get; }
    /// <summary>Gets all admitted operations, including those still pending.</summary>
    public long Admitted { get; }
    /// <summary>Gets all settled operations, independent of detail retention.</summary>
    public long Settled { get; }
    /// <summary>Gets reservation requests denied without spending.</summary>
    public long Denied { get; }
    /// <summary>Gets conservatively charged operations lacking actual receipts.</summary>
    public long Unknown { get; }
    /// <summary>Gets whether a producer violated its maximum and admission was stopped.</summary>
    public bool MaximumViolated { get; }
    /// <summary>Gets the number of settled receipts whose details are not in this snapshot.</summary>
    public long DroppedReceipts => Settled - Receipts.Count;
}
