using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Distinguishes an original measurement from copies of its existing evidence.</summary>
public enum EvolutionMeasurementOriginKind
{
    /// <summary>The producer obtained new measurements for this result.</summary>
    Measured = 0,
    /// <summary>An external persistent store supplied an existing measurement.</summary>
    PersistentReuse = 1,
    /// <summary>The engine's run-local memo supplied an existing measurement.</summary>
    RunLocalReuse = 2,
    /// <summary>Island migration copied already measured evidence.</summary>
    MigrationCopy = 3
}

/// <summary>Bounded sample identity and uncertainty that must survive reuse independently of optional artifacts.</summary>
/// <remarks>
/// Producers declare genuine, globally stable observation ids and the statistics policy; this metadata cannot
/// prove independence, stationarity or truthful measurement. Reuse keeps the original observation ids, time,
/// uncertainty and acquisition cost. It never creates independent samples. Current work is charged separately
/// through EvolutionTaskResult.CostUnits and the resource ledger, not through OriginalCostUnits.
/// </remarks>
public sealed class EvolutionMeasurementOrigin
{
    /// <summary>Maximum original observation identities retained in one measurement record.</summary>
    public const int MaximumSampleIds = 256;
    /// <summary>Maximum UTF8 bytes in serialized measurement-origin metadata.</summary>
    public const int MaximumJsonBytes = 512 * 1024;

