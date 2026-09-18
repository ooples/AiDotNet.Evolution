using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

/// <summary>A bounded, checksummed settled-batch checkpoint containing original evidence and opaque continuation tokens.</summary>
/// <remarks>
/// Persist only in a trusted store: the checksum detects corruption, not malicious forgery. This supports coordinated
/// pause/restart, not recovery of unjournaled in-flight work. Restore the matching resource state and reconcile any
/// subsequently dispatched work before resuming. Ordinary property serialization never exposes token or ledger payloads.
/// </remarks>
public sealed class EvolutionFidelityCheckpoint
{
    internal const int MaximumCharacters = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new() { MaxDepth = 32, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private readonly string _payload;
    private readonly string _json;
    private readonly string _resourceState;
    internal EvolutionFidelityCheckpoint(EvolutionFidelityCheckpointData data)
        : this(JsonSerializer.Serialize(data, Options), data) { }
    private EvolutionFidelityCheckpoint(string payload, EvolutionFidelityCheckpointData data)
    {
        if (payload.Length > MaximumCharacters || data.Batches is null || data.Batches.Length is < 1 or > 576 ||
            string.IsNullOrWhiteSpace(data.RunIdentity) || string.IsNullOrWhiteSpace(data.SchedulerVersionHash) || string.IsNullOrWhiteSpace(data.ResourceState))
            throw new ArgumentException("Invalid or oversized fidelity checkpoint.");
        _payload = payload; _resourceState = data.ResourceState; RunIdentity = data.RunIdentity; SchedulerVersionHash = data.SchedulerVersionHash;
        SettledBatchCount = data.Batches.Length; Checksum = EvolutionHash.Combine(new[] { "fidelity-checkpoint-v1", payload });
        _json = JsonSerializer.Serialize(new { SchemaVersion = 1, Payload = payload, Checksum }, Options);
        if (_json.Length > MaximumCharacters) throw new ArgumentException("Fidelity checkpoint exceeds 16 Mi UTF-16 characters.");
    }
    /// <summary>Gets the original run/cohort/seed identity.</summary>
    public string RunIdentity { get; }
    /// <summary>Gets the scheduler, plan and callback semantic identity required for resumption.</summary>
    public string SchedulerVersionHash { get; }
    /// <summary>Gets the number of fully settled batches, including rejected/unknown-cost batches.</summary>
    public int SettledBatchCount { get; }
    /// <summary>Gets the corruption-detection checksum, not authentication of the stored measurements.</summary>
    public string Checksum { get; }
    /// <summary>Explicitly exports sensitive state for caller-owned trusted durable storage.</summary>
    public string ToJson() => _json;
    /// <summary>Gets the exact ledger state to restore into a compatible fresh ledger before coordinated resumption.</summary>
    public string GetResourceState() => _resourceState;
    internal EvolutionFidelityCheckpointData ReadData() => JsonSerializer.Deserialize<EvolutionFidelityCheckpointData>(_payload, Options)!;

    /// <summary>Validates a bounded checkpoint envelope; run, sample and receipt compatibility are checked again on resume.</summary>
    public static EvolutionFidelityCheckpoint Parse(string json)
    {
        Guard.NotNull(json);
        if (json.Length > MaximumCharacters) throw new ArgumentException("Fidelity checkpoint exceeds its bound.", nameof(json));
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        UniqueProperties(root);
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3 || root.GetProperty("SchemaVersion").GetInt32() != 1)
            throw new ArgumentException("Unsupported fidelity checkpoint envelope.", nameof(json));
        string payload = root.GetProperty("Payload").GetString() ?? throw new ArgumentException("Missing checkpoint payload.", nameof(json));
        string checksum = root.GetProperty("Checksum").GetString() ?? string.Empty;
        if (payload.Length > MaximumCharacters || checksum != EvolutionHash.Combine(new[] { "fidelity-checkpoint-v1", payload }))
            throw new ArgumentException("Fidelity checkpoint checksum differs.", nameof(json));
        using var content = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        UniqueProperties(content.RootElement);
        var data = JsonSerializer.Deserialize<EvolutionFidelityCheckpointData>(payload, Options) ?? throw new ArgumentException("Missing fidelity state.", nameof(json));
        return new(payload, data);
    }
    private static void UniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new ArgumentException("Duplicate fidelity checkpoint property.");
                UniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) UniqueProperties(item);
    }
}

internal sealed class EvolutionFidelityCheckpointData
{
    public string RunIdentity { get; set; } = string.Empty;
    public string SchedulerVersionHash { get; set; } = string.Empty;
    public string ResourceConfigurationHash { get; set; } = string.Empty;
    public string ResourceLimitsHash { get; set; } = string.Empty;
    public string ResourceState { get; set; } = string.Empty;
    public EvolutionFidelityBatchData[]? Batches { get; set; }
    public Dictionary<string, EvolutionFidelityTokenData?[]>? States { get; set; }
}

internal sealed class EvolutionFidelityBatchData
{
    public string GenomeId { get; set; } = string.Empty;
    public int Rung { get; set; } = -1;
    public bool? Confirmation { get; set; }
    public string BatchIdentity { get; set; } = string.Empty;
    public EvolutionReplicationStopReason? StopReason { get; set; }
    public EvolutionFidelitySampleData[]? Samples { get; set; }
    public string?[]? PriorSamples { get; set; }
    public int AcceptedTokens { get; set; } = -1;
    public int RejectedTokens { get; set; } = -1;
}

internal sealed class EvolutionFidelitySampleData
{
    public EvolutionEvaluationStatus? Status { get; set; }
    public double? Quality { get; set; }
    public decimal? Charged { get; set; }
    public bool? Unknown { get; set; }
    public double? Reported { get; set; }
    public string? OriginJson { get; set; }
}

internal sealed class EvolutionFidelityTokenData
{
    public int Rung { get; set; } = -1;
    public string SourceSampleIdentity { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string PayloadBase64 { get; set; } = string.Empty;
}
