using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiDotNet.Evolution.Host;

/// <summary>The wire shapes, and the source-generated context AOT needs to serialise them.</summary>
/// <remarks>
/// <para>
/// ONE JSON OBJECT PER LINE, IN AND OUT. The transport is a child process rather than an FFI
/// binding, chosen because ask/tell is coarse-grained -- one call per BATCH, not per
/// evaluation -- so a process hop costs almost nothing while removing an ABI to keep in sync,
/// a C++ toolchain, and the possibility of a native crash taking the host runtime with it.
/// </para>
/// <para>
/// SOURCE-GENERATED SERIALISATION, not reflection. NativeAOT trims the metadata that
/// <c>JsonSerializer</c>'s reflection path needs, and the failure is at runtime on the user's
/// machine rather than at build time on ours. The generated context makes it a compile-time
/// concern instead, which is why <c>TreatWarningsAsErrors</c> is on for this project.
/// </para>
/// </remarks>
internal static class Protocol
{
    /// <summary>Reads a request, or null when the line is unusable.</summary>
    internal static Request? ParseRequest(string line, out string? error)
    {
        error = null;
        try
        {
            Request? request = JsonSerializer.Deserialize(line, HostJsonContext.Default.Request);
            if (request is null || string.IsNullOrWhiteSpace(request.Op))
            {
                error = "a request needs an 'op'";
                return null;
            }
            return request;
        }
        catch (JsonException ex)
        {
            error = $"malformed JSON: {ex.Message}";
            return null;
        }
    }

    internal static string Serialize(Response response) =>
        JsonSerializer.Serialize(response, HostJsonContext.Default.Response);
}

/// <summary>One line from the client.</summary>
internal sealed class Request
{
    /// <summary>`open`, `ask`, `tell`, `status`, `close`, or `ping`.</summary>
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    /// <summary>Echoed back on the response, so a client can correlate without ordering rules.</summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("config")]
    public RunConfig? Config { get; set; }

    /// <summary>`ask`: the largest batch wanted.</summary>
    [JsonPropertyName("max")]
    public int Max { get; set; }

    /// <summary>`tell`: the outcomes of previously asked candidates.</summary>
    [JsonPropertyName("results")]
    public List<TellResult>? Results { get; set; }
}

/// <summary>Everything needed to start a run.</summary>
internal sealed class RunConfig
{
    [JsonPropertyName("parameters")]
    public List<ParameterConfig> Parameters { get; set; } = new();

    /// <summary>Behaviour dimensions the archive is organised by. Names the client reports in `tell`.</summary>
    [JsonPropertyName("descriptors")]
    public List<DescriptorConfig> Descriptors { get; set; } = new();

    /// <summary>Starting points. Absent or empty means one genome at the midpoint of every range.</summary>
    [JsonPropertyName("seeds")]
    public List<Dictionary<string, double>>? Seeds { get; set; }

    [JsonPropertyName("seed")]
    public ulong Seed { get; set; } = 1234UL;

    [JsonPropertyName("maxProposals")]
    public int MaxProposals { get; set; } = 200;

    /// <summary>Evaluation-attempt budget, which is a SEPARATE cap from proposals.</summary>
    /// <remarks>
    /// Exposed because leaving it at the engine default silently capped runs: a client
    /// asking for 300 proposals got 100 evaluations and a `stopReason` of
    /// `EvaluationBudgetReached` that named no setting it could raise. Defaults to the
    /// proposal budget, so raising one raises both unless the caller separates them.
    /// </remarks>
    [JsonPropertyName("maxEvaluations")]
    public int? MaxEvaluations { get; set; }

    [JsonPropertyName("maxGenerations")]
    public int MaxGenerations { get; set; } = 1000;

    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 8;

    /// <summary>`maximize` (default) or `minimize`.</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "maximize";
}

internal sealed class ParameterConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }

    /// <summary>Resolution. Defaults to a hundredth of the range.</summary>
    [JsonPropertyName("step")]
    public double? Step { get; set; }

    [JsonPropertyName("integral")]
    public bool Integral { get; set; }
}

internal sealed class DescriptorConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("min")]
    public double Min { get; set; }

    [JsonPropertyName("max")]
    public double Max { get; set; }

    [JsonPropertyName("bins")]
    public int Bins { get; set; } = 10;
}

/// <summary>One scored candidate coming back from the client.</summary>
internal sealed class TellResult
{
    [JsonPropertyName("evaluationId")]
    public long EvaluationId { get; set; }

    /// <summary>Absent means the evaluation failed; `reason` then says why.</summary>
    [JsonPropertyName("quality")]
    public double? Quality { get; set; }

    [JsonPropertyName("descriptors")]
    public Dictionary<string, double>? Descriptors { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>One line back to the client.</summary>
internal sealed class Response
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>`ask`: the candidates to score. Empty means the run is over.</summary>
    [JsonPropertyName("candidates")]
    public List<Candidate>? Candidates { get; set; }

    /// <summary>`tell`: how many of the reported ids were actually outstanding.</summary>
    [JsonPropertyName("accepted")]
    public int? Accepted { get; set; }

    [JsonPropertyName("complete")]
    public bool? Complete { get; set; }

    /// <summary>`close`/`status`: the best genome found so far, when there is one.</summary>
    [JsonPropertyName("best")]
    public Candidate? Best { get; set; }

    [JsonPropertyName("stopReason")]
    public string? StopReason { get; set; }

    [JsonPropertyName("version")]
    public string? Version { get; set; }
}

internal sealed class Candidate
{
    [JsonPropertyName("evaluationId")]
    public long EvaluationId { get; set; }

    [JsonPropertyName("parameters")]
    public Dictionary<string, double> Parameters { get; set; } = new();

    [JsonPropertyName("quality")]
    public double? Quality { get; set; }
}

[JsonSerializable(typeof(Request))]
[JsonSerializable(typeof(Response))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class HostJsonContext : JsonSerializerContext;
