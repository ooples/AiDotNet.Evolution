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
    /// <summary>Whole rewrites (the default) or diffs against the parent (OpenEvolve's <c>diff_based_evolution</c>).</summary>
    public ProgramEvolutionMode Mode { get; init; } = ProgramEvolutionMode.FullRewrite;
    /// <summary>Extra models sampled beside <see cref="Model"/> by weight (OpenEvolve's <c>llm.models</c>); empty for one model.</summary>
    public List<RunModel> AdditionalModels { get; init; } = new();
    /// <summary>The system prompt for proposals, or <c>null</c> for the built-in one (OpenEvolve's <c>prompt.system_message</c>).</summary>
    public string? SystemMessage { get; init; }
    /// <summary>Islands, migration, selection and stopping; defaults reproduce a single-island uniform search.</summary>
    public RunSearch Search { get; init; } = new();
    /// <summary>How an unmodified OpenEvolve evaluator is run; <c>null</c> when <see cref="Evaluator"/> follows this CLI's contract.</summary>
    public RunOpenEvolveEvaluator? OpenEvolveEvaluator { get; init; }
}

/// <summary>Search settings OpenEvolve exposes under <c>database</c>, <c>prompt</c> and the top level.</summary>
internal sealed class RunSearch
{
    public int Islands { get; init; } = 1;
    /// <summary>Generations between migrations; zero never migrates.</summary>
    public int MigrationInterval { get; init; }
    public double MigrationRate { get; init; } = 0.1;
    /// <summary>OpenEvolve's exploration/exploitation/elite parent ratios; all null keeps uniform parent selection.</summary>
    public double? ExplorationRatio { get; init; }
    public double? ExploitationRatio { get; init; }
    public double? EliteRatio { get; init; }
    /// <summary>Top programs shown in each prompt (OpenEvolve's <c>prompt.num_top_programs</c>).</summary>
    public int? TopPrograms { get; init; }
    /// <summary>Diverse programs shown in each prompt (OpenEvolve's <c>prompt.num_diverse_programs</c>).</summary>
    public int? DiversePrograms { get; init; }
    /// <summary>Evaluator metrics used as archive axes, with their bin counts; empty keeps program length alone.</summary>
    public List<RunMetricDescriptor> MetricDescriptors { get; init; } = new();
    /// <summary>Evaluations without improvement before stopping; null never stops early.</summary>
    public long? EarlyStoppingPatience { get; init; }
    public double EarlyStoppingMinimumImprovement { get; init; }
    /// <summary>Retries for an evaluation that failed or timed out (OpenEvolve's <c>evaluator.max_retries</c>).</summary>
    public int EvaluationRetries { get; init; }
    /// <summary>A memory cap per evaluation in MiB, enforced by the sandbox; null for none.</summary>
    public int? EvaluationMemoryLimitMb { get; init; }
    /// <summary>Whether evaluator artifacts are fed into later prompts (OpenEvolve's <c>prompt.include_artifacts</c>).</summary>
    public bool IncludeArtifacts { get; init; } = true;
    public int? MaxArtifactBytes { get; init; }
    /// <summary>Whether evaluator artifacts are collected at all (OpenEvolve's <c>evaluator.enable_artifacts</c>).</summary>
    public bool CollectArtifacts { get; init; } = true;
    /// <summary>The most elites the archive holds (OpenEvolve's <c>database.population_size</c>); zero means the whole grid.</summary>
    public int ArchiveCapacity { get; init; }
    /// <summary>The global elite index size programs are drawn from (OpenEvolve's <c>database.archive_size</c>); zero for none.</summary>
    public int EliteArchiveSize { get; init; }
}

internal sealed class RunMetricDescriptor
{
    public required string Name { get; init; }
    public double Minimum { get; init; }
    public double Maximum { get; init; } = 1;
    public int Bins { get; init; } = 10;
}

/// <summary>Runs an OpenEvolve evaluator (<c>evaluate(program_path)</c> or cascade stages) through a Python shim.</summary>
internal sealed class RunOpenEvolveEvaluator
{
    /// <summary>The Python interpreter; <c>python</c> on PATH when null.</summary>
    public string? Python { get; init; }
    public bool Cascade { get; init; }
    public List<double> CascadeThresholds { get; init; } = new();
    public string FileSuffix { get; init; } = ".py";
    public int TimeoutSeconds { get; init; } = 300;
    /// <summary>Metrics excluded from the fitness average, as OpenEvolve excludes its feature dimensions.</summary>
    public List<string> FeatureDimensions { get; init; } = new();
}

