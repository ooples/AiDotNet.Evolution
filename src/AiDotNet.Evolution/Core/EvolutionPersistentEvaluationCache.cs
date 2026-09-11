using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>An eligibility decision with a reused result only when the explicit policy accepted the stored evidence.</summary>
public sealed class EvolutionEvaluationCacheLookup
{
    internal EvolutionEvaluationCacheLookup(EvolutionEvaluationReuseDecision decision, EvolutionEvaluationCacheRecord? record = null)
    {
        Decision = decision;
        if (decision != EvolutionEvaluationReuseDecision.Eligible) return;
        ReusedResult = record!.AsReused(); EvidenceSha256 = record.EvidenceSha256;
    }
    /// <summary>Gets why reuse was accepted or refused.</summary>
    public EvolutionEvaluationReuseDecision Decision { get; }
    /// <summary>Gets an explicitly reused result with zero new evaluator cost; store invocations are charged separately.</summary>
    public EvolutionTaskResult? ReusedResult { get; }
    /// <summary>Gets the accepted record's raw-evidence digest for separately metered consumer validation; storage does not verify the external artifact.</summary>
    public string? EvidenceSha256 { get; }
}

/// <summary>Outcome of a metered persistent-evidence write; a failed write never changes a fresh measurement into a hit.</summary>
public enum EvolutionEvaluationCacheWriteStatus
{
    /// <summary>The store retained the supplied evidence.</summary>
    Stored,
    /// <summary>The store declined publication, for example because of capacity or newer/equal evidence.</summary>
    NotStored,
    /// <summary>The resource ledger refused the call before dispatch.</summary>
    BudgetExhausted,
    /// <summary>The dispatched store call failed; no successful publication is claimed.</summary>
    StorageUnavailable
}

/// <summary>Budgeted persistent lookups/writes with explicit freshness and sample-reuse decisions.</summary>
/// <remarks>
/// Each dispatched store method consumes exactly one cache_store_invocations unit, including failure/cancellation;
/// this is a logical invocation count, not physical I/O, bytes, elapsed time, API dollars or evaluator cost_units.
/// Callers separately meter codec/validation/evaluation/model work and retain raw evidence. Stable operation IDs
/// and the ledger must resume at the same boundary as the engine. Never double-meter these store invocations.
/// Force-fresh skips this cache; it cannot override an outer engine's run-local memo. Disable that memo for tasks
/// whose freshness/force-fresh policy must be checked on every dispatched evaluation.
/// </remarks>
public sealed class EvolutionPersistentEvaluationCache
{
    /// <summary>The ledger resource used for exact logical store-method invocation counts.</summary>
    public const string StoreInvocationResource = "cache_store_invocations";
    private static readonly EvolutionResources OneInvocation = EvolutionResources.Of(StoreInvocationResource, 1);
    private readonly IEvolutionEvaluationStore _store;
    private readonly EvolutionResourceLedger _ledger;

    /// <summary>Creates a coordinator over caller-owned storage and a ledger declaring cache_store_invocations.</summary>
    public EvolutionPersistentEvaluationCache(IEvolutionEvaluationStore store, EvolutionEvaluationReusePolicy policy, EvolutionResourceLedger ledger)
    {
        Guard.NotNull(store); Guard.NotNull(policy); Guard.NotNull(ledger);
        if (!ledger.Limits.Amounts.ContainsKey(StoreInvocationResource)) throw new ArgumentException("The ledger must declare cache_store_invocations.", nameof(ledger));
        _store = store; Policy = policy; _ledger = ledger;
        VersionHash = EvolutionHash.Combine(new[] { "persistent-evaluation-cache-v1", policy.VersionHash });
    }
    /// <summary>Gets immutable eligibility semantics.</summary>
    public EvolutionEvaluationReusePolicy Policy { get; }
    /// <summary>Gets coordinator/policy semantics; existing store contents and wall-clock inputs are external state.</summary>
    public string VersionHash { get; }

    /// <summary>Reads only after admission; disabled/force-fresh decisions require no store call.</summary>
    public async ValueTask<EvolutionEvaluationCacheLookup> LookupAsync(EvolutionEvaluationCacheKey key, string operationId,
        DateTimeOffset now, bool forceFresh = false, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(key); EvolutionReuseEncoding.Label(operationId, nameof(operationId));
        cancellationToken.ThrowIfCancellationRequested();
        EvolutionEvaluationReuseDecision initial = Policy.Check(key, null, now, forceFresh);
        if (initial is EvolutionEvaluationReuseDecision.Disabled or EvolutionEvaluationReuseDecision.ForceFresh) return new(initial);
        using EvolutionResourceReservation? reservation = Reserve(key, operationId, "read");
        if (reservation is null) return new(EvolutionEvaluationReuseDecision.BudgetExhausted);
        EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Failed;
        try
        {
            EvolutionEvaluationCacheRecord? record = await _store.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            EvolutionEvaluationReuseDecision decision = Policy.Check(key, record, now);
            outcome = decision == EvolutionEvaluationReuseDecision.KeyMismatch ? EvolutionResourceOutcome.Failed : EvolutionResourceOutcome.Completed;
            return new(decision, record);
        }
        catch (OperationCanceledException) { outcome = EvolutionResourceOutcome.Canceled; throw; }
        catch (Exception exception) when (Recoverable(exception)) { return new(EvolutionEvaluationReuseDecision.StorageUnavailable); }
        finally { reservation.Complete(OneInvocation, outcome); }
    }

    /// <summary>Publishes already measured evidence after admission, retaining the write outcome independently of fitness.</summary>
    public async ValueTask<EvolutionEvaluationCacheWriteStatus> TryStoreAsync(EvolutionEvaluationCacheRecord record, string operationId,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(record); EvolutionReuseEncoding.Label(operationId, nameof(operationId)); cancellationToken.ThrowIfCancellationRequested();
        using EvolutionResourceReservation? reservation = Reserve(record.Key, operationId, "write");
        if (reservation is null) return EvolutionEvaluationCacheWriteStatus.BudgetExhausted;
        EvolutionResourceOutcome outcome = EvolutionResourceOutcome.Failed;
        try
        {
            bool stored = await _store.TryWriteAsync(record, cancellationToken).ConfigureAwait(false);
            outcome = EvolutionResourceOutcome.Completed;
            return stored ? EvolutionEvaluationCacheWriteStatus.Stored : EvolutionEvaluationCacheWriteStatus.NotStored;
        }
        catch (OperationCanceledException) { outcome = EvolutionResourceOutcome.Canceled; throw; }
        catch (Exception exception) when (Recoverable(exception)) { return EvolutionEvaluationCacheWriteStatus.StorageUnavailable; }
        finally { reservation.Complete(OneInvocation, outcome); }
    }

    private EvolutionResourceReservation? Reserve(EvolutionEvaluationCacheKey key, string operationId, string action) => _ledger.TryReserve(
        "persistent-cache/" + EvolutionHash.Combine(new[] { action, operationId, key.StableKey }), EvolutionResourceStage.Persistence,
        OneInvocation, OneInvocation);

    private static bool Recoverable(Exception exception) => exception is IOException or UnauthorizedAccessException or
        ArgumentException or InvalidOperationException or FormatException or OverflowException or JsonException;
}
