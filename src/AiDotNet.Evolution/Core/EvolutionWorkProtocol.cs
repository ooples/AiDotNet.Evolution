using System.Text;
using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>A bounded, versioned JSON worker/control protocol over a trusted caller-provided transport.</summary>
/// <remarks>No networking, authentication, automatic physical retries, or engine-state restoration is implied.
/// Int64 work IDs and decimal resource amounts are strings. Null claims mean unavailable now, never search completion.
/// An I/O error or lost reply requires explicit reconciliation against the same store, not a new budget or blind redispatch.</remarks>
public sealed partial class EvolutionWorkProtocol : IDisposable
{
    /// <summary>The current wire contract. Every request must declare it; silent downgrade is refused.</summary>
    public const int Version = 1;
    /// <summary>Maximum UTF-8 bytes per request or reply, independently of the coordinator's stricter payload bounds.</summary>
    public const int MaximumFrameBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset>? _utcNow;
    private readonly bool _ownsCoordinator;
    private DurableEvolutionWorkCoordinator? _coordinator;
    private bool _closed;

    /// <summary>Creates a protocol endpoint that owns the coordinator opened by its first open command.</summary>
    public EvolutionWorkProtocol(Func<DateTimeOffset>? utcNow = null) { _utcNow = utcNow; _ownsCoordinator = true; }

    /// <summary>Creates an endpoint for an already attached live-session coordinator, without taking ownership.</summary>
    /// <remarks>The caller must keep the coordinator alive and handle bridge delivery/reconciliation. Open is then refused.</remarks>
    public EvolutionWorkProtocol(DurableEvolutionWorkCoordinator coordinator)
    { Guard.NotNull(coordinator); _coordinator = coordinator; }

    /// <summary>Gets whether close/disposal ended this endpoint. Closing does not cancel physical work or refund reservations.</summary>
    public bool IsClosed { get { lock (_sync) return _closed; } }