/// <summary>How the CLI reaches the model (OpenEvolve's <c>provider</c> plus its manual mode).</summary>
internal enum ModelProvider
{
    /// <summary>Any OpenAI-compatible <c>chat/completions</c> endpoint.</summary>
    OpenAiCompatible,
    /// <summary>The Claude Code CLI and its login session; no API key.</summary>
    ClaudeCode,
    /// <summary>A person answers each prompt through a queue directory.</summary>
    Manual
}

internal sealed class RunModel
{
    public ModelProvider Provider { get; init; } = ModelProvider.OpenAiCompatible;
    /// <summary>An OpenAI-compatible base URL, for example <c>http://127.0.0.1:8000/v1</c>; required for that provider.</summary>
    public string? Endpoint { get; init; }
    public required string Name { get; init; }
    /// <summary>The environment variable holding the API key; null for an endpoint that needs none.</summary>
    public string? ApiKeyEnvironmentVariable { get; init; }
    public double? Temperature { get; init; }
    public double? TopP { get; init; }
    public ProgramReasoningEffort? ReasoningEffort { get; init; }
    public int? MaxOutputTokens { get; init; }
    public int TimeoutSeconds { get; init; } = 300;
    /// <summary>Retries for throttled, failed or timed-out calls (OpenEvolve's <c>retries</c>).</summary>
    public int MaxRetries { get; init; } = 3;
    /// <summary>Seconds before the first retry; each further retry doubles it (OpenEvolve's <c>retry_delay</c>).</summary>
    public int RetryDelaySeconds { get; init; } = 5;
    /// <summary>Per-call spending cap for the Claude Code provider (OpenEvolve's <c>max_budget_usd</c>).</summary>
    public decimal? MaxBudgetUsd { get; init; }
    /// <summary>The Claude Code executable; <c>claude</c> on PATH when null.</summary>
    public string? ClaudeExecutable { get; init; }
    /// <summary>The manual provider's queue directory, relative to the run file.</summary>
    public string? ManualQueue { get; init; }
    public int ManualTimeoutSeconds { get; init; } = 3600;
    /// <summary>Relative sampling weight when several models are configured (OpenEvolve's <c>weight</c>).</summary>
    public double Weight { get; init; } = 1;
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

    /// <summary>Writes run files in the form <see cref="Load"/> reads, for the OpenEvolve importer.</summary>
    internal static readonly JsonSerializerOptions RunFileJson = new(RunJson) { WriteIndented = true };

    /// <summary>Exit code for a run aborted by a second interrupt; its final checkpoint was still written.</summary>
    public const int AbortedExitCode = 130;

    /// <summary>Exit code for a run in which the model was called and every call failed: a configuration error, not a result.</summary>
    public const int ModelUnavailableExitCode = 3;

    public static int Execute(string runFilePath, bool resume, TextWriter output, TextWriter error, RunInterrupt interrupt)
    {
        try { return ExecuteCore(runFilePath, resume, output, error, interrupt); }
        catch (OperationCanceledException) when (interrupt.Token.IsCancellationRequested)
        {
            // Interrupted during setup (checkpoint lookup, warm-start import), before the engine could take a graceful stop.
            error.WriteLine("aborted before the run started; nothing was evaluated.");
            return AbortedExitCode;
        }
    }

