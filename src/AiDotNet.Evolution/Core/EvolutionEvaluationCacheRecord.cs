using System.Text.Json;

namespace AiDotNet.Evolution;

/// <summary>Exact applicability, canonical genome/payload and measurement-policy identity for persistent reuse.</summary>
public sealed class EvolutionEvaluationCacheKey
{
    /// <summary>Creates a key; measurementVersion must identify the requested replication/aggregation policy.</summary>
    public EvolutionEvaluationCacheKey(EvolutionReuseScope scope, string genomeId, string genomePayloadSha256, string measurementVersion)
    {
        Guard.NotNull(scope); EvolutionReuseEncoding.Label(genomeId, nameof(genomeId));
        EvolutionReuseEncoding.Digest(genomePayloadSha256, nameof(genomePayloadSha256));
        EvolutionReuseEncoding.Label(measurementVersion, nameof(measurementVersion));
        Scope = scope; GenomeId = genomeId; GenomePayloadSha256 = genomePayloadSha256; MeasurementVersion = measurementVersion;
        StableKey = EvolutionHash.Combine(new[] { "persistent-evaluation-key-v1", scope.StableKey, genomeId, genomePayloadSha256, measurementVersion });
    }
    /// <summary>Gets all twelve explicit applicability facets.</summary>
    public EvolutionReuseScope Scope { get; }
    /// <summary>Gets the current canonical genome identity.</summary>
    public string GenomeId { get; }
    /// <summary>Gets the hash of the exact strict UTF8 codec payload, not just its canonical identity.</summary>
    public string GenomePayloadSha256 { get; }
    /// <summary>Gets the explicit replication/aggregation request fingerprint.</summary>
    public string MeasurementVersion { get; }
    /// <summary>Gets the domain-separated persistent lookup identity.</summary>
    public string StableKey { get; }

    /// <summary>Revalidates a canonical candidate and its exact bounded codec round trip before constructing a key.</summary>
    /// <remarks>Custom task/codec work is caller-metered and caller-isolated; this does not execute EvaluateAsync.</remarks>
    public static async ValueTask<EvolutionEvaluationCacheKey> CreateAsync<TGenome>(EvolutionCanonicalGenome<TGenome> candidate,
        IEvolutionTask<TGenome> task, IEvolutionGenomeCodec<TGenome> codec, EvolutionReuseScope scope, string measurementVersion,
        CancellationToken cancellationToken = default)
    {
        Guard.NotNull(candidate); Guard.NotNull(scope); EvolutionReuseEncoding.Label(measurementVersion, nameof(measurementVersion));
        cancellationToken.ThrowIfCancellationRequested(); scope.Validate(task, codec);
        string payload = codec.Serialize(candidate.Genome);
        EvolutionReuseJson.ValidateSize(payload, EvolutionRepertoire.MaximumPayloadBytes);
        scope.Validate(task, codec); cancellationToken.ThrowIfCancellationRequested();
        var canonical = await task.CanonicalizeAsync(candidate.Genome, cancellationToken).ConfigureAwait(false);
        scope.Validate(task, codec); cancellationToken.ThrowIfCancellationRequested();
        if (canonical is null || canonical.Id != candidate.Id || codec.Serialize(canonical.Genome) != payload)
            throw new InvalidOperationException("The candidate is not canonical under the current task and codec.");
        scope.Validate(task, codec); cancellationToken.ThrowIfCancellationRequested();
        TGenome decoded = codec.Deserialize(payload);
        scope.Validate(task, codec); cancellationToken.ThrowIfCancellationRequested();
        var roundTrip = await task.CanonicalizeAsync(decoded, cancellationToken).ConfigureAwait(false);
        scope.Validate(task, codec); cancellationToken.ThrowIfCancellationRequested();
        if (roundTrip is null || roundTrip.Id != candidate.Id || codec.Serialize(roundTrip.Genome) != payload)
            throw new InvalidOperationException("The canonical genome codec round trip changed identity or payload.");
        scope.Validate(task, codec); cancellationToken.ThrowIfCancellationRequested();
        return new(scope, candidate.Id, EvolutionHash.Compute(payload), measurementVersion);
    }
}