    /// <summary>Processes one JSON object and returns one correlated response. The transport supplies framing and access control.</summary>
    /// <remarks>Commands are serialized. Failed commands return ok:false; publication or transport failure never authorizes retrying physical work.
    /// After an uncertain storage failure, dispose the endpoint and reopen the original store. No old-state fallback is attempted.</remarks>
    public string ProcessJson(string requestJson)
    {
        lock (_sync)
        {
            long id = 0;
            try
            {
                if (_closed) throw new ObjectDisposedException(nameof(EvolutionWorkProtocol));
                EvolutionWorkValidation.Payload(requestJson, MaximumFrameBytes, nameof(requestJson));
                using JsonDocument document = JsonDocument.Parse(requestJson);
                JsonElement request = document.RootElement;
                if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("id", out JsonElement rawId)
                    && rawId.ValueKind == JsonValueKind.Number && rawId.TryGetInt64(out long candidate) && candidate >= 1 && candidate <= 9007199254740991)
                    id = candidate;
                if (id == 0) throw new ArgumentException("id must be a positive JavaScript-safe integer.");
                if (Integer(request, "protocol") != Version) throw new ArgumentException("Unsupported durable worker protocol version.");
                string op = Text(request, "op");
                return Reply(id, writer => Dispatch(request, op, writer));
            }
            catch (Exception ex) when (ex is ArgumentException or JsonException or IOException or InvalidOperationException or OverflowException)
            {
                return Reply(id, writer =>
                {
                    writer.WriteString("error", ex.GetType().Name + ": " + ex.Message);
                    writer.WriteString("recovery", "reconcile-original-store; never assume an unacknowledged mutation failed");
                }, ok: false);
            }
        }
    }

    private static string Reply(long id, Action<Utf8JsonWriter> body, bool ok = true)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("id", id); writer.WriteNumber("protocol", Version); writer.WriteBoolean("ok", ok);
            body(writer); writer.WriteEndObject(); writer.Flush();
        }
        if (stream.Length > MaximumFrameBytes) throw new InvalidOperationException("Worker response exceeds its byte limit; reconcile the original store.");
        return Utf8.GetString(stream.ToArray());
    }

    private void Dispatch(JsonElement request, string op, Utf8JsonWriter writer)
    {
        if (op == "open")
        {
            Shape(request, "id", "protocol", "op", "config");
            if (_coordinator is not null) throw new InvalidOperationException("A coordinator is already open.");
            JsonElement config = Required(request, "config");
            Shape(config, "directory", "runId", "compatibilityHash", "limits", "maximumWorkItems", "maximumDeliveriesPerWork",
                "maximumStateBytes", "maximumPayloadBytes", "maximumWorkers", "leaseDurationMs");
            var options = new EvolutionWorkCoordinatorOptions(Integer(config, "maximumWorkItems", 1024), Integer(config, "maximumDeliveriesPerWork", 3),
                Integer(config, "maximumStateBytes", 16 * 1024 * 1024), Integer(config, "maximumPayloadBytes", 64 * 1024),
                Integer(config, "maximumWorkers", 256), TimeSpan.FromMilliseconds(Integer(config, "leaseDurationMs", 300000)));
            _coordinator = new DurableEvolutionWorkCoordinator(Text(config, "directory"), Text(config, "runId"), Text(config, "compatibilityHash"),
                Amounts(config, "limits"), options, _utcNow);
            WriteStatus(writer); return;
        }
        if (op == "close") { Shape(request, "id", "protocol", "op"); Dispose(); writer.WriteBoolean("closed", true); return; }
        DurableEvolutionWorkCoordinator coordinator = _coordinator ?? throw new InvalidOperationException("No coordinator is open.");
        switch (op)
        {
            case "enqueue":
                Shape(request, "id", "protocol", "op", "job"); Enqueue(coordinator, Required(request, "job"), writer); break;
            case "claim":
                Shape(request, "id", "protocol", "op", "worker");
                EvolutionWorkLease? lease = coordinator.Claim(Worker(request));
                writer.WriteBoolean("available", lease is not null); WriteLease(writer, lease); break;
            case "heartbeat":
                Shape(request, "id", "protocol", "op", "identity", "workerId");
                writer.WriteString("status", Heartbeat(coordinator.Heartbeat(Ticket(request), Text(request, "workerId")))); break;
            case "cancel":
                Shape(request, "id", "protocol", "op", "evaluationId", "attempt");
                writer.WriteBoolean("canceled", coordinator.Cancel(EvaluationId(request), Integer(request, "attempt"))); break;
            case "commit":
                Shape(request, "id", "protocol", "op", "identity", "workerId", "payload", "provenance", "actual", "outcome");
                writer.WriteString("disposition", Disposition(coordinator.Commit(Ticket(request), Text(request, "workerId"), Text(request, "payload"),
                    Text(request, "provenance"), Amounts(request, "actual"), Outcome(request)))); break;
            case "result":
                Shape(request, "id", "protocol", "op", "evaluationId", "attempt");
                WriteResult(writer, coordinator.GetResult(EvaluationId(request), Integer(request, "attempt"))); break;
            case "delivery":
                Shape(request, "id", "protocol", "op", "identity", "workerId");
                WriteResult(writer, coordinator.GetDeliveryResult(Ticket(request), Text(request, "workerId"))); break;
            case "unsettled":
                Shape(request, "id", "protocol", "op", "workerId", "afterLeaseId"); Unsettled(coordinator, request, writer); break;
            case "status": Shape(request, "id", "protocol", "op"); WriteStatus(writer); break;
            default: throw new ArgumentException("Unknown durable worker operation: " + op);
        }
    }

    private static void Enqueue(DurableEvolutionWorkCoordinator coordinator, JsonElement job, Utf8JsonWriter writer)
    {
        Shape(job, "evaluationId", "attempt", "canonicalGenomeId", "payload", "tags", "minimumResources", "estimated", "maximum");
        writer.WriteBoolean("enqueued", coordinator.Enqueue(EvaluationId(job), Integer(job, "attempt"), Text(job, "canonicalGenomeId"),
            Text(job, "payload"), new EvolutionWorkRequirements(Tags(job), Amounts(job, "minimumResources", optional: true)),
            Amounts(job, "estimated"), Amounts(job, "maximum")));
    }
    private static void Unsettled(DurableEvolutionWorkCoordinator coordinator, JsonElement request, Utf8JsonWriter writer)
    {
        string? after = request.TryGetProperty("afterLeaseId", out _) ? Text(request, "afterLeaseId") : null;
        if (after is not null) _ = new EvolutionWorkIdentity(coordinator.RunId, 0, 1, after);
        // A stable keyset cursor, not an offset into a shrinking list. It is a reconciliation view,
        // not a consistent snapshot or authorization to execute; restart the scan for new claims.
        EvolutionWorkLease? lease = coordinator.GetUnsettledDeliveries(Text(request, "workerId"))
            .OrderBy(item => item.Identity.LeaseId, StringComparer.Ordinal)
            .FirstOrDefault(item => after is null || string.CompareOrdinal(item.Identity.LeaseId, after) > 0);
        WriteLease(writer, lease); writer.WriteBoolean("reconciliationOnly", true);
        if (lease is null) writer.WriteNull("nextAfterLeaseId"); else writer.WriteString("nextAfterLeaseId", lease.Identity.LeaseId);
    }
    private void WriteStatus(Utf8JsonWriter writer)
    {
        DurableEvolutionWorkCoordinator coordinator = _coordinator!;
        EvolutionResourceSnapshot resources = coordinator.Resources;
        writer.WriteString("runId", coordinator.RunId); writer.WriteString("compatibilityHash", coordinator.CompatibilityHash);
        writer.WriteBoolean("wasRecovered", coordinator.WasRecovered); writer.WriteString("sourceSessionId", coordinator.SourceSessionId);
        writer.WriteBoolean("supportsExactSearchContinuation", false); writer.WriteString("searchContinuationGuarantee", coordinator.SearchContinuationGuarantee);
        writer.WriteString("searchState", "not-owned");
        WriteAmounts(writer, "spent", resources.Spent); WriteAmounts(writer, "reserved", resources.Reserved);
        writer.WriteString("admitted", resources.Admitted.ToString(Invariant)); writer.WriteString("settled", resources.Settled.ToString(Invariant));
        writer.WriteString("denied", resources.Denied.ToString(Invariant)); writer.WriteBoolean("maximumViolated", resources.MaximumViolated);
    }

    /// <summary>Closes this endpoint. Borrowed coordinators remain alive; owned ones release their store lock.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            if (_ownsCoordinator)
            {
                _coordinator?.Dispose();
            }

            _coordinator = null;
        }
    }
}
