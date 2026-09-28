using System.Globalization;
using System.Reflection;
using System.Text.Json;
using AiDotNet.Evolution.Programs;
using YamlDotNet.RepresentationModel;

namespace AiDotNet.Evolution.Cli;

internal sealed record OpenEvolveImportEntry(string Key, string Value, OpenEvolveKeyDisposition Disposition, string Ours, bool FromConfig);

internal sealed record OpenEvolveImport(RunFile Run, IReadOnlyList<OpenEvolveImportEntry> Entries, IReadOnlyList<string> Notes);

/// <summary>
/// Translates an OpenEvolve 0.3.2 <c>config.yaml</c> into a run file. The config is laid over OpenEvolve's own defaults,
/// so the run behaves as OpenEvolve would with the same file. Every key is reported. A key OpenEvolve does not define
/// is refused, and so is a value we cannot reproduce: unless it equals OpenEvolve's default, it is refused by name.
/// </summary>
internal static class OpenEvolveConfigImporter
{
    private const string DefaultsResource = "AiDotNet.Evolution.Cli.OpenEvolve.defaults.json";
    // OpenEvolve sets no memory limit, but the sandbox always sets one. This is generous enough for numpy/scipy evaluators.
    private const int ImportedMemoryLimitMb = 4096;
    // The fields one entry of llm.models may carry: OpenEvolve's LLMModelConfig, which the top-level llm keys default.
    private static readonly HashSet<string> ModelFields = new(StringComparer.Ordinal)
    {
        "api_base", "api_key", "name", "provider", "init_client", "weight", "system_message", "temperature", "top_p",
        "max_tokens", "timeout", "retries", "retry_delay", "random_seed", "reasoning_effort", "max_budget_usd", "manual_mode",
        "_manual_queue_dir"
    };

