using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiDotNet.Evolution;

// The host evolves strings; only the separately isolated broker evaluator executes candidate code.
if (args.Length != 7 || !int.TryParse(args[3], out int iterations) || iterations is < 1 or > 64 ||
    !uint.TryParse(args[4], out uint seed) || args[6] is not ("controlled" or "native-bounded")) return 64;
string Read(string path, int bound)
{
    using var file = File.OpenRead(path);
    if (file.Length > bound) throw new InvalidDataException("Input exceeds its bound.");
    using var reader = new StreamReader(file, new UTF8Encoding(false, true));
    return reader.ReadToEnd();
}
string initial = Read(args[0], 65536), task = Read(args[5], 16384), mode = args[6];
using var broker = new Broker();
using var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
var evaluator = new ProgramTask(broker);
var variation = new ProgramVariation(broker, task, mode);
var engine = new EvolutionEngine<string>(evaluator, variation,
    _ => new MapElitesArchive<string>(new[] { new EvolutionDescriptorDefinition("length", 0, 65536, 64) }),
    new EvolutionEngineOptions
    {
        RunId = "us02-evolution-owned",
        Seed = seed,
        MaxEvaluationAttempts = iterations + 1,
        MaxProposals = iterations + 1, // The unchanged initial seed also consumes one proposal.
        MaxGenerations = iterations + 1,
        ProposalBatchSize = 1,
        MaxDegreeOfParallelism = 1,
        IslandCount = 1,
        InspirationCount = 3,
        MigrationInterval = 0,
        EnableEvaluationCache = false,
        EvaluationGracePeriod = null
    });
try
{
    var result = await engine.RunAsync(new[] { initial });
    await JsonSerializer.SerializeAsync(output, new
    {
        schema = "evolution-owned-program-run-v1",
        mode,
        requested_model = args[2],
        seed,
        iterations,
        engine = new { result.Counters, result.StateHash, result.StopReason },
        best = result.Best?.Candidate.CanonicalGenome.Genome,
        artifacts = new[] { typeof(ProgramTask).Assembly.Location, typeof(EvolutionEngineOptions).Assembly.Location }
            .ToDictionary(p => Path.GetFileName(p)!, p => ProgramTask.Hash(File.ReadAllBytes(p))),
        limitations = "Evolution core; length MAP-Elites, one island, full rewrite, no retries. Native-bounded adds parent score/inspirations; it is not the legacy AiDotNet facade preset."
    });
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    await JsonSerializer.SerializeAsync(output, new { status = "failed", error = error.GetType().Name });
    return 1;
}

internal sealed class ProgramTask(Broker broker) : IEvolutionTask<string>
{
    public string Id => "shared-python-program";
    public string VersionHash => "evolution-owned-program-v1";
    public string EvaluatorVersionHash => "independent-broker-v1";
    internal static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    public ValueTask<EvolutionCanonicalGenome<string>> CanonicalizeAsync(string genome, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        byte[] bytes = new UTF8Encoding(false, true).GetBytes(genome);
        if (bytes.Length is < 1 or > 65536) throw new InvalidDataException("Invalid program size.");
        return new(new EvolutionCanonicalGenome<string>(genome, Hash(bytes)));
    }
    public async ValueTask<EvolutionTaskResult> EvaluateAsync(EvolutionCandidate<string> candidate,
        EvolutionEvaluationContext context, CancellationToken token = default)
    {
        string code = candidate.CanonicalGenome.Genome;
        var receipt = await broker.Call("evaluate", new { code }, token);
        if (receipt.GetProperty("candidate_hash").GetString() != candidate.CanonicalGenome.Id ||
            receipt.GetProperty("unknown_work").GetBoolean()) throw new InvalidDataException("Unreconciled evaluation.");
        string? status = receipt.GetProperty("status").GetString();
        if (status is not ("valid" or "invalid")) throw new InvalidDataException("Invalid evaluation status.");
        double work = receipt.GetProperty("work_units").GetDouble();
        if (!double.IsFinite(work) || work < 0) throw new InvalidDataException("Invalid work receipt.");
        if (status == "invalid") return new EvolutionTaskResult(EvolutionEvaluationStatus.Failed, costUnits: work);
        double quality = receipt.GetProperty("quality").GetDouble();
        return EvolutionTaskResult.Completed(quality, new Dictionary<string, double> { ["length"] = code.Length }, costUnits: work);
    }
}

internal sealed class ProgramVariation(Broker broker, string task, string mode) : IVariationOperator<string>
{
    public string Id => "broker-full-rewrite";
    public string VersionHash => "evolution-owned-rewrite-v1-" + mode;
    public async ValueTask<string> ProposeAsync(EvolutionVariationContext<string> context, CancellationToken token = default)
    {
        string system = "Optimize the supplied Python program for this task. Return the complete program in a python code fence. " +
            "Preserve its interface and correctness. Do not access tools or evaluate it yourself.\nTask:\n" + task;
        string prompt = "Parent program:\n```python\n" + context.Parent.Candidate.CanonicalGenome.Genome + "\n```";
        if (mode == "native-bounded")
        {
            prompt += "\nMeasured parent maximization score: " + context.Parent.Evaluation.Quality?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            foreach (var inspiration in context.Inspirations.Take(3))
            {
                string addition = "\nInspiration:\n```python\n" + inspiration.Candidate.CanonicalGenome.Genome + "\n```";
                if (Encoding.UTF8.GetByteCount(prompt + addition) < 48000) prompt += addition;
            }
        }
        var result = await broker.Call("model", new { system, messages = new[] { new { role = "user", content = prompt } } }, token);
        string response = result.GetString() ?? throw new InvalidDataException("Missing model response.");
        string[] blocks = response.Split("```", StringSplitOptions.None);
        if (blocks.Length != 3 || !string.IsNullOrWhiteSpace(blocks[0]) || !string.IsNullOrWhiteSpace(blocks[2]))
            throw new InvalidDataException("Expected one complete fenced Python program.");
        int newline = blocks[1].IndexOf('\n');
        if (newline < 0 || blocks[1][..newline].Trim() != "python") throw new InvalidDataException("Expected a Python fence.");
        return blocks[1][(newline + 1)..];
    }
}

internal sealed class Broker : IDisposable
{
    private readonly HttpClient _http;
    public Broker()
    {
        var address = new Uri(Environment.GetEnvironmentVariable("EVOLUTION_BROKER_ENDPOINT") ?? "", UriKind.Absolute);
        if (address.Scheme != "http" || address.Host != "127.0.0.1" || address.UserInfo.Length != 0 ||
            address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new InvalidDataException("Only a loopback broker is permitted.");
        string capability = Environment.GetEnvironmentVariable("EVOLUTION_BROKER_CAPABILITY") ?? "";
        if (capability.Length != 64 || capability.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("Missing capability.");
        _http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
        { BaseAddress = address, Timeout = TimeSpan.FromSeconds(310), MaxResponseContentBufferSize = 256 * 1024 };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", capability);
    }
    public async Task<JsonElement> Call(string operation, object payload, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (bytes.Length > 256 * 1024) throw new InvalidDataException("Broker request exceeds its bound.");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync(operation, content, token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
        if (json.RootElement.GetProperty("status").GetString() != "ok") throw new InvalidDataException("Broker refused work.");
        return json.RootElement.GetProperty("result").Clone();
    }
    public void Dispose() => _http.Dispose();
}