    /// <summary>Creates a sample-origin declaration; confidence bounds and their level must be supplied together.</summary>
    public EvolutionMeasurementOrigin(string scopeKey, string sourceRunId, string sourceEvaluationId,
        IEnumerable<string> sampleIds, DateTimeOffset observedAt, double originalCostUnits, string costUnit,
        string statisticsVersion, EvolutionMeasurementOriginKind kind = EvolutionMeasurementOriginKind.Measured,
        double? standardError = null, double? lowerConfidenceBound = null, double? upperConfidenceBound = null,
        double? confidenceLevel = null)
    {
        EvolutionReuseEncoding.Digest(scopeKey, nameof(scopeKey));
        EvolutionReuseEncoding.Label(sourceRunId, nameof(sourceRunId));
        EvolutionReuseEncoding.Label(sourceEvaluationId, nameof(sourceEvaluationId));
        EvolutionReuseEncoding.Label(costUnit, nameof(costUnit));
        EvolutionReuseEncoding.Label(statisticsVersion, nameof(statisticsVersion));
        Guard.NotNull(sampleIds);
        string[] identities = sampleIds.Take(MaximumSampleIds + 1).ToArray();
        if (identities.Length == 0 || identities.Length > MaximumSampleIds)
            throw new ArgumentException("A measurement origin requires one to 256 original sample ids.", nameof(sampleIds));
        foreach (string id in identities) EvolutionReuseEncoding.Label(id, nameof(sampleIds));
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length)
            throw new ArgumentException("Original sample ids must be distinct.", nameof(sampleIds));
        if (observedAt == default) throw new ArgumentOutOfRangeException(nameof(observedAt));
        if (!EvolutionDescriptorDefinition.IsFinite(originalCostUnits) || originalCostUnits < 0)
            throw new ArgumentOutOfRangeException(nameof(originalCostUnits));
        if (!Enum.IsDefined(typeof(EvolutionMeasurementOriginKind), kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (standardError.HasValue && (!EvolutionDescriptorDefinition.IsFinite(standardError.Value) || standardError.Value < 0))
            throw new ArgumentOutOfRangeException(nameof(standardError));
        if (lowerConfidenceBound.HasValue != upperConfidenceBound.HasValue ||
            lowerConfidenceBound.HasValue != confidenceLevel.HasValue)
            throw new ArgumentException("Confidence bounds and their level must be supplied together.", nameof(confidenceLevel));
        if (lowerConfidenceBound.HasValue &&
            (!EvolutionDescriptorDefinition.IsFinite(lowerConfidenceBound.Value) ||
             !EvolutionDescriptorDefinition.IsFinite(upperConfidenceBound!.Value) ||
             lowerConfidenceBound.Value > upperConfidenceBound.Value ||
             !EvolutionDescriptorDefinition.IsFinite(confidenceLevel!.Value) || confidenceLevel.Value <= 0 || confidenceLevel.Value >= 1))
            throw new ArgumentOutOfRangeException(nameof(confidenceLevel));
        ScopeKey = scopeKey; SourceRunId = sourceRunId; SourceEvaluationId = sourceEvaluationId;
        SampleIds = Array.AsReadOnly(identities); ObservedAt = observedAt.ToUniversalTime();
        OriginalCostUnits = originalCostUnits; CostUnit = costUnit; StatisticsVersion = statisticsVersion;
        Kind = kind; StandardError = standardError; LowerConfidenceBound = lowerConfidenceBound;
        UpperConfidenceBound = upperConfidenceBound; ConfidenceLevel = confidenceLevel;
        SampleSetHash = EvolutionHash.Combine(new[] { "measurement-sample-set-v1", scopeKey }
            .Concat(identities.OrderBy(id => id, StringComparer.Ordinal)));
    }

    /// <summary>Gets the exact twelve-facet applicability key.</summary>
    public string ScopeKey { get; }
    /// <summary>Gets the originating run id, not the current reuse run.</summary>
    public string SourceRunId { get; }
    /// <summary>Gets the originating evaluation id, not the current reuse attempt.</summary>
    public string SourceEvaluationId { get; }
    /// <summary>Gets globally stable identities of the original distinct observations.</summary>
    public IReadOnlyList<string> SampleIds { get; }
    /// <summary>Gets the number of original observations, never the number of cache lookups.</summary>
    public int SampleCount => SampleIds.Count;
    /// <summary>Gets an order-independent digest of the original sample set and scope.</summary>
    public string SampleSetHash { get; }
    /// <summary>Gets when the original measurement was made, in UTC.</summary>
    public DateTimeOffset ObservedAt { get; }
    /// <summary>Gets original acquisition cost retained for provenance, not charged as current work.</summary>
    public double OriginalCostUnits { get; }
    /// <summary>Gets the versioned unit of the acquisition cost.</summary>
    public string CostUnit { get; }
    /// <summary>Gets the policy defining the retained statistics and their assumptions.</summary>
    public string StatisticsVersion { get; }
    /// <summary>Gets how the current result obtained the original evidence.</summary>
    public EvolutionMeasurementOriginKind Kind { get; }
    /// <summary>Gets a declared standard error, or null when unavailable/not applicable.</summary>
    public double? StandardError { get; }
    /// <summary>Gets the declared lower confidence bound, or null when unavailable.</summary>
    public double? LowerConfidenceBound { get; }
    /// <summary>Gets the declared upper confidence bound, or null when unavailable.</summary>
    public double? UpperConfidenceBound { get; }
    /// <summary>Gets the declared confidence level for both bounds, or null when unavailable.</summary>
    public double? ConfidenceLevel { get; }

    /// <summary>Marks a copy without changing original sample identity, uncertainty, time or acquisition cost.</summary>
    public EvolutionMeasurementOrigin AsReused(EvolutionMeasurementOriginKind kind)
    {
        if (kind == EvolutionMeasurementOriginKind.Measured)
            throw new ArgumentException("A reused observation cannot be relabeled as newly measured.", nameof(kind));
        return new EvolutionMeasurementOrigin(ScopeKey, SourceRunId, SourceEvaluationId, SampleIds, ObservedAt,
            OriginalCostUnits, CostUnit, StatisticsVersion, kind, StandardError, LowerConfidenceBound, UpperConfidenceBound, ConfidenceLevel);
    }

    /// <summary>Serializes bounded metadata without genome or diagnostic bodies; caller labels must not contain secrets.</summary>
    public string ToJson()
    {
        string json = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            ScopeKey,
            SourceRunId,
            SourceEvaluationId,
            SampleIds,
            ObservedAt,
            OriginalCostUnits,
            CostUnit,
            StatisticsVersion,
            Kind,
            StandardError,
            LowerConfidenceBound,
            UpperConfidenceBound,
            ConfidenceLevel
        }, EvolutionJson.Compact);
        ValidateSize(json);
        return json;
    }

    /// <summary>Reads only the supported strict schema; ambiguous, oversized or invalid records are rejected.</summary>
    public static EvolutionMeasurementOrigin FromJson(string json)
    {
        ValidateSize(json);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        var fields = new HashSet<string>(new[] { "SchemaVersion", "ScopeKey", "SourceRunId", "SourceEvaluationId",
            "SampleIds", "ObservedAt", "OriginalCostUnits", "CostUnit", "StatisticsVersion", "Kind", "StandardError",
            "LowerConfidenceBound", "UpperConfidenceBound", "ConfidenceLevel" }, StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!fields.Remove(property.Name)) throw new InvalidDataException("Duplicate or unknown measurement-origin field.");
        if (fields.Count != 0 || root.GetProperty("SchemaVersion").GetInt32() != 1)
            throw new InvalidDataException("Missing or unsupported measurement-origin schema.");
        string Text(string name) => root.GetProperty(name).GetString() ?? throw new InvalidDataException("A measurement-origin string is null.");
        double? Number(string name) => root.GetProperty(name).ValueKind == JsonValueKind.Null ? null : root.GetProperty(name).GetDouble();
        string[] samples = root.GetProperty("SampleIds").EnumerateArray().Take(MaximumSampleIds + 1).Select(id => id.GetString()!).ToArray();
        return new EvolutionMeasurementOrigin(Text("ScopeKey"), Text("SourceRunId"), Text("SourceEvaluationId"), samples,
            root.GetProperty("ObservedAt").GetDateTimeOffset(), root.GetProperty("OriginalCostUnits").GetDouble(),
            Text("CostUnit"), Text("StatisticsVersion"), (EvolutionMeasurementOriginKind)root.GetProperty("Kind").GetInt32(),
            Number("StandardError"), Number("LowerConfidenceBound"), Number("UpperConfidenceBound"), Number("ConfidenceLevel"));
    }

    private static void ValidateSize(string json)
    {
        Guard.NotNull(json);
        if (json.Length > MaximumJsonBytes || EvolutionReuseEncoding.Utf8.GetByteCount(json) > MaximumJsonBytes)
            throw new InvalidDataException("Measurement-origin metadata exceeds its byte limit.");
    }
}
