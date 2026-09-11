using System.Text;

namespace AiDotNet.Evolution;

/// <summary>Immutable bounds for one local-filesystem durable delivery coordinator.</summary>
public sealed class EvolutionWorkCoordinatorOptions
{
    /// <summary>Creates bounded retention, delivery, payload and lease policies. Full state is retained; no duplicate-protection eviction.</summary>
    public EvolutionWorkCoordinatorOptions(int maximumWorkItems = 1024, int maximumDeliveriesPerWork = 3,
        int maximumStateBytes = 16 * 1024 * 1024, int maximumPayloadBytes = 64 * 1024,
        int maximumWorkers = 256, TimeSpan? leaseDuration = null)
    {
        if (maximumWorkItems < 1 || maximumWorkItems > 10_000) throw new ArgumentOutOfRangeException(nameof(maximumWorkItems));
        if (maximumDeliveriesPerWork < 1 || maximumDeliveriesPerWork > 16) throw new ArgumentOutOfRangeException(nameof(maximumDeliveriesPerWork));
        if (maximumStateBytes < 4096 || maximumStateBytes > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(maximumStateBytes));
        if (maximumPayloadBytes < 1 || maximumPayloadBytes > 1024 * 1024 || maximumPayloadBytes > maximumStateBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPayloadBytes));
        if (maximumWorkers < 1 || maximumWorkers > 4096) throw new ArgumentOutOfRangeException(nameof(maximumWorkers));
        TimeSpan duration = leaseDuration ?? TimeSpan.FromMinutes(5);
        if (duration < TimeSpan.FromMilliseconds(1) || duration > TimeSpan.FromDays(1)) throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        MaximumWorkItems = maximumWorkItems; MaximumDeliveriesPerWork = maximumDeliveriesPerWork;
        MaximumStateBytes = maximumStateBytes; MaximumPayloadBytes = maximumPayloadBytes;
        MaximumWorkers = maximumWorkers; LeaseDuration = duration;
    }

    /// <summary>Maximum retained logical evaluations; includes completed and canceled tombstones.</summary>
    public int MaximumWorkItems { get; }
    /// <summary>Maximum physical deliveries of each engine attempt, each with its own reservation and lease token.</summary>
    public int MaximumDeliveriesPerWork { get; }
    /// <summary>Maximum UTF-8 bytes in the serialized atomic state, excluding its fixed checksum header.</summary>
    public int MaximumStateBytes { get; }
    /// <summary>Maximum UTF-8 bytes in each genome, result or provenance payload.</summary>
    public int MaximumPayloadBytes { get; }
    /// <summary>Maximum retained worker-incarnation profiles.</summary>
    public int MaximumWorkers { get; }
    /// <summary>Duration renewed by a heartbeat before expiry, never after it.</summary>
    public TimeSpan LeaseDuration { get; }
}

/// <summary>Declared hardware/software tags and per-job capacity requirements, distinct from billed resources.</summary>
public sealed class EvolutionWorkRequirements
{
    /// <summary>Creates immutable, bounded requirements. All tags and all resource minima must match.</summary>
    public EvolutionWorkRequirements(IEnumerable<string>? tags = null, EvolutionResources? minimumResources = null)
    {
        Tags = EvolutionWorkValidation.Tags(tags ?? Array.Empty<string>());
        MinimumResources = minimumResources ?? EvolutionResources.Empty;
    }
    /// <summary>Gets the required ordinal, case-sensitive tags.</summary>
    public IReadOnlyList<string> Tags { get; }
    /// <summary>Gets hardware capacity requirements, for example cpu_slots, gpu_count and memory_bytes.</summary>
    public EvolutionResources MinimumResources { get; }
}