/// <summary>An immutable successful measurement record, separate from current-operation cost and optional artifacts.</summary>
/// <remarks>
/// Only fresh, feasible, completed evidence can be stored. Diagnostic/artifact bodies are deliberately excluded;
/// EvidenceSha256 must identify separately retained raw evidence. Integrity hashes are not authentication or proof
/// that a producer used the declared evaluator/environment. Metric names and caller labels must not contain secrets.
/// </remarks>
public sealed class EvolutionEvaluationCacheRecord
{
    /// <summary>Maximum UTF8 bytes in a serialized record envelope.</summary>
    public const int MaximumJsonBytes = 4 * 1024 * 1024;
    private readonly EvolutionTaskResult _measurement;

    /// <summary>Creates an owned reusable record from genuine new evidence with matching scope.</summary>
    public EvolutionEvaluationCacheRecord(EvolutionEvaluationCacheKey key, EvolutionTaskResult measurement, string evidenceSha256)
    {
        Guard.NotNull(key); Guard.NotNull(measurement); EvolutionReuseEncoding.Digest(evidenceSha256, nameof(evidenceSha256));
        EvolutionMeasurementOrigin origin = measurement.MeasurementOrigin ?? throw new ArgumentException("Persistent evidence requires sample origin.", nameof(measurement));
        if (measurement.Status != EvolutionEvaluationStatus.Completed || measurement.ConstraintViolations.Any(value => value > 0) ||
            origin.Kind != EvolutionMeasurementOriginKind.Measured || origin.ScopeKey != key.Scope.StableKey)
            throw new ArgumentException("Store only fresh, feasible completed evidence with matching scope.", nameof(measurement));
        foreach (string name in measurement.Descriptors.Keys.Concat(measurement.Metrics.Keys)) EvolutionReuseEncoding.Label(name, nameof(measurement));
        Key = key; Origin = origin; EvidenceSha256 = evidenceSha256;
        _measurement = new EvolutionTaskResult(measurement.Status, measurement.Quality, measurement.Direction, measurement.Descriptors,
            measurement.Objectives, measurement.ConstraintViolations, 0, metrics: measurement.Metrics);
    }
    /// <summary>Gets exact lookup identity and scope.</summary>
    public EvolutionEvaluationCacheKey Key { get; }
    /// <summary>Gets the original acquisition declaration; this does not label a later lookup as fresh work.</summary>
    public EvolutionMeasurementOrigin Origin { get; }
    /// <summary>Gets the SHA256 of separately retained raw evidence.</summary>
    public string EvidenceSha256 { get; }
    /// <summary>Gets the measured scalar quality.</summary>
    public double Quality => _measurement.Quality!.Value;
    /// <summary>Gets the required optimization direction.</summary>
    public EvolutionOptimizationDirection Direction => _measurement.Direction;

    /// <summary>Materializes an explicitly reused result, charging only caller-reported current work.</summary>
    public EvolutionTaskResult AsReused(double currentCostUnits = 0) => new EvolutionTaskResult(_measurement.Status,
        _measurement.Quality, _measurement.Direction, _measurement.Descriptors, _measurement.Objectives,
        _measurement.ConstraintViolations, currentCostUnits, metrics: _measurement.Metrics)
        .WithMeasurementOrigin(Origin.AsReused(EvolutionMeasurementOriginKind.PersistentReuse));

    /// <summary>Serializes bounded metadata and values with an integrity checksum, never genome or diagnostic bodies.</summary>
    public string ToJson()
    {
        string payload = JsonSerializer.Serialize(new
        {
            SchemaVersion = 1,
            Scope = Key.Scope.CopyParts(),
            Key.GenomeId,
            Key.GenomePayloadSha256,
            Key.MeasurementVersion,
            EvidenceSha256,
            OriginJson = Origin.ToJson(),
            _measurement.Quality,
            Direction = (int)_measurement.Direction,
            _measurement.Descriptors,
            _measurement.Objectives,
            _measurement.ConstraintViolations,
            _measurement.Metrics
        }, EvolutionJson.Compact);
        EvolutionReuseJson.ValidateSize(payload, MaximumJsonBytes / 2);
        string json = JsonSerializer.Serialize(new { Checksum = EvolutionHash.Compute(payload), Payload = payload }, EvolutionJson.Compact);
        EvolutionReuseJson.ValidateSize(json, MaximumJsonBytes);
        return json;
    }

