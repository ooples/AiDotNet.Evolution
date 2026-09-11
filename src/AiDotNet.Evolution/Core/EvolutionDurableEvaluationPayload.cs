using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution;

/// <summary>A versioned, bounded genome/context envelope for external session evaluators.</summary>
/// <remarks>Unsigned 64-bit random seeds are decimal strings on the wire, never rounded JavaScript numbers.
/// The injected genome codec and result-decoding protocol must be covered by the session compatibility fingerprint.</remarks>
public sealed class EvolutionDurableEvaluationPayload
{
    private const int MaximumBytes = 1024 * 1024;

    /// <summary>Creates an envelope from the engine's own codec payload and deterministic evaluator context.</summary>
    public EvolutionDurableEvaluationPayload(string genomePayload, string canonicalGenomeId, EvolutionEvaluationContext context)
    {
        EvolutionWorkValidation.Payload(genomePayload, MaximumBytes, nameof(genomePayload));
        Guard.NotNullOrWhiteSpace(canonicalGenomeId);
        EvolutionWorkValidation.Payload(canonicalGenomeId, MaximumBytes, nameof(canonicalGenomeId));
        Guard.NotNull(context);
        GenomePayload = genomePayload; CanonicalGenomeId = canonicalGenomeId; Context = context;
    }
    /// <summary>Gets the genome encoded by the engine-owned codec.</summary>
    public string GenomePayload { get; }
    /// <summary>Gets the engine's complete canonical identity, without truncating display-sized or multiline identities.</summary>
    public string CanonicalGenomeId { get; }
    /// <summary>Gets the exact evaluator identity, root seed, seed stream and engine attempt count.</summary>
    public EvolutionEvaluationContext Context { get; }

    /// <summary>Serializes with generated metadata and a one-MiB envelope bound; coordinator limits may be stricter.</summary>
    public string ToJson()
    {
        string json = JsonSerializer.Serialize(new DurableSessionWorkDocument
        {
            Schema = 1,
            GenomePayload = GenomePayload,
            CanonicalGenomeId = CanonicalGenomeId,
            EvaluationId = Context.EvaluationId.ToString(CultureInfo.InvariantCulture),
            Attempt = Context.AttemptCount,
            RootSeed = Context.RootSeed.ToString(CultureInfo.InvariantCulture),
            SeedStream = Context.SeedStream.ToString(CultureInfo.InvariantCulture),
        }, EvolutionWorkJsonContext.Default.DurableSessionWorkDocument);
        EvolutionWorkValidation.Payload(json, MaximumBytes, nameof(json)); return json;
    }

    /// <summary>Reads a complete versioned envelope, refusing absent context or lossy numeric seed representations.</summary>
    public static EvolutionDurableEvaluationPayload FromJson(string json)
    {
        EvolutionWorkValidation.Payload(json, MaximumBytes, nameof(json));
        DurableSessionWorkDocument document = JsonSerializer.Deserialize(json, EvolutionWorkJsonContext.Default.DurableSessionWorkDocument)
            ?? throw new InvalidDataException("Missing durable session payload.");
        if (document.Schema != 1 || document.EvaluationId is null || document.Attempt is null)
            throw new InvalidDataException("Incomplete or unsupported durable session context.");
        ulong evaluationId = ParseSeed(document.EvaluationId);
        if (evaluationId > long.MaxValue) throw new InvalidDataException("Evaluation identity exceeds Int64.");
        var context = new EvolutionEvaluationContext((long)evaluationId, ParseSeed(document.RootSeed),
            ParseSeed(document.SeedStream), document.Attempt.Value);
        return new EvolutionDurableEvaluationPayload(document.GenomePayload!, document.CanonicalGenomeId!, context);
    }

    private static ulong ParseSeed(string value)
    {
        if (!ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number)
            || value != number.ToString(CultureInfo.InvariantCulture)) throw new InvalidDataException("A seed must be a canonical unsigned 64-bit decimal string.");
        return number;
    }
}

internal sealed class DurableSessionWorkDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; }
    [JsonPropertyName("genomePayload")]
    public string? GenomePayload { get; set; }
    [JsonPropertyName("canonicalGenomeId")]
    public string? CanonicalGenomeId { get; set; }
    [JsonPropertyName("evaluationId")]
    public string? EvaluationId { get; set; }
    [JsonPropertyName("attempt")]
    public int? Attempt { get; set; }
    [JsonPropertyName("rootSeed")]
    public string RootSeed { get; set; } = string.Empty;
    [JsonPropertyName("seedStream")]
    public string SeedStream { get; set; } = string.Empty;
}