/// <summary>A trusted worker incarnation's declared compatibility and capacity; declarations are not attestation.</summary>
public sealed class EvolutionWorkerProfile
{
    /// <summary>Creates a worker profile. Restarted processes should use a new incarnation ID unless reconciling the old work.</summary>
    public EvolutionWorkerProfile(string workerId, string compatibilityHash, IEnumerable<string>? tags = null,
        EvolutionResources? capacity = null, int maximumConcurrentWork = 1)
    {
        EvolutionWorkValidation.Id(workerId, nameof(workerId)); EvolutionWorkValidation.Id(compatibilityHash, nameof(compatibilityHash));
        if (maximumConcurrentWork < 1 || maximumConcurrentWork > 256) throw new ArgumentOutOfRangeException(nameof(maximumConcurrentWork));
        WorkerId = workerId; CompatibilityHash = compatibilityHash;
        Tags = EvolutionWorkValidation.Tags(tags ?? Array.Empty<string>());
        Capacity = capacity ?? EvolutionResources.Empty; MaximumConcurrentWork = maximumConcurrentWork;
    }
    /// <summary>Gets the worker/process incarnation identity.</summary>
    public string WorkerId { get; }
    /// <summary>Gets the exact evaluator/task/codec contract this worker implements.</summary>
    public string CompatibilityHash { get; }
    /// <summary>Gets ordinal, case-sensitive hardware/software tags.</summary>
    public IReadOnlyList<string> Tags { get; }
    /// <summary>Gets this incarnation's total capacity, shared by all its unsettled deliveries.</summary>
    public EvolutionResources Capacity { get; }
    /// <summary>Gets the simultaneous-work bound, including expired deliveries without final receipts.</summary>
    public int MaximumConcurrentWork { get; }
}

/// <summary>Work published durably before being returned to a worker.</summary>
public sealed class EvolutionWorkLease
{
    internal EvolutionWorkLease(EvolutionWorkIdentity identity, string workerId, string canonicalGenomeId,
        string payload, int deliveryNumber, long expiresUtcTicks)
    {
        Identity = identity; WorkerId = workerId; CanonicalGenomeId = canonicalGenomeId; Payload = payload;
        DeliveryNumber = deliveryNumber; ExpiresAt = new DateTimeOffset(expiresUtcTicks, TimeSpan.Zero);
    }
    /// <summary>Gets the full result/heartbeat correlation ticket; never a proof of authorization.</summary>
    public EvolutionWorkIdentity Identity { get; }
    /// <summary>Gets the assigned worker incarnation.</summary>
    public string WorkerId { get; }
    /// <summary>Gets the caller's canonical genome identity, not display text.</summary>
    public string CanonicalGenomeId { get; }
    /// <summary>Gets the bounded serialized genome. Its codec is pinned by the coordinator compatibility contract.</summary>
    public string Payload { get; }
    /// <summary>Gets the physical delivery number within this engine attempt.</summary>
    public int DeliveryNumber { get; }
    /// <summary>Gets the UTC lease deadline at publication/heartbeat.</summary>
    public DateTimeOffset ExpiresAt { get; }
}

/// <summary>The durable outcome of a result submission, independent of physical-work cancellation.</summary>
public enum EvolutionWorkCommitDisposition
{
    /// <summary>The current lease supplied the logical result.</summary>
    Accepted,
    /// <summary>This exact accepted result and receipt were already committed.</summary>
    Duplicate,
    /// <summary>The old/canceled/expired lease's receipt was settled but its result cannot update the search.</summary>
    Stale,
    /// <summary>This exact stale result and receipt were already settled.</summary>
    DuplicateStale,
    /// <summary>No matching published lease belongs to this worker/run/attempt.</summary>
    UnknownLease,
    /// <summary>The receipt exceeded its reserved maximum; accounting retained it and stopped further admission.</summary>
    BudgetViolation,
}

/// <summary>The current lease's response to heartbeat or cancellation polling.</summary>
public enum EvolutionWorkHeartbeat
{
    /// <summary>The live lease was renewed.</summary>
    Renewed,
    /// <summary>The lease expired and cannot be renewed.</summary>
    Expired,
    /// <summary>Cancellation was requested. A final resource receipt is still required.</summary>
    Canceled,
    /// <summary>A final receipt/result was already committed.</summary>
    Completed,
    /// <summary>The full worker/run/attempt/token identity did not match.</summary>
    UnknownLease,
}