    public static OpenEvolveImport Import(string configPath, string initialProgram, string evaluator, int? iterations, string? python)
    {
        Dictionary<string, object?> config = Flatten(Load(configPath, python));
        Dictionary<string, object?> defaults = Defaults();
        var merged = new Dictionary<string, object?>(defaults, StringComparer.Ordinal);
        var errors = new List<string>();
        var unknown = new List<string>();
        foreach ((string key, object? value) in config)
        {
            OpenEvolveKey? known = OpenEvolveConfigCatalog.Find(key);
            if (known is null)
            {
                // OpenEvolve builds its config with dacite, which drops keys it does not define, so these change nothing
                // there either. They are named rather than dropped silently, since one may be a misspelt real key.
                unknown.Add(key);
                continue;
            }
            if (known.Disposition == OpenEvolveKeyDisposition.RefusedUnlessDefault && !Same(value, defaults[key]))
                errors.Add(key + " = " + Show(value) + ": not supported (" + known.Ours + ")");
            merged[key] = value;
        }
        var notes = new List<string>();
        foreach (string key in unknown)
            notes.Add(key + " is not an OpenEvolve 0.3.2 config key; OpenEvolve ignores it, and so does this import.");
        if (!config.ContainsKey("llm.api_base") && Environment.GetEnvironmentVariable("OPENAI_API_BASE") is { Length: > 0 } apiBase)
        {
            merged["llm.api_base"] = apiBase; // OpenEvolve's own fallback when the config leaves api_base unset
            notes.Add("llm.api_base comes from OPENAI_API_BASE: " + apiBase);
        }
        if (config.TryGetValue("llm.api_key", out object? configuredKey) && configuredKey is not null)
            notes.Add("llm.api_key in the config is not used; the key is read from OPENAI_API_KEY.");
        List<RunModel> models = Models(merged, defaults, errors);
        foreach (object? judge in List(merged["llm.evaluator_models"]))
        {
            if (judge is not Dictionary<string, object?> fields) errors.Add("llm.evaluator_models: every entry must be a mapping");
            else foreach (string field in fields.Keys.Where(field => !ModelFields.Contains(field)))
                errors.Add("llm.evaluator_models[].'" + field + "': not an OpenEvolve 0.3.2 model field");
        }
        if (errors.Count > 0)
            throw new InvalidDataException("The OpenEvolve config cannot be imported:" + Environment.NewLine + "  " +
                                           string.Join(Environment.NewLine + "  ", errors));

        long seed = Integer(merged["random_seed"]) ?? 42;
        if (merged["random_seed"] is null) notes.Add("random_seed is null (an unseeded OpenEvolve run); this run uses seed 42.");
        int maxIterations = iterations ?? (int)(Integer(merged["max_iterations"]) ?? 10000);
        if (maxIterations < 1) throw new InvalidDataException("max_iterations must be at least 1.");
        List<RunMetricDescriptor> descriptors = Descriptors(merged, notes);
        long configuredTimeout = Integer(merged["evaluator.timeout"]) ?? 300;
        // OpenEvolve has no upper bound; the sandbox refuses a wall clock past a day. Cap it and say so.
        int stageCount = Boolean(merged["evaluator.cascade_evaluation"]) ? 3 : 1;
        int timeout = (int)Math.Clamp(configuredTimeout, 1, (ProgramSandboxLimitOptions.MaxTimeLimitSeconds - 10) / stageCount);
        if (timeout != configuredTimeout)
            notes.Add($"evaluator.timeout = {configuredTimeout} s: OpenEvolve sets no ceiling, but the sandbox limits each " +
                      $"evaluation to {ProgramSandboxLimitOptions.MaxTimeLimitSeconds} s, so each stage runs for at most {timeout} s.");
        int? memory = (int?)Integer(merged["evaluator.memory_limit_mb"]);
        if (memory is null) notes.Add("evaluator.memory_limit_mb is null: OpenEvolve sets no limit, the sandbox caps each evaluation at " +
                                      ImportedMemoryLimitMb + " MiB.");
        double exploration = Number(merged["database.exploration_ratio"]) ?? 0.2;
        double exploitation = Number(merged["database.exploitation_ratio"]) ?? 0.7;
        if (exploration < 0 || exploitation < 0 || exploration + exploitation > 1 + 1e-9)
            throw new InvalidDataException("database.exploration_ratio + exploitation_ratio must be between 0 and 1.");
        int inspirationCount = (int)(Integer(merged["prompt.num_diverse_programs"]) ?? 2);
        int topInspirations = Math.Max(1, (int)(inspirationCount * (Number(merged["database.elite_selection_ratio"]) ?? 0.1)));
        string? language = merged["language"] as string;

        var run = new RunFile
        {
            Schema = RunFile.CurrentSchema,
            RunId = "openevolve-" + Path.GetFileNameWithoutExtension(configPath),
            InitialProgram = Path.GetFullPath(initialProgram),
            Evaluator = Path.GetFullPath(evaluator),
            Output = ".",
            Model = models[0],
            AdditionalModels = models.Skip(1).ToList(),
            Language = language is null ? ProgramLanguage.Python : ParseLanguage(language, notes),
            Mode = Boolean(merged["diff_based_evolution"]) ? ProgramEvolutionMode.Diff : ProgramEvolutionMode.FullRewrite,
            Prompt = Prompt(merged),
            LlmFeedback = Boolean(merged["evaluator.use_llm_feedback"])
                ? new RunLlmFeedback
                {
                    Weight = Number(merged["evaluator.llm_feedback_weight"]) ?? 0.1,
                    // OpenEvolve judges with the proposal models when llm.evaluator_models is empty.
                    Models = List(merged["llm.evaluator_models"]).OfType<Dictionary<string, object?>>()
                        .Select(judge => ToModel(Resolve(judge, merged))).ToList()
                }
                : null,
            Budget = new RunBudget
            {
                MaxEvaluations = checked(maxIterations + 1),
                Seed = (ulong)seed,
                Parallelism = Math.Max(1, (int)(Integer(merged["evaluator.parallel_evaluations"]) ?? 1)),
                EvaluationTimeLimitSeconds = Math.Max(1, timeout),
                MaxProgramChars = (int)(Integer(merged["max_code_length"]) ?? 10000)
            },
            Search = new RunSearch
            {
                Islands = Math.Max(1, (int)(Integer(merged["database.num_islands"]) ?? 1)),
                MigrationInterval = (int)(Integer(merged["database.migration_interval"]) ?? 0),
                MigrationRate = Number(merged["database.migration_rate"]) ?? 0.1,
                // OpenEvolve draws the parent by exploration, then exploitation, then a remainder branch; ours has the same
                // three branches with the remainder as the third, and its defaults (0.2/0.7/0.1) are OpenEvolve's.
                ExplorationRatio = exploration,
                ExploitationRatio = exploitation,
                EliteRatio = 1 - exploration - exploitation,
                TopPrograms = (int?)Integer(merged["prompt.num_top_programs"]),
                // OpenEvolve's n inspirations: the island best, then max(1, int(n x elite_selection_ratio)) top programs,
                // then diverse ones filling the rest of n.
                TopInspirations = topInspirations,
                DiversePrograms = Math.Max(0, inspirationCount - 1 - topInspirations),
                MetricDescriptors = descriptors,
                EarlyStoppingPatience = Integer(merged["early_stopping_patience"]),
                EarlyStoppingMinimumImprovement = Number(merged["convergence_threshold"]) ?? 0,
                EvaluationRetries = (int)(Integer(merged["evaluator.max_retries"]) ?? 0),
                EvaluationMemoryLimitMb = memory ?? ImportedMemoryLimitMb,
                IncludeArtifacts = Boolean(merged["prompt.include_artifacts"]),
                MaxArtifactBytes = (int?)Integer(merged["prompt.max_artifact_bytes"]),
                CollectArtifacts = Boolean(merged["evaluator.enable_artifacts"]),
                ArchiveCapacity = (int)(Integer(merged["database.population_size"]) ?? 0),
                EliteArchiveSize = (int)(Integer(merged["database.archive_size"]) ?? 0)
            },
            OpenEvolveEvaluator = new RunOpenEvolveEvaluator
            {
                Python = python,
                Cascade = Boolean(merged["evaluator.cascade_evaluation"]),
                CascadeThresholds = List(merged["evaluator.cascade_thresholds"]).Select(value => Number(value) ?? 0).ToList(),
                FileSuffix = merged["file_suffix"] as string ?? ".py",
                TimeoutSeconds = Math.Max(1, timeout),
                FeatureDimensions = List(merged["database.feature_dimensions"]).OfType<string>().ToList()
            }
        };

        var entries = OpenEvolveConfigCatalog.Keys
            .Select(key => new OpenEvolveImportEntry(key.Name, Show(merged[key.Name]), key.Disposition, key.Ours, config.ContainsKey(key.Name)))
            .ToList();
        return new OpenEvolveImport(run, entries, notes);
    }