    /// <summary>Rejects unknown/duplicate fields, invalid identities/measurements, oversized data and corrupt checksums.</summary>
    public static EvolutionEvaluationCacheRecord FromJson(string json)
    {
        EvolutionReuseJson.ValidateSize(json, MaximumJsonBytes);
        using JsonDocument envelope = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement outer = envelope.RootElement;
        EvolutionReuseJson.RequireFields(outer, "Checksum", "Payload");
        string payload = EvolutionReuseJson.Text(outer, "Payload");
        EvolutionReuseJson.ValidateSize(payload, MaximumJsonBytes / 2);
        if (EvolutionReuseJson.Text(outer, "Checksum") != EvolutionHash.Compute(payload)) throw new InvalidDataException("Persistent evaluation checksum mismatch.");
        using JsonDocument document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        EvolutionReuseJson.RequireFields(root, "SchemaVersion", "Scope", "GenomeId", "GenomePayloadSha256", "MeasurementVersion",
            "EvidenceSha256", "OriginJson", "Quality", "Direction", "Descriptors", "Objectives", "ConstraintViolations", "Metrics");
        if (root.GetProperty("SchemaVersion").GetInt32() != 1) throw new InvalidDataException("Unsupported persistent evaluation schema.");
        var scope = new EvolutionReuseScope(root.GetProperty("Scope").EnumerateArray().Take(13).Select(part => part.GetString()!).ToArray());
        var key = new EvolutionEvaluationCacheKey(scope, EvolutionReuseJson.Text(root, "GenomeId"),
            EvolutionReuseJson.Text(root, "GenomePayloadSha256"), EvolutionReuseJson.Text(root, "MeasurementVersion"));
        var result = new EvolutionTaskResult(EvolutionEvaluationStatus.Completed, root.GetProperty("Quality").GetDouble(),
            (EvolutionOptimizationDirection)root.GetProperty("Direction").GetInt32(), ReadValues(root.GetProperty("Descriptors")),
            ReadVector(root.GetProperty("Objectives")), ReadVector(root.GetProperty("ConstraintViolations")),
            metrics: ReadValues(root.GetProperty("Metrics"))).WithMeasurementOrigin(
                EvolutionMeasurementOrigin.FromJson(EvolutionReuseJson.Text(root, "OriginJson")));
        return new EvolutionEvaluationCacheRecord(key, result, EvolutionReuseJson.Text(root, "EvidenceSha256"));
    }

    private static double[] ReadVector(JsonElement element) => element.EnumerateArray()
        .Take(EvolutionTaskResult.MaximumVectorValues + 1).Select(value => value.GetDouble()).ToArray();

    private static Dictionary<string, double> ReadValues(JsonElement element)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (result.Count >= EvolutionTaskResult.MaximumNamedValues || result.ContainsKey(property.Name))
                throw new InvalidDataException("Duplicate or excessive persistent evaluation values.");
            EvolutionReuseEncoding.Label(property.Name, nameof(element));
            result.Add(property.Name, property.Value.GetDouble());
        }
        return result;
    }
}

internal static class EvolutionReuseJson
{
    internal static void ValidateSize(string json, int maximum)
    {
        Guard.NotNull(json);
        if (json.Length > maximum || EvolutionReuseEncoding.Utf8.GetByteCount(json) > maximum)
            throw new InvalidDataException("Persistent evaluation JSON exceeds its byte limit.");
    }
    internal static string Text(JsonElement root, string name) => root.GetProperty(name).GetString()
        ?? throw new InvalidDataException("A persistent evaluation string is null.");
    internal static void RequireFields(JsonElement root, params string[] names)
    {
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!remaining.Remove(property.Name)) throw new InvalidDataException("Unknown or duplicate persistent evaluation field.");
        if (remaining.Count != 0) throw new InvalidDataException("Missing persistent evaluation field.");
    }
}
