using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiDotNet.Evolution.Programs;

namespace AiDotNet.Evolution.Cli;

/// <summary>The run file: what to evolve, how to score it, which model proposes, and the budget.</summary>
/// <remarks>Relative paths resolve against the run file's directory. Unknown fields are refused, not ignored.</remarks>
internal sealed class RunFile
{
    public const string CurrentSchema = "aidotnet-evolve-run-v1";

    public required string Schema { get; init; }
    public required string RunId { get; init; }
    /// <summary>The seed program.</summary>
    public required string InitialProgram { get; init; }
    /// <summary>Reads the candidate source on standard input and prints one JSON object with a numeric <c>quality</c>.</summary>
    public required string Evaluator { get; init; }
    public required RunModel Model { get; init; }
    public required RunBudget Budget { get; init; }
    /// <summary>Holds <c>checkpoints/</c> and one <c>trace-NNN.jsonl</c> per run or resume session.</summary>
    public required string Output { get; init; }
    public ProgramLanguage Language { get; init; } = ProgramLanguage.Python;
    public ProgramLanguage EvaluatorLanguage { get; init; } = ProgramLanguage.Python;
    public EvolutionOptimizationDirection Direction { get; init; } = EvolutionOptimizationDirection.Maximize;
    public string? TaskDescription { get; init; }
    /// <summary>Identity of the interpreter image; part of the checkpoint compatibility hash, so it must be stable across resumes.</summary>
    public string RuntimeVersion { get; init; } = "aidotnet-evolve-local";
    /// <summary>A repertoire exported by an earlier run whose programs join the seeds; used by <c>run</c>, ignored by <c>resume</c>.</summary>
    public string? WarmStart { get; init; }
}

internal sealed class RunModel
{
    /// <summary>An OpenAI-compatible base URL, for example <c>http://127.0.0.1:8000/v1</c>.</summary>
    public required string Endpoint { get; init; }
    public required string Name { get; init; }
    /// <summary>The environment variable holding the API key; null for an endpoint that needs none.</summary>
    public string? ApiKeyEnvironmentVariable { get; init; }
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
}

internal sealed class RunBudget
{
    /// <summary>Total evaluations for the run, the seed included; a resume continues toward the same total.</summary>
    public required int MaxEvaluations { get; init; }
    public ulong Seed { get; init; }
    public int Parallelism { get; init; } = 1;
    public int EvaluationTimeLimitSeconds { get; init; } = 60;
    public int MaxProgramChars { get; init; } = 65_536;
}

internal static class RunCommand
{
    private const int MaxRunFileBytes = 1 << 20;

    private static readonly JsonSerializerOptions RunJson = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    /// <summary>Exit code for a run aborted by a second interrupt; its final checkpoint was still written.</summary>
    public const int AbortedExitCode = 130;

    /// <summary>Exit code for a run in which the model was called and every call failed: a configuration error, not a result.</summary>
    public const int ModelUnavailableExitCode = 3;