    // OpenEvolve's shipped template names (openevolve/prompts/defaults at 411fb59). OpenEvolve treats a system message as
    // a template when a template of that name exists and as literal text otherwise; the import records that guess.
    private static readonly HashSet<string> OpenEvolveTemplates = new(StringComparer.Ordinal)
    {
        "diff_user", "evaluation", "evaluator_system_message", "evolution_history", "full_rewrite_user", "inspiration_program",
        "inspirations_section", "previous_attempt", "system_message", "system_message_changes_description",
        "system_message_with_changes_description", "top_program", "user_message_with_changes_description"
    };

    private static RunPrompt Prompt(Dictionary<string, object?> merged)
    {
        string? templates = merged["prompt.template_dir"] as string;
        // OpenEvolve opens template_dir as given, so a relative path is relative to the working directory (its examples
        // write "examples/<name>/prompts" and run from the repository root), not to the config file.
        string? templateDirectory = templates is null ? null : Path.GetFullPath(templates);
        bool IsTemplate(string name) => OpenEvolveTemplates.Contains(name) ||
            (templateDirectory is not null && File.Exists(Path.Combine(templateDirectory, name + ".txt")));
        string? system = merged["prompt.system_message"] as string;
        string? evaluatorSystem = merged["prompt.evaluator_system_message"] as string;
        string? initial = merged["prompt.initial_changes_description"] as string;
        return new RunPrompt
        {
            TemplateDirectory = templateDirectory,
            // The default name stands for our built-in system message; any other value follows OpenEvolve's guess.
            SystemMessage = system is null or "system_message" ? null : system,
            SystemMessageMode = system is not null && IsTemplate(system)
                ? AiDotNet.Evolution.Programs.ProgramPromptSystemMessageMode.TemplateKey
                : AiDotNet.Evolution.Programs.ProgramPromptSystemMessageMode.Literal,
            EvaluatorSystemMessage = evaluatorSystem is null or "evaluator_system_message" ? null : evaluatorSystem,
            ProgramsAsChangesDescription = Boolean(merged["prompt.programs_as_changes_description"]),
            InitialChangesDescription = string.IsNullOrEmpty(initial) ? null : initial,
            // OpenEvolve strips it and uses it in place of its system_message_changes_description template.
            SystemMessageChangesDescription = (merged["prompt.system_message_changes_description"] as string)?.Trim() is { Length: > 0 } changes
                ? changes : null,
            NumTopPrograms = (int?)Integer(merged["prompt.num_top_programs"]),
            NumDiversePrograms = (int?)Integer(merged["prompt.num_diverse_programs"]),
            IncludeArtifacts = Boolean(merged["prompt.include_artifacts"]),
            MaxArtifactBytes = (int?)Integer(merged["prompt.max_artifact_bytes"]),
            ArtifactSecurityFilter = Boolean(merged["prompt.artifact_security_filter"]),
            UseTemplateStochasticity = Boolean(merged["prompt.use_template_stochasticity"]),
            TemplateVariations = merged["prompt.template_variations"] is Dictionary<string, object?> variations && variations.Count > 0
                ? variations.ToDictionary(pair => pair.Key, pair => List(pair.Value).Select(Show).ToList(), StringComparer.Ordinal)
                : null,
            // code_length_threshold is OpenEvolve's older name for the same threshold, used when the newer one is unset.
            SuggestSimplificationAfterChars = (int?)(Integer(merged["prompt.suggest_simplification_after_chars"]) ??
                                                     Integer(merged["prompt.code_length_threshold"])),
            IncludeChangesUnderChars = (int?)Integer(merged["prompt.include_changes_under_chars"]),
            ConciseImplementationMaxLines = (int?)Integer(merged["prompt.concise_implementation_max_lines"]),
            ComprehensiveImplementationMinLines = (int?)Integer(merged["prompt.comprehensive_implementation_min_lines"])
        };
    }

