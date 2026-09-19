using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

[JsonSerializable(typeof(EvolutionEngineDocuments.EngineStateDocument))]
[JsonSerializable(typeof(Dictionary<string, double>), TypeInfoPropertyName = "NumericMap")]
[JsonSerializable(typeof(ResourceMeteredVariationDocuments.State), TypeInfoPropertyName = "MeteredVariationState")]
[JsonSerializable(typeof(MeasurementOriginDocument))]
internal sealed partial class EvolutionStateJsonContext : JsonSerializerContext;

// These documents intentionally retain the prior field order and default/null encoding.
// They contain serialized backend/genome strings, never open generic genome properties.
internal static class ResourceMeteredVariationDocuments
{
    internal sealed class Pending
    {
        public Dictionary<string, decimal>? Charged { get; set; }
        public EvolutionResourceOutcome Outcome { get; set; }
        public bool ExceededMaximum { get; set; }
        public bool Dispatched { get; set; }
    }
    internal sealed class State
    {
        public string? VersionHash { get; set; }
        public string? Backend { get; set; }
        public SortedDictionary<long, Pending>? Pending { get; set; }
    }
}

internal sealed class MeasurementOriginDocument
{
    public int SchemaVersion { get; set; }
    public string ScopeKey { get; set; } = string.Empty;
    public string SourceRunId { get; set; } = string.Empty;
    public string SourceEvaluationId { get; set; } = string.Empty;
    public string[] SampleIds { get; set; } = Array.Empty<string>();
    public DateTimeOffset ObservedAt { get; set; }
    public double OriginalCostUnits { get; set; }
    public string CostUnit { get; set; } = string.Empty;
    public string StatisticsVersion { get; set; } = string.Empty;
    public EvolutionMeasurementOriginKind Kind { get; set; }
    public double? StandardError { get; set; }
    public double? LowerConfidenceBound { get; set; }
    public double? UpperConfidenceBound { get; set; }
    public double? ConfidenceLevel { get; set; }
}
