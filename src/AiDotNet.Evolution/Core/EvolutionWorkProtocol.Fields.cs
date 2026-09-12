using System.Globalization;
using System.Text.Json;

namespace AiDotNet.Evolution;

public sealed partial class EvolutionWorkProtocol
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private const string DecimalFormat = "0.############################";

    private static void Shape(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a JSON object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!names.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new ArgumentException("Duplicate or unsupported field: " + property.Name);
    }

    private static JsonElement Required(JsonElement value, string name) => value.TryGetProperty(name, out JsonElement field)
        ? field : throw new ArgumentException("Missing field: " + name);
    private static string Text(JsonElement value, string name) => Required(value, name).ValueKind == JsonValueKind.String
        ? Required(value, name).GetString()! : throw new ArgumentException("Expected a string: " + name);
    private static int Integer(JsonElement value, string name, int? fallback = null)
    {
        if (fallback.HasValue && !value.TryGetProperty(name, out _)) return fallback.Value;
        JsonElement field = Required(value, name);
        if (field.ValueKind != JsonValueKind.Number || !field.TryGetInt32(out int number))
            throw new ArgumentException("Expected an Int32: " + name);
        return number;
    }
    private static long EvaluationId(JsonElement value)
    {
        string text = Text(value, "evaluationId");
        if (!long.TryParse(text, NumberStyles.None, Invariant, out long number) || number < 0 || text != number.ToString(Invariant))
            throw new ArgumentException("evaluationId must be a canonical nonnegative Int64 decimal string.");
        return number;
    }
    private static EvolutionResources Amounts(JsonElement value, string name, bool optional = false)
    {
        if (optional && !value.TryGetProperty(name, out _)) return EvolutionResources.Empty;
        JsonElement map = Required(value, name);
        if (map.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a resource object: " + name);
        var values = new List<KeyValuePair<string, decimal>>();
        foreach (JsonProperty property in map.EnumerateObject())
        {
            if (values.Count == 32) throw new ArgumentException("At most 32 resource entries are supported.");
            string? text = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (text is null || !decimal.TryParse(text, NumberStyles.AllowDecimalPoint, Invariant, out decimal number)
                || number < 0 || text != number.ToString(DecimalFormat, Invariant))
                throw new ArgumentException("Resource amounts must be exact canonical nonnegative decimal strings.");
            values.Add(new KeyValuePair<string, decimal>(property.Name, number));
        }
        return new EvolutionResources(values);
    }
    private static string[] Tags(JsonElement value)
    {
        if (!value.TryGetProperty("tags", out JsonElement tags)) return Array.Empty<string>();
        if (tags.ValueKind != JsonValueKind.Array || tags.GetArrayLength() > 32) throw new ArgumentException("At most 32 tags are supported.");
        return tags.EnumerateArray().Select(tag => tag.ValueKind == JsonValueKind.String ? tag.GetString()!
            : throw new ArgumentException("Tags must be strings.")).ToArray();
    }
    private static EvolutionWorkIdentity Ticket(JsonElement value)
    {
        JsonElement ticket = Required(value, "identity"); Shape(ticket, "runId", "evaluationId", "attempt", "leaseId");
        return new EvolutionWorkIdentity(Text(ticket, "runId"), EvaluationId(ticket), Integer(ticket, "attempt"), Text(ticket, "leaseId"));
    }
    private static EvolutionWorkerProfile Worker(JsonElement request)
    {
        JsonElement worker = Required(request, "worker");
        Shape(worker, "workerId", "compatibilityHash", "tags", "capacity", "maximumConcurrentWork");
        return new EvolutionWorkerProfile(Text(worker, "workerId"), Text(worker, "compatibilityHash"), Tags(worker),
            Amounts(worker, "capacity", optional: true), Integer(worker, "maximumConcurrentWork", 1));
    }
    private static EvolutionResourceOutcome Outcome(JsonElement request) => Text(request, "outcome") switch
    {
        "completed" => EvolutionResourceOutcome.Completed,
        "failed" => EvolutionResourceOutcome.Failed,
        "rejected" => EvolutionResourceOutcome.Rejected,
        "canceled" => EvolutionResourceOutcome.Canceled,
        _ => throw new ArgumentException("outcome must be completed, failed, rejected or canceled; actual receipts are mandatory."),
    };
    private static string Outcome(EvolutionResourceOutcome value) => value switch
    {
        EvolutionResourceOutcome.Completed => "completed",
        EvolutionResourceOutcome.Failed => "failed",
        EvolutionResourceOutcome.Rejected => "rejected",
        EvolutionResourceOutcome.Canceled => "canceled",
        _ => throw new InvalidDataException("Unsupported physical receipt outcome."),
    };
    private static string Disposition(EvolutionWorkCommitDisposition value) => value switch
    {
        EvolutionWorkCommitDisposition.Accepted => "accepted",
        EvolutionWorkCommitDisposition.Duplicate => "duplicate",
        EvolutionWorkCommitDisposition.Stale => "stale",
        EvolutionWorkCommitDisposition.DuplicateStale => "duplicate-stale",
        EvolutionWorkCommitDisposition.UnknownLease => "unknown-lease",
        EvolutionWorkCommitDisposition.BudgetViolation => "budget-violation",
        _ => throw new InvalidDataException("Unsupported commit disposition."),
    };
    private static string Heartbeat(EvolutionWorkHeartbeat value) => value switch
    {
        EvolutionWorkHeartbeat.Renewed => "renewed",
        EvolutionWorkHeartbeat.Expired => "expired",
        EvolutionWorkHeartbeat.Canceled => "canceled",
        EvolutionWorkHeartbeat.Completed => "completed",
        EvolutionWorkHeartbeat.UnknownLease => "unknown-lease",
        _ => throw new InvalidDataException("Unsupported heartbeat status."),
    };

    private static void WriteAmounts(Utf8JsonWriter writer, string name, IReadOnlyDictionary<string, decimal> values)
    {
        writer.WriteStartObject(name);
        foreach (KeyValuePair<string, decimal> pair in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            writer.WriteString(pair.Key, pair.Value.ToString(DecimalFormat, Invariant));
        writer.WriteEndObject();
    }
    private static void WriteTicket(Utf8JsonWriter writer, EvolutionWorkIdentity identity)
    {
        writer.WriteStartObject("identity"); writer.WriteString("runId", identity.RunId);
        writer.WriteString("evaluationId", identity.EvaluationId.ToString(Invariant));
        writer.WriteNumber("attempt", identity.Attempt); writer.WriteString("leaseId", identity.LeaseId); writer.WriteEndObject();
    }
    private static void WriteLease(Utf8JsonWriter writer, EvolutionWorkLease? lease)
    {
        if (lease is null) { writer.WriteNull("lease"); return; }
        writer.WriteStartObject("lease"); WriteTicket(writer, lease.Identity); writer.WriteString("workerId", lease.WorkerId);
        writer.WriteString("canonicalGenomeId", lease.CanonicalGenomeId); writer.WriteString("payload", lease.Payload);
        writer.WriteNumber("deliveryNumber", lease.DeliveryNumber); writer.WriteString("expiresAt", lease.ExpiresAt.ToString("O", Invariant));
        writer.WriteEndObject();
    }
    private static void WriteResult(Utf8JsonWriter writer, EvolutionCommittedWork? result)
    {
        if (result is null) { writer.WriteNull("result"); return; }
        writer.WriteStartObject("result"); WriteTicket(writer, result.Identity); writer.WriteString("payload", result.Payload);
        writer.WriteString("provenance", result.Provenance); WriteAmounts(writer, "actual", result.ActualResources.Amounts);
        writer.WriteString("outcome", Outcome(result.Outcome)); writer.WriteBoolean("accepted", result.Accepted); writer.WriteEndObject();
    }
}
