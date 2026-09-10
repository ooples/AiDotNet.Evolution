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
    /// <summary>
    /// Reads a request.
    /// </summary>
    /// <remarks>
    /// PARSING AND VALIDATION ARE SEPARATE, and conflating them lost the correlation id.
    /// <c>{"id":42,"op":""}</c> is perfectly good JSON with a bad <c>op</c>: the old code
    /// returned null for both cases, the caller had no request to read an id from, and the
    /// error went back as <c>id: 0</c> -- unmatchable against request 42, in a protocol
    /// whose whole ordering story is that the id comes back.
    /// </remarks>
    /// <param name="line">One JSON-lines frame.</param>
    /// <param name="error">Set when the frame cannot be used.</param>
    /// <param name="id">The id to answer with, which survives a validation failure.</param>
    internal static Request? ParseRequest(string line, out string? error, out long id)
    {
        error = null;
        id = 0;

        Request? request;
        try
        {
            request = JsonSerializer.Deserialize(line, HostJsonContext.Default.Request);
        }
        catch (JsonException ex)
        {
            // No id is recoverable here: the document did not parse, so there is nothing
            // to read one out of.
            error = $"malformed JSON: {ex.Message}";
            return null;
        }

        if (request is null)
        {
            error = "a request must be a JSON object";
            return null;
        }

        // Recovered BEFORE validation, which is the entire point of the split.
        id = request.Id;

        if (string.IsNullOrWhiteSpace(request.Op))
        {
            error = "a request needs an 'op'";
            return null;
        }
        return request;
    }

    /// <summary>The operations this host implements.</summary>
    /// <remarks>
    /// AN ENUM INSIDE, A STRING ON THE WIRE, deliberately. Deserializing straight into an
    /// enum makes an unknown op a <c>JsonException</c>, which reaches the client as
    /// "malformed JSON" and drops the id -- so a client that sent a typo, or spoke a newer
    /// protocol version, would be told its perfectly valid JSON was broken. Mapping here
    /// keeps the closed set inside the host, where the compiler can check the switch, and
    /// keeps the wire error accurate.
    /// </remarks>
    internal enum Op
    {
        Ping,
        Open,
        Ask,
        Tell,
        Status,
        Close,
    }

    /// <summary>Maps a wire op to the enum, or null when it names nothing.</summary>
    internal static Op? ParseOp(string op) => op switch
    {
        "ping" => Op.Ping,
        "open" => Op.Open,
        "ask" => Op.Ask,
        "tell" => Op.Tell,
        "status" => Op.Status,
        "close" => Op.Close,
        _ => null,
    };

    internal static string Serialize(Response response) =>
        JsonSerializer.Serialize(response, HostJsonContext.Default.Response);
}

/// <summary>What the host will accept in one frame, and in one collection.</summary>
/// <remarks>
/// A HOST READING FROM A PIPE HAS NO NATURAL BOUND. `ReadLineAsync` grows a string until
/// it finds a newline, so a peer that never sends one -- a bug, a wedged client, a hostile
/// one -- walks the host out of memory with no error anyone can act on. The collection
/// caps are the same argument one level down: a single well-formed frame declaring a
/// million parameters is small on the wire and large in the heap.
///
/// The numbers are generous rather than tuned. They exist to turn "the process died" into
/// "the host said the frame was too large", which is the difference between a mystery and
/// a bug report.
/// </remarks>
internal static class ProtocolLimits
{
    /// <summary>Longest frame the host will assemble, in characters.</summary>
    internal const int MaxFrameChars = 16 * 1024 * 1024;

    /// <summary>Most parameters or descriptors one run may declare.</summary>
    internal const int MaxDimensions = 4096;

    /// <summary>Most seed genomes one run may be given.</summary>
    internal const int MaxSeeds = 10_000;

    /// <summary>Most results one `tell` may carry.</summary>
    internal const int MaxResults = 8192;
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