    private static int ExecuteCore(string runFilePath, bool resume, TextWriter output, TextWriter error, RunInterrupt interrupt)
    {
        CancellationToken cancellationToken = interrupt.Token;
        string fullPath = Path.GetFullPath(runFilePath);
        RunFile run = Load(fullPath);
        string baseDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        string outputDirectory = Path.GetFullPath(Path.Combine(baseDirectory, run.Output));
        var checkpoints = new DirectoryEvolutionCheckpointStore(Path.Combine(outputDirectory, "checkpoints"));

        RunMarker.EnsureAvailable(outputDirectory); // before NextTracePath creates anything there
        bool exists = checkpoints.LoadLatestAsync(run.RunId, cancellationToken).GetAwaiter().GetResult() is not null;
        if (resume && !exists)
            throw new InvalidDataException("Nothing to resume: " + outputDirectory + " holds no checkpoint for run '" + run.RunId + "'.");
        if (!resume && exists)
            throw new InvalidDataException(outputDirectory + " already holds run '" + run.RunId + "'; use resume, or choose another output. Runs never overwrite.");

        string initial = ReadBounded(Path.Combine(baseDirectory, run.InitialProgram), run.Budget.MaxProgramChars);
        string evaluator = LoadEvaluator(run, baseDirectory);
        string tracePath = NextTracePath(outputDirectory);
        using var marker = RunMarker.Create(outputDirectory, run.RunId, tracePath);

        using var execution = CreateExecution(run);
        IProgramChatClient model = run.AdditionalModels.Count == 0
            ? CreateModel(run.Model, baseDirectory)
            : new WeightedEnsembleChatClient(new[] { run.Model }.Concat(run.AdditionalModels)
                .Select(member => new WeightedChatModel(CreateModel(member, baseDirectory), member.Weight)).ToList());
        using var ownedModel = model as IDisposable;

        var programOptions = new ProgramProposalOptions
        {
            Language = run.Language,
            TaskDescription = run.TaskDescription,
            MaxProgramChars = run.Budget.MaxProgramChars
        };
        foreach (RunMetricDescriptor metric in run.Search.MetricDescriptors.Where(metric => metric.Name != "length"))
            programOptions.MetricDescriptors.Add(metric.Name);
        var fitness = CreateFitness(run, execution, evaluator);
        var task = new ProgramEvolutionTask(fitness, new ProgramDescriptorSet(new[] { new ProgramLengthDescriptor() }), programOptions);
        var variationOptions = new LlmProgramVariationOptions
        {
            Mode = run.Mode,
            SystemMessage = run.SystemMessage,
            Temperature = run.Model.Temperature,
            TopP = run.Model.TopP,
            ReasoningEffort = run.Model.ReasoningEffort,
            MaxOutputTokens = run.Model.MaxOutputTokens
        };
        if (run.Search.TopPrograms is int topPrograms) variationOptions.MaxTopPrograms = topPrograms;
        var variation = new LlmProgramVariationOperator(model, programOptions, variationOptions);
        // Program length stands for OpenEvolve's built-in "complexity" axis. Configured descriptors replace it, as
        // OpenEvolve's feature_dimensions replace its defaults; "length" among them keeps it.
        EvolutionDescriptorDefinition[] descriptors = run.Search.MetricDescriptors.Count == 0
            ? new[] { new EvolutionDescriptorDefinition("length", 0, run.Budget.MaxProgramChars, 64) }
            : run.Search.MetricDescriptors.Select(metric => metric.Name == "length"
                ? new EvolutionDescriptorDefinition("length", 0, run.Budget.MaxProgramChars, metric.Bins)
                : new EvolutionDescriptorDefinition(metric.Name, metric.Minimum, metric.Maximum, metric.Bins,
                    EvolutionOutOfRangePolicy.Clamp)).ToArray();
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
            Resume = resume,
            IslandCount = run.Search.Islands,
            MigrationInterval = run.Search.Islands > 1 ? run.Search.MigrationInterval : 0,
            MigrationRate = run.Search.MigrationRate,
            MaxRetries = run.Search.EvaluationRetries
        };
        if (run.Search.ExplorationRatio is not null || run.Search.ExploitationRatio is not null || run.Search.EliteRatio is not null)
        {
            options.SelectionPolicy = EvolutionSelectionPolicyKind.Ratio;
            if (run.Search.ExplorationRatio is double exploration) options.Selection.ExplorationRatio = exploration;
            if (run.Search.ExploitationRatio is double exploitation) options.Selection.ExploitationRatio = exploitation;
            if (run.Search.EliteRatio is double elite) options.Selection.EliteRatio = elite;
        }
        if (run.Search.DiversePrograms is int diverse) options.Selection.DiverseInspirationCount = diverse;
        if (run.Search.EarlyStoppingPatience is long patience)
        {
            options.EarlyStopping.PatienceEvaluations = patience;
            options.EarlyStopping.MinimumImprovement = run.Search.EarlyStoppingMinimumImprovement;
        }
        options.Artifacts.Enabled = run.Search.CollectArtifacts;
        options.Artifacts.DeliverToNextProposal = run.Search.IncludeArtifacts;
        options.GlobalEliteCount = run.Search.EliteArchiveSize;
        if (run.Search.MaxArtifactBytes is int artifactBytes) options.Artifacts.MaxArtifactBytes = artifactBytes;