    public static int Execute(string runFilePath, bool resume, TextWriter output, TextWriter error, RunInterrupt interrupt)
    {
        CancellationToken cancellationToken = interrupt.Token;
        string fullPath = Path.GetFullPath(runFilePath);
        RunFile run = Load(fullPath);
        string baseDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        string outputDirectory = Path.GetFullPath(Path.Combine(baseDirectory, run.Output));
        var checkpoints = new DirectoryEvolutionCheckpointStore(Path.Combine(outputDirectory, "checkpoints"));

        bool exists = checkpoints.LoadLatestAsync(run.RunId, cancellationToken).GetAwaiter().GetResult() is not null;
        if (resume && !exists)
            throw new InvalidDataException("Nothing to resume: " + outputDirectory + " holds no checkpoint for run '" + run.RunId + "'.");
        if (!resume && exists)
            throw new InvalidDataException(outputDirectory + " already holds run '" + run.RunId + "'; use resume, or choose another output. Runs never overwrite.");

        string initial = ReadBounded(Path.Combine(baseDirectory, run.InitialProgram), run.Budget.MaxProgramChars);
        string evaluator = ReadBounded(Path.Combine(baseDirectory, run.Evaluator), MaxRunFileBytes);
        string tracePath = NextTracePath(outputDirectory);

        var sandbox = new ProgramSandboxOptions { RuntimeVersion = run.RuntimeVersion };
        sandbox.Limits.TimeLimitSeconds = run.Budget.EvaluationTimeLimitSeconds;
        sandbox.Limits.MaxConcurrentExecutions = run.Budget.Parallelism;
        using var execution = new ProcessProgramExecutionEngine(sandbox);
        using var model = new OpenAiCompatibleChatClient(run.Model);

        var programOptions = new ProgramProposalOptions
        {
            Language = run.Language,
            TaskDescription = run.TaskDescription,
            MaxProgramChars = run.Budget.MaxProgramChars
        };
        var fitness = new ScriptProgramFitnessEvaluator(execution, evaluator,
            new ScriptProgramEvaluationOptions { EvaluatorScriptLanguage = run.EvaluatorLanguage, Direction = run.Direction });
        var task = new ProgramEvolutionTask(fitness, new ProgramDescriptorSet(new[] { new ProgramLengthDescriptor() }), programOptions);
        var variation = new LlmProgramVariationOperator(model, programOptions, new LlmProgramVariationOptions
        {
            Mode = ProgramEvolutionMode.FullRewrite,
            Temperature = run.Model.Temperature,
            MaxOutputTokens = run.Model.MaxOutputTokens
        });
        var descriptors = new[] { new EvolutionDescriptorDefinition("length", 0, run.Budget.MaxProgramChars, 64) };
        var codec = new ProgramGenomeCodec();
        EvolutionReuseScope scope = Scope(task, codec, run);

        // Warm start: the earlier run's programs become seeds and are evaluated afresh here. Their old measurements and
        // the cost of building them are reported beside this session's own spend, never folded into it.
        var seeds = new List<ProgramGenome> { new(initial, run.Language) };
        object? warmStart = null;
        if (run.WarmStart is not null && resume)
        {
            warmStart = new { Ignored = "resume restores the checkpoint's population; warmStart applies to run only" };
        }
        else if (run.WarmStart is not null)
        {
            string path = Path.Combine(baseDirectory, run.WarmStart);
            if (new FileInfo(path).Length > MaxRepertoireBytes) throw new InvalidDataException(path + " exceeds " + MaxRepertoireBytes + " bytes.");
            EvolutionRepertoire repertoire = EvolutionRepertoire.FromJson(File.ReadAllText(path, new UTF8Encoding(false, true)));
            EvolutionRepertoireImport<ProgramGenome> imported = repertoire.ImportAsync(task, codec, scope, cancellationToken).GetAwaiter().GetResult();
            string initialId = task.CanonicalizeAsync(seeds[0], cancellationToken).GetAwaiter().GetResult().Id;
            seeds.AddRange(imported.Seeds.Where(seed => seed.Id != initialId).Select(seed => seed.Genome));
            warmStart = new
            {
                Source = path,
                repertoire.Provenance.SourceRunId,
                Accepted = imported.Decisions.Count(d => d.Status == EvolutionRepertoireImportStatus.Accepted),
                Duplicate = imported.Decisions.Count(d => d.Status == EvolutionRepertoireImportStatus.Duplicate),
                Rejected = imported.Decisions.Count(d => d.Status == EvolutionRepertoireImportStatus.Rejected),
                repertoire.Provenance.PriorCostUnits,
                repertoire.Provenance.CostUnit
            };
        }

        var options = new EvolutionEngineOptions
        {
            RunId = run.RunId,
            Seed = run.Budget.Seed,
            MaxEvaluationAttempts = run.Budget.MaxEvaluations,
            MaxProposals = run.Budget.MaxEvaluations,
            MaxGenerations = run.Budget.MaxEvaluations,
            MaxDegreeOfParallelism = run.Budget.Parallelism,
            // One proposal per worker per batch: a graceful stop is observed at batch boundaries, so the default of 8 would
            // let a Ctrl+C run up to 8 more model calls before the run reports.
            ProposalBatchSize = run.Budget.Parallelism,
            CheckpointInterval = 1,
            Resume = resume
        };

        EvolutionRunResult<ProgramGenome> result;
        using (var tracer = new EvolutionTraceObserver<ProgramGenome>(new EvolutionTraceOptions { Enabled = true, Path = tracePath }, run.RunId, descriptors))
        {
            var engine = new EvolutionEngine<ProgramGenome>(task, variation, _ => new MapElitesArchive<ProgramGenome>(descriptors), options,
                observer: tracer, checkpointStore: checkpoints, genomeCodec: codec);
            interrupt.Attach(engine.RequestStop);
            try
            {
                result = engine.RunAsync(seeds, cancellationToken).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                error.WriteLine("aborted: the final checkpoint was written; continue with: aidotnet-evolve resume " + runFilePath);
                return AbortedExitCode;
            }
        }

        EvolutionArchiveEntry<ProgramGenome>? best = result.Best;
        string? repertoirePath = ExportRepertoire(result, task, codec, scope, run, tracePath, cancellationToken);
        output.WriteLine(JsonSerializer.Serialize(new
        {
            RunId = run.RunId,
            Resumed = resume,
            StopReason = result.StopReason.ToString(),
            result.Counters.CompletedEvaluations,
            BestQuality = best?.Evaluation.Quality,
            Trace = tracePath,
            Checkpoints = Path.Combine(outputDirectory, "checkpoints"),
            Repertoire = repertoirePath,
            WarmStart = warmStart,
            ModelUsage = variation.GetUsage()
        }, Program.Json));
        if (best is not null)
        {
            string bestPath = Path.Combine(outputDirectory, "best" + Extension(run.Language));
            File.WriteAllText(bestPath, best.Candidate.CanonicalGenome.Genome.Source, new UTF8Encoding(false));
        }
        ProgramEvolutionLlmUsage usage = variation.GetUsage();
        if (usage.ChatCalls > 0 && usage.ProviderErrors >= usage.ChatCalls)
        {
            error.WriteLine("error: all " + usage.ChatCalls + " model calls failed; check model.endpoint, model.name and the API key. " +
                "Only the seed program was scored.");
            return ModelUnavailableExitCode;
        }
        return 0;
    }