    // An llm.models or llm.evaluator_models entry over the shared llm defaults, as OpenEvolve resolves each model.
    private static Dictionary<string, object?> Resolve(Dictionary<string, object?> model, Dictionary<string, object?> merged)
    {
        var resolved = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string field in ModelFields) resolved[field] = merged["llm." + field];
        foreach ((string field, object? value) in model) resolved[field] = value;
        return resolved;
    }

    private static List<RunModel> Models(Dictionary<string, object?> merged, Dictionary<string, object?> defaults, List<string> errors)
    {
        var shared = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (string field in ModelFields) shared[field] = merged["llm." + field];
        var entries = new List<Dictionary<string, object?>>();
        foreach (object? item in List(merged["llm.models"]))
        {
            if (item is not Dictionary<string, object?> model)
            {
                errors.Add("llm.models: every entry must be a mapping");
                continue;
            }
            foreach (string field in model.Keys.Where(field => !ModelFields.Contains(field)))
                errors.Add("llm.models[].'" + field + "': not an OpenEvolve 0.3.2 model field");
            var resolved = new Dictionary<string, object?>(shared, StringComparer.Ordinal);
            foreach ((string field, object? value) in model)
            {
                resolved[field] = value;
                OpenEvolveKey? key = OpenEvolveConfigCatalog.Find("llm." + field);
                if (key?.Disposition == OpenEvolveKeyDisposition.RefusedUnlessDefault && !Same(value, defaults["llm." + field]))
                    errors.Add("llm.models[]." + field + " = " + Show(value) + ": not supported (" + key.Ours + ")");
            }
            entries.Add(resolved);
        }
        // OpenEvolve's older form, used when llm.models is empty.
        if (entries.Count == 0 && merged["llm.primary_model"] is string primary)
        {
            entries.Add(new(shared, StringComparer.Ordinal) { ["name"] = primary, ["weight"] = merged["llm.primary_model_weight"] ?? 1.0 });
            if (merged["llm.secondary_model"] is string secondary)
                entries.Add(new(shared, StringComparer.Ordinal) { ["name"] = secondary, ["weight"] = merged["llm.secondary_model_weight"] ?? 0.0 });
        }
        if (entries.Count == 0 && merged["llm.name"] is string name) entries.Add(new(shared, StringComparer.Ordinal) { ["name"] = name });
        if (entries.Count == 0)
        {
            errors.Add("llm: no model is configured (set llm.models, llm.primary_model or llm.name)");
            return new List<RunModel>();
        }
        return entries.Where(model => (Number(model["weight"]) ?? 1) > 0).Select(ToModel).ToList() is { Count: > 0 } weighted
            ? weighted
            : throw new InvalidDataException("llm: every configured model has weight 0.");
    }

    private static RunModel ToModel(Dictionary<string, object?> model)
    {
        bool manual = Boolean(model["manual_mode"]);
        return new RunModel
        {
            Provider = manual ? ModelProvider.Manual : Provider(model["provider"] as string),
            Endpoint = model["api_base"] as string,
            Name = model["name"] as string ?? throw new InvalidDataException("llm.models: every model needs a name."),
            ApiKeyEnvironmentVariable = manual || Provider(model["provider"] as string) == ModelProvider.ClaudeCode ? null : "OPENAI_API_KEY",
            Temperature = Number(model["temperature"]),
            TopP = Number(model["top_p"]),
            MaxOutputTokens = (int?)Integer(model["max_tokens"]),
            ReasoningEffort = model["reasoning_effort"] is string effort ? ParseEffort(effort) : null,
            TimeoutSeconds = Math.Max(1, (int)(Integer(model["timeout"]) ?? 60)),
            MaxRetries = (int)(Integer(model["retries"]) ?? 3),
            RetryDelaySeconds = (int)(Integer(model["retry_delay"]) ?? 5),
            MaxBudgetUsd = Number(model["max_budget_usd"]) is double budget ? (decimal)budget : null,
            ManualQueue = model["_manual_queue_dir"] as string,
            Weight = Number(model["weight"]) ?? 1
        };
    }

    private static List<RunMetricDescriptor> Descriptors(Dictionary<string, object?> merged, List<string> notes)
    {
        var descriptors = new List<RunMetricDescriptor>();
        object? bins = merged["database.feature_bins"];
        foreach (string dimension in List(merged["database.feature_dimensions"]).OfType<string>())
        {
            int count = (int)((bins is Dictionary<string, object?> perDimension && perDimension.TryGetValue(dimension, out object? own)
                ? Integer(own) : Integer(bins)) ?? 10);
            if (dimension == "diversity")
            {
                notes.Add("database.feature_dimensions: OpenEvolve's population-relative 'diversity' axis has no deterministic " +
                          "equivalent and is dropped.");
                continue;
            }
            descriptors.Add(new RunMetricDescriptor
            {
                // OpenEvolve's "complexity" is program length; "score" is the fitness itself; others are evaluator metrics.
                Name = dimension switch { "complexity" => "length", _ => dimension },
                Bins = Math.Max(1, count)
            });
        }
        return descriptors;
    }

    private static ModelProvider Provider(string? provider) => provider?.ToLowerInvariant() switch
    {
        null or "openai" => ModelProvider.OpenAiCompatible,
        "claude_code" or "claude-code" => ModelProvider.ClaudeCode,
        _ => throw new InvalidDataException("llm.provider = " + provider + ": not supported (openai or claude_code).")
    };

    // OpenEvolve's language is only a label for prompts and code fences; the evaluator decides how a candidate runs.
    // A language with no dedicated support (a prompt, "text", "markdown") is evolved as generic text, as there.
    private static ProgramLanguage ParseLanguage(string language, List<string> notes)
    {
        if (string.Equals(language, "python", StringComparison.OrdinalIgnoreCase)) return ProgramLanguage.Python;
        if (Enum.TryParse(language, ignoreCase: true, out ProgramLanguage parsed) && Enum.IsDefined(parsed)) return parsed;
        notes.Add("language = " + language + ": no dedicated support, so the program is evolved as generic text.");
        return ProgramLanguage.Generic;
    }

    private static ProgramReasoningEffort ParseEffort(string effort) =>
        Enum.TryParse(effort, ignoreCase: true, out ProgramReasoningEffort parsed)
            ? parsed
            : throw new InvalidDataException("reasoning_effort = " + effort + ": not a known effort.");

    private static YamlMappingNode Load(string path, string? python)
    {
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException("OpenEvolve config not found: " + path, path);
        if (info.Length > 1024 * 1024) throw new InvalidDataException("The OpenEvolve config exceeds 1 MiB.");
        var stream = new YamlStream();
        try
        {
            using var reader = new StreamReader(path);
            stream.Load(reader);
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            // OpenEvolve reads its config with PyYAML, which accepts some input the YAML spec does not (for example a
            // quoted multi-line scalar indented less than its key). Parse such a file the way OpenEvolve does.
            string json = ReadWithPyYaml(path, python)
                ?? throw new InvalidDataException(
                    $"The OpenEvolve config is not valid YAML at line {exception.Start.Line}, column {exception.Start.Column}, " +
                    "and PyYAML was not available to read it the way OpenEvolve does. Pass --python with an interpreter " +
                    "that has PyYAML installed.", exception);
            stream = new YamlStream();
            using var reader = new StringReader(json);
            stream.Load(reader);
        }
        if (stream.Documents.Count == 0) return new YamlMappingNode();
        return stream.Documents[0].RootNode as YamlMappingNode
               ?? throw new InvalidDataException("The OpenEvolve config must be a mapping at the top level.");
    }

    // Returns the config as JSON (which is also YAML) read by PyYAML, or null when no interpreter with PyYAML ran.
    private static string? ReadWithPyYaml(string path, string? python)
    {
        var start = new System.Diagnostics.ProcessStartInfo(python ?? "python")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("import json, sys, yaml; json.dump(yaml.safe_load(open(sys.argv[1], encoding='utf-8')), sys.stdout)");
        start.ArgumentList.Add(Path.GetFullPath(path));
        try
        {
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            Task.WaitAll(output, error);
            return process.ExitCode == 0 && output.Result.Length > 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // Nested sections become dotted keys, as OpenEvolve's dataclasses nest. Lists and the free-form mappings some keys
    // take (llm.models entries, per-dimension feature_bins) stay whole values.
    private static Dictionary<string, object?> Flatten(YamlMappingNode root)
    {
        var flat = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode value) in root.Children)
        {
            string key = ((YamlScalarNode)keyNode).Value ?? string.Empty;
            if (value is YamlMappingNode section && key is "llm" or "prompt" or "database" or "evaluator" or "evolution_trace")
            {
                foreach ((YamlNode childKey, YamlNode child) in section.Children)
                    flat[key + "." + (((YamlScalarNode)childKey).Value ?? string.Empty)] = Convert(child);
            }
            else
            {
                flat[key] = Convert(value);
            }
        }
        return flat;
    }

    private static object? Convert(YamlNode node) => node switch
    {
        YamlScalarNode scalar => Scalar(scalar),
        YamlSequenceNode sequence => sequence.Children.Select(Convert).ToList(),
        YamlMappingNode mapping => mapping.Children.ToDictionary(pair => ((YamlScalarNode)pair.Key).Value ?? string.Empty,
            pair => Convert(pair.Value), StringComparer.Ordinal),
        _ => null
    };

    // YAML scalars are text; this applies YAML 1.2's core schema, the types PyYAML gives OpenEvolve for these values.
    private static object? Scalar(YamlScalarNode scalar)
    {
        string text = scalar.Value ?? string.Empty;
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted) return text;
        if (text is "" or "~" or "null" or "Null" or "NULL") return null;
        if (text is "true" or "True" or "TRUE") return true;
        if (text is "false" or "False" or "FALSE") return false;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer)) return integer;
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return number;
        return text;
    }

    private static Dictionary<string, object?> Defaults()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(DefaultsResource)
            ?? throw new InvalidOperationException("The OpenEvolve defaults are missing from the tool.");
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => FromJson(property.Value),
            StringComparer.Ordinal);
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out long integer) ? integer : element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        _ => null
    };

    private static bool Same(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (null, _) or (_, null) => false,
        (long x, long y) => x == y,
        (long or double, long or double) => Math.Abs(System.Convert.ToDouble(a, CultureInfo.InvariantCulture) -
                                                     System.Convert.ToDouble(b, CultureInfo.InvariantCulture)) < 1e-12,
        (List<object?> x, List<object?> y) => x.Count == y.Count && x.Zip(y, Same).All(equal => equal),
        (Dictionary<string, object?> x, Dictionary<string, object?> y) => x.Count == y.Count &&
            x.All(pair => y.TryGetValue(pair.Key, out object? other) && Same(pair.Value, other)),
        _ => Equals(a, b)
    };

    private static long? Integer(object? value) => value switch
    {
        long integer => integer,
        double number when Math.Abs(number - Math.Round(number)) < 1e-12 => (long)Math.Round(number),
        null => null,
        _ => throw new InvalidDataException("expected a whole number, found " + Show(value) + ".")
    };

    private static double? Number(object? value) => value switch
    {
        long integer => integer,
        double number => number,
        null => null,
        _ => throw new InvalidDataException("expected a number, found " + Show(value) + ".")
    };

    private static bool Boolean(object? value) => value switch
    {
        bool flag => flag,
        _ => throw new InvalidDataException("expected true or false, found " + Show(value) + ".")
    };

    private static List<object?> List(object? value) => value switch
    {
        List<object?> list => list,
        null => new List<object?>(),
        _ => new List<object?> { value }
    };

    internal static string Show(object? value) => value switch
    {
        null => "null",
        bool flag => flag ? "true" : "false",
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        List<object?> list => "[" + string.Join(", ", list.Select(Show)) + "]",
        Dictionary<string, object?> map => "{" + string.Join(", ", map.Select(pair => pair.Key + ": " + Show(pair.Value))) + "}",
        _ => value.ToString() ?? string.Empty
    };
}