        EvolutionRunResult<ProgramGenome> result;
        using (var tracer = new EvolutionTraceObserver<ProgramGenome>(new EvolutionTraceOptions { Enabled = true, Path = tracePath }, run.RunId, descriptors))
        {
            var engine = new EvolutionEngine<ProgramGenome>(task, variation, _ => new MapElitesArchive<ProgramGenome>(descriptors, capacity: run.Search.ArchiveCapacity), options,
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

    /// <summary>
    /// Checks everything a run needs before it spends a model call: the run file, the checkpoint state, a writable
    /// output directory, the seed and evaluator files, and that the seed actually passes the evaluator.
    /// </summary>
    /// <remarks>
    /// A seed the evaluator rejects would make every later proposal compete against nothing, and a run that finds
    /// that out after its first model call has already paid for it. Preflight scores the seed exactly as run
    /// would -- the same sandbox, limits and evaluator -- and never contacts the model.
    /// </remarks>
    public static int Preflight(string runFilePath, TextWriter output, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(runFilePath);
        RunFile run = Load(fullPath);
        string baseDirectory = Path.GetDirectoryName(fullPath) ?? ".";
        string outputDirectory = Path.GetFullPath(Path.Combine(baseDirectory, run.Output));
        // Looked up only when the directory exists: opening the store creates it, and preflight starts nothing.
        string checkpointDirectory = Path.Combine(outputDirectory, "checkpoints");
        bool hasCheckpoint = Directory.Exists(checkpointDirectory) && new DirectoryEvolutionCheckpointStore(checkpointDirectory).LoadLatestAsync(run.RunId, cancellationToken).GetAwaiter().GetResult() is not null;

        // run would fail on this before its first model call; preflight promises to find it first.
        if (run.Model.ApiKeyEnvironmentVariable is { } variable && string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
            throw new InvalidDataException(MissingKeyMessage(variable));
        string initial = ReadBounded(Path.Combine(baseDirectory, run.InitialProgram), run.Budget.MaxProgramChars);
        string evaluator = LoadEvaluator(run, baseDirectory);
        ProbeWritable(outputDirectory);

        EvolutionTaskResult seed;
        using (var execution = CreateExecution(run))
        {
            var fitness = CreateFitness(run, execution, evaluator);
            seed = fitness.EvaluateAsync(new ProgramGenome(initial, run.Language),
                new EvolutionEvaluationContext(0, run.Budget.Seed, 0, 1), cancellationToken).AsTask().GetAwaiter().GetResult();
        }

        bool passed = seed.Status == EvolutionEvaluationStatus.Completed && seed.Quality is not null;
        output.WriteLine(JsonSerializer.Serialize(new
        {
            RunId = run.RunId,
            Passed = passed,
            // What the next command must be: a fresh run refuses an existing checkpoint and resume refuses a missing one.
            Next = hasCheckpoint ? "resume" : "run",
            Output = outputDirectory,
            SeedStatus = seed.Status.ToString(),
            SeedQuality = seed.Quality,
            SeedDiagnostics = seed.Diagnostics,
            ModelCalls = 0
        }, Program.Json));
        return passed ? 0 : PreflightFailedExitCode;
    }

    /// <summary>Returned when preflight finds the seed does not pass the evaluator.</summary>
    public const int PreflightFailedExitCode = 4;

    private static ProcessProgramExecutionEngine CreateExecution(RunFile run)
    {
        var sandbox = new ProgramSandboxOptions { RuntimeVersion = run.RuntimeVersion };
        sandbox.Limits.TimeLimitSeconds = run.Budget.EvaluationTimeLimitSeconds;
        sandbox.Limits.MaxConcurrentExecutions = run.Budget.Parallelism;
        if (run.Search.EvaluationMemoryLimitMb is int memory) sandbox.Limits.MemoryLimitMb = memory;
        if (run.OpenEvolveEvaluator is { } openEvolve)
        {
            // Up to three cascade stages each get the OpenEvolve timeout; the sandbox limit covers them all.
            int stages = openEvolve.Cascade ? 3 : 1;
            sandbox.Limits.TimeLimitSeconds = Math.Max(sandbox.Limits.TimeLimitSeconds, stages * openEvolve.TimeoutSeconds + 10);
            if (openEvolve.Python is { } python)
                sandbox.SetInterpreter(ProgramLanguage.Python, new ProgramInterpreterSpecification(python, "{source}"));
        }
        return new ProcessProgramExecutionEngine(sandbox);
    }

    /// <summary>The evaluator script to run: the file itself, or the OpenEvolve shim that imports it.</summary>
    private static string LoadEvaluator(RunFile run, string baseDirectory)
    {
        string path = Path.GetFullPath(Path.Combine(baseDirectory, run.Evaluator));
        string source = ReadBounded(path, MaxRunFileBytes); // bounds and existence, whichever form runs
        return run.OpenEvolveEvaluator is { } openEvolve ? OpenEvolveEvaluatorShim.Build(path, openEvolve) : source;
    }

    private static ScriptProgramFitnessEvaluator CreateFitness(RunFile run, ProcessProgramExecutionEngine execution, string evaluator)
        => new(execution, evaluator,
            new ScriptProgramEvaluationOptions { EvaluatorScriptLanguage = run.EvaluatorLanguage, Direction = run.Direction });

    /// <summary>Proves the output directory accepts a write, so a run cannot fail at its first checkpoint.</summary>
    private static void ProbeWritable(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        string probe = Path.Combine(outputDirectory, ".preflight-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, string.Empty);
        File.Delete(probe);
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
        // System.Text.Json does not enforce non-nullable required members, so an explicit null arrives here.
        if (run.Model is null) throw new InvalidDataException("model is required.");
        if (run.Budget is null) throw new InvalidDataException("budget is required.");
        if (run.Budget.MaxEvaluations < 1) throw new InvalidDataException("budget.maxEvaluations must be at least 1.");
        if (run.Budget.Parallelism < 1) throw new InvalidDataException("budget.parallelism must be at least 1.");
        if (run.Budget.EvaluationTimeLimitSeconds < 1) throw new InvalidDataException("budget.evaluationTimeLimitSeconds must be at least 1.");
        if (run.Budget.MaxProgramChars < 1) throw new InvalidDataException("budget.maxProgramChars must be at least 1.");
        if (run.Model.TimeoutSeconds < 1) throw new InvalidDataException("model.timeoutSeconds must be at least 1.");
        if (run.Model.MaxRetries is < 0 or > 20) throw new InvalidDataException("model.maxRetries must be between 0 and 20.");
        if (run.Model.RetryDelaySeconds < 0) throw new InvalidDataException("model.retryDelaySeconds cannot be negative.");
        if (run.Model.TopP is { } topP && (double.IsNaN(topP) || topP <= 0 || topP > 1)) throw new InvalidDataException("model.topP must be in (0, 1].");
        switch (run.Model.Provider)
        {
            case ModelProvider.OpenAiCompatible:
                if (!Uri.TryCreate(run.Model.Endpoint, UriKind.Absolute, out Uri? endpoint) || endpoint.Scheme is not ("http" or "https"))
                    throw new InvalidDataException("model.endpoint must be an absolute http(s) URL.");
                if (endpoint.Scheme == "http" && !endpoint.IsLoopback)
                    throw new InvalidDataException("model.endpoint must use https unless it is a loopback address; an API key is never sent in clear text.");
                break;
            case ModelProvider.ClaudeCode:
                if (run.Model.MaxBudgetUsd is <= 0) throw new InvalidDataException("model.maxBudgetUsd must be positive.");
                break;
            case ModelProvider.Manual:
                if (string.IsNullOrWhiteSpace(run.Model.ManualQueue)) throw new InvalidDataException("model.manualQueue is required for the manual provider.");
                if (run.Model.ManualTimeoutSeconds < 1) throw new InvalidDataException("model.manualTimeoutSeconds must be at least 1.");
                break;
            default:
                throw new InvalidDataException("model.provider is not supported.");
        }
        return run;
    }

    /// <summary>Builds the configured provider's client; the caller disposes it when it is disposable.</summary>
    internal static IProgramChatClient CreateModel(RunModel model, string baseDirectory)
    {
        switch (model.Provider)
        {
            case ModelProvider.ClaudeCode:
                return new ClaudeCodeChatClient(new ClaudeCodeChatClientOptions
                {
                    Executable = model.ClaudeExecutable ?? "claude",
                    Model = model.Name,
                    MaxBudgetUsd = model.MaxBudgetUsd,
                    Timeout = TimeSpan.FromSeconds(model.TimeoutSeconds),
                    MaxRetries = model.MaxRetries,
                    RetryDelay = TimeSpan.FromSeconds(model.RetryDelaySeconds)
                });
            case ModelProvider.Manual:
                return new ManualProgramChatClient(Path.Combine(baseDirectory, model.ManualQueue ?? "manual-queue"),
                    TimeSpan.FromSeconds(model.ManualTimeoutSeconds), modelId: model.Name);
            default:
                string? key = null;
                if (model.ApiKeyEnvironmentVariable is { } variable)
                {
                    key = Environment.GetEnvironmentVariable(variable);
                    if (string.IsNullOrEmpty(key)) throw new InvalidDataException(MissingKeyMessage(variable));
                }
                return new OpenAiCompatibleChatClient(new OpenAiCompatibleChatClientOptions
                {
                    Endpoint = new Uri(model.Endpoint ?? string.Empty, UriKind.Absolute),
                    Model = model.Name,
                    ApiKey = key,
                    Timeout = TimeSpan.FromSeconds(model.TimeoutSeconds),
                    MaxRetries = model.MaxRetries,
                    RetryDelay = TimeSpan.FromSeconds(model.RetryDelaySeconds)
                });
        }
    }

    internal static string MissingKeyMessage(string variable) =>
        "Environment variable " + variable + " (model.apiKeyEnvironmentVariable) is not set.";

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

    internal static string Extension(ProgramLanguage language) => language switch
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

/// <summary>Marks an output directory as holding a run in progress, for as long as the run process lives.</summary>
/// <remarks>
/// A trace flushed at every checkpoint is readable while it is written, but nothing in it says whether its writer is
/// still going: a finished run and a running one look alike. The marker names the trace and the process, and a
/// reader believes it only while that process is alive, so a run killed without cleanup never reads as running.
/// </remarks>
internal sealed class RunMarker : IDisposable
{
    internal const string FileName = "running.json";
    internal const string LockFileName = "running.lock";
    private readonly string _path;
    private readonly FileStream _lock;

    private RunMarker(string path, FileStream held) { _path = path; _lock = held; }

    /// <summary>Claims the output directory for this process, refusing one a live run or resume already holds.</summary>
    /// <remarks>
    /// The claim is an exclusive OS lock on <c>running.lock</c>, held for the whole run: the kernel releases it if the
    /// process dies, so a crash leaves nothing stale and a reused process id cannot block the directory. The JSON marker
    /// only tells readers which trace is live.
    /// </remarks>
    public static RunMarker Create(string outputDirectory, string runId, string tracePath)
    {
        Directory.CreateDirectory(outputDirectory);
        FileStream held;
        try
        {
            held = new FileStream(Path.Combine(outputDirectory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw InUse(outputDirectory);
        }
        try
        {
            string path = Path.Combine(outputDirectory, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(new Entry(runId, Environment.ProcessId, Path.GetFileName(tracePath), DateTimeOffset.UtcNow)));
            return new RunMarker(path, held);
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>Refuses to start when a live run or resume holds the directory; checkpoints and traces must have one writer.</summary>
    public static void EnsureAvailable(string outputDirectory)
    {
        string lockPath = Path.Combine(outputDirectory, LockFileName);
        if (!File.Exists(lockPath)) return;
        try { using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw InUse(outputDirectory); }
    }

    private static InvalidDataException InUse(string outputDirectory)
    {
        int? processId = Read(Path.Combine(outputDirectory, FileName))?.ProcessId;
        return new InvalidDataException(outputDirectory + " is in use by process " +
            (processId?.ToString(CultureInfo.InvariantCulture) ?? "unknown") + "; wait for that run or resume to finish.");
    }

    public static bool IsRunning(string tracePath)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(tracePath)) ?? ".", FileName);
        Entry? entry = Read(path);
        if (entry is null || !string.Equals(entry.Trace, Path.GetFileName(tracePath), StringComparison.Ordinal)) return false;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(entry.ProcessId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; } // no such process: the run ended without removing its marker
    }

    private static Entry? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path)); }
        catch (Exception exception) when (exception is IOException or JsonException) { return null; }
    }

    public void Dispose()
    {
        try { File.Delete(_path); }
        catch (IOException) { /* a stale marker is harmless: the lock, not the marker, decides who holds the directory */ }
        _lock.Dispose();
    }

    private sealed record Entry(string RunId, int ProcessId, string Trace, DateTimeOffset StartedUtc);
}