    private const int MaxRepertoireBytes = 8 * 1024 * 1024;
    private const int MaxRepertoireSeeds = 256;

    /// <summary>The reuse identity of this run's programs. Facets the CLI cannot observe carry explicit versioned labels.</summary>
    private static EvolutionReuseScope Scope(ProgramEvolutionTask task, ProgramGenomeCodec codec, RunFile run) =>
        new(task.Id, task.VersionHash, task.EvaluatorVersionHash, codec.Id, codec.VersionHash,
            constraintsVersion: "evaluator-defined-v1", dataVersion: "evaluator-defined-v1", fidelityVersion: "single-fidelity-v1",
            compilerVersion: "not-applicable-v1", runtimeVersion: run.RuntimeVersion, hardwareVersion: "not-recorded-v1",
            correctnessPolicyVersion: "evaluator-defined-v1");

    /// <summary>Writes the session's archive elites, best first, as <c>repertoire-NNN.json</c> beside its trace.</summary>
    /// <remarks>No evaluator or model call is made. The prior cost is the run's evaluation attempts so far.</remarks>
    private static string? ExportRepertoire(EvolutionRunResult<ProgramGenome> result, ProgramEvolutionTask task, ProgramGenomeCodec codec,
        EvolutionReuseScope scope, RunFile run, string tracePath, CancellationToken cancellationToken)
    {
        bool minimize = run.Direction == EvolutionOptimizationDirection.Minimize;
        IEnumerable<EvolutionArchiveEntry<ProgramGenome>> entries = result.Islands.SelectMany(island => island.Entries);
        ProgramGenome[] elites = (minimize ? entries.OrderBy(e => e.Evaluation.Quality) : entries.OrderByDescending(e => e.Evaluation.Quality))
            .Take(MaxRepertoireSeeds).Select(e => e.Candidate.CanonicalGenome.Genome).ToArray();
        if (elites.Length == 0) return null;
        string evidence = File.Exists(tracePath)
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(tracePath))).ToLowerInvariant()
            : throw new InvalidDataException("The session trace is missing, so the repertoire has no evidence to cite: " + tracePath);
        var provenance = new EvolutionRepertoireProvenance(run.RunId, result.StateHash, evidence, DateTimeOffset.UtcNow,
            result.Counters.EvaluationAttempts, "evaluation-attempts-v1");
        EvolutionRepertoire repertoire = EvolutionRepertoire.ExportAsync(elites, task, codec, scope, provenance, cancellationToken).GetAwaiter().GetResult();
        string path = Path.Combine(Path.GetDirectoryName(tracePath) ?? ".",
            "repertoire-" + Path.GetFileNameWithoutExtension(tracePath).Substring("trace-".Length) + ".json");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            writer.Write(repertoire.ToJson());
        return path;
    }

    internal static RunFile Load(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("Run file not found: " + path, path);
        if (info.Length > MaxRunFileBytes) throw new InvalidDataException("Run file exceeds " + MaxRunFileBytes + " bytes.");
        RunFile run = JsonSerializer.Deserialize<RunFile>(File.ReadAllBytes(path), RunJson)
            ?? throw new InvalidDataException("Run file is empty.");
        if (run.Schema != RunFile.CurrentSchema)
            throw new InvalidDataException("Run file schema must be '" + RunFile.CurrentSchema + "', not '" + run.Schema + "'.");
        if (string.IsNullOrWhiteSpace(run.RunId)) throw new InvalidDataException("runId is required.");
        if (run.Budget.MaxEvaluations < 1) throw new InvalidDataException("budget.maxEvaluations must be at least 1.");
        if (run.Budget.Parallelism < 1) throw new InvalidDataException("budget.parallelism must be at least 1.");
        if (run.Budget.EvaluationTimeLimitSeconds < 1) throw new InvalidDataException("budget.evaluationTimeLimitSeconds must be at least 1.");
        if (run.Budget.MaxProgramChars < 1) throw new InvalidDataException("budget.maxProgramChars must be at least 1.");
        if (run.Model.TimeoutSeconds < 1) throw new InvalidDataException("model.timeoutSeconds must be at least 1.");
        if (!Uri.TryCreate(run.Model.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https"))
            throw new InvalidDataException("model.endpoint must be an absolute http(s) URL.");
        if (endpoint.Scheme == "http" && !endpoint.IsLoopback)
            throw new InvalidDataException("model.endpoint must use https unless it is a loopback address; an API key is never sent in clear text.");
        return run;
    }

    private static string ReadBounded(string path, int maximumChars)
    {
        string text = File.ReadAllText(path, new UTF8Encoding(false, true));
        if (text.Length > maximumChars) throw new InvalidDataException(path + " exceeds " + maximumChars + " characters.");
        return text;
    }

    private static string NextTracePath(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        for (int session = 0; session < 1000; session++)
        {
            string path = Path.Combine(outputDirectory, "trace-" + session.ToString("000", CultureInfo.InvariantCulture) + ".jsonl");
            if (!File.Exists(path)) return path;
        }
        throw new InvalidDataException(outputDirectory + " already holds 1000 trace sessions.");
    }

    private static string Extension(ProgramLanguage language) => language switch
    {
        ProgramLanguage.Python => ".py",
        ProgramLanguage.JavaScript => ".js",
        ProgramLanguage.CSharp => ".cs",
        _ => ".txt"
    };
}

/// <summary>Turns interrupts into a graceful stop first and an abort second.</summary>
internal sealed class RunInterrupt : IDisposable
{
    private readonly CancellationTokenSource _abort = new();
    private Action? _stop;
    private int _presses;

    public CancellationToken Token => _abort.Token;

    /// <summary>Supplies the graceful stop once the engine exists; a press before then aborts.</summary>
    public void Attach(Action stop) => Volatile.Write(ref _stop, stop);

    public void Press()
    {
        Action? stop = Volatile.Read(ref _stop);
        if (Interlocked.Increment(ref _presses) == 1 && stop is not null) stop();
        else _abort.Cancel();
    }

    public void Dispose() => _abort.Dispose();
}

/// <summary>A minimal OpenAI-compatible <c>/chat/completions</c> client for the CLI.</summary>
internal sealed class OpenAiCompatibleChatClient : IProgramChatClient, IDisposable
{
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _completions;

    public OpenAiCompatibleChatClient(RunModel model)
    {
        ModelId = model.Name;
        string root = model.Endpoint.EndsWith('/') ? model.Endpoint : model.Endpoint + "/";
        _completions = new Uri(new Uri(root, UriKind.Absolute), "chat/completions");
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(model.TimeoutSeconds),
            MaxResponseContentBufferSize = MaxResponseBytes
        };
        if (model.ApiKeyEnvironmentVariable is { } variable)
        {
            string key = Environment.GetEnvironmentVariable(variable) ?? "";
            if (key.Length == 0) throw new InvalidDataException("Environment variable " + variable + " (model.apiKeyEnvironmentVariable) is not set.");
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    public string ModelId { get; }

    public async Task<ProgramChatResponse> GetResponseAsync(IReadOnlyList<ProgramChatMessage> messages, ProgramChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (options?.ResponseFormat is ProgramChatResponseFormat.Json)
            throw new NotSupportedException("The CLI model client requests text completions only.");
        var body = new Dictionary<string, object>
        {
            ["model"] = ModelId,
            ["messages"] = messages.Select(message => new { role = message.Role.ToString().ToLowerInvariant(), content = message.Text }).ToArray()
        };
        if (options?.Temperature is { } temperature) body["temperature"] = temperature;
        if (options?.MaxOutputTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (options?.Seed is { } seed) body["seed"] = seed;

        using var content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(body));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using HttpResponseMessage response = await _http.PostAsync(_completions, content, cancellationToken).ConfigureAwait(false);
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException("Model endpoint returned " + (int)response.StatusCode + ".", null, response.StatusCode);

        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        string text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()
            ?? throw new InvalidDataException("Model response has no message content.");
        ProgramChatUsage? usage = null;
        if (root.TryGetProperty("usage", out JsonElement reported) && reported.ValueKind == JsonValueKind.Object &&
            reported.TryGetProperty("prompt_tokens", out JsonElement input) && reported.TryGetProperty("completion_tokens", out JsonElement generated))
        {
            usage = new ProgramChatUsage(input.GetInt32(), generated.GetInt32());
        }
        string? reportedModel = root.TryGetProperty("model", out JsonElement name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null;
        return new ProgramChatResponse(ProgramChatMessage.Assistant(text), usage, reportedModel);
    }

    public void Dispose() => _http.Dispose();
}