/// <summary>A detached durable result and its physical-cost/provenance evidence.</summary>
public sealed class EvolutionCommittedWork
{
    internal EvolutionCommittedWork(EvolutionWorkIdentity identity, string payload, string provenance,
        EvolutionResources actual, EvolutionResourceOutcome outcome, bool accepted)
    { Identity = identity; Payload = payload; Provenance = provenance; ActualResources = actual; Outcome = outcome; Accepted = accepted; }
    /// <summary>Gets the original delivery ticket.</summary>
    public EvolutionWorkIdentity Identity { get; }
    /// <summary>Gets the serialized evaluator result, to be decoded and independently validated by the caller.</summary>
    public string Payload { get; }
    /// <summary>Gets caller-provided external-response provenance under the pinned evaluator contract.</summary>
    public string Provenance { get; }
    /// <summary>Gets the settled physical resource amounts.</summary>
    public EvolutionResources ActualResources { get; }
    /// <summary>Gets the physical-work accounting outcome, not a correctness or archive-admission verdict.</summary>
    public EvolutionResourceOutcome Outcome { get; }
    /// <summary>Gets whether this was the current lease's logical result. The engine must still validate it.</summary>
    public bool Accepted { get; }
}

internal static class EvolutionWorkValidation
{
    internal static void Id(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("Work identifiers must be nonblank, printable and at most 256 characters.", name);
    }
    internal static IReadOnlyList<string> Tags(IEnumerable<string> tags)
    {
        var copy = new SortedSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (string tag in tags)
        {
            if (++count > 32) throw new ArgumentException("At most 32 capability tags are supported.", nameof(tags));
            Id(tag, nameof(tags));
            if (tag.Length > 64 || !copy.Add(tag)) throw new ArgumentException("Capability tags must be unique and at most 64 characters.", nameof(tags));
        }
        return Array.AsReadOnly(copy.ToArray());
    }
    internal static void Payload(string value, int maximumBytes, string name)
    {
        if (value is null || new UTF8Encoding(false, true).GetByteCount(value) > maximumBytes)
            throw new ArgumentException("Work payload exceeds its UTF-8 byte limit or is null.", name);
    }
    internal static Dictionary<string, decimal> Copy(EvolutionResources values) => values.Amounts.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
}

internal sealed class EvolutionWorkState
{
    public int Schema { get; set; } = 1;
    public string ContractHash { get; set; } = string.Empty;
    public long LastUtcTicks { get; set; }
    public string Ledger { get; set; } = string.Empty;
    public Dictionary<string, WorkItemState> Work { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, WorkerState> Workers { get; set; } = new(StringComparer.Ordinal);
}

internal enum WorkItemStatus { Pending, Leased, Completed, Canceled, DeliveryLimitReached }
internal sealed class WorkItemState
{
    public long EvaluationId { get; set; }
    public int Attempt { get; set; }
    public string CanonicalGenomeId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public Dictionary<string, decimal> MinimumResources { get; set; } = new();
    public Dictionary<string, decimal> Estimated { get; set; } = new();
    public Dictionary<string, decimal> Maximum { get; set; } = new();
    public WorkItemStatus Status { get; set; }
    public List<WorkLeaseState> Leases { get; set; } = new();
}
internal sealed class WorkLeaseState
{
    public string LeaseId { get; set; } = string.Empty;
    public string WorkerId { get; set; } = string.Empty;
    public long ExpiresUtcTicks { get; set; }
    public bool Expired { get; set; }
    public bool CancelRequested { get; set; }
    public bool Accepted { get; set; }
    public string? Result { get; set; }
    public string? Provenance { get; set; }
    public Dictionary<string, decimal>? Actual { get; set; }
    public EvolutionResourceOutcome? Outcome { get; set; }
}
internal sealed class WorkerState
{
    public string CompatibilityHash { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
    public Dictionary<string, decimal> Capacity { get; set; } = new();
    public int MaximumConcurrentWork { get; set; }
}
