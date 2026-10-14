#if NET8_0_OR_GREATER
using System.Text.Json;
using AiDotNet.Evolution.Cli;
using AiDotNet.Evolution.Programs;
using Xunit;

namespace AiDotNet.Evolution.Tests;

/// <summary>V1-56 (#169): running unmodified OpenEvolve configs and evaluators through the CLI.</summary>
[Collection(ConsoleCollection.Name)]
public sealed class OpenEvolveImportTests
{
    private static readonly string Python = Environment.GetEnvironmentVariable("EVOLUTION_PYTHON")
        ?? (OperatingSystem.IsWindows() ? "python" : "python3");

    // Every key of OpenEvolve 0.3.2's Config, taken from its dataclasses at 411fb59. A key upstream adds or renames fails
    // here until it is classified, so a new option can never be ignored silently.
    private static readonly string[] UpstreamKeys =
    {
        "max_iterations", "checkpoint_interval", "log_level", "log_dir", "random_seed", "language", "file_suffix",
        "llm.api_base", "llm.api_key", "llm.name", "llm.provider", "llm.init_client", "llm.weight", "llm.system_message",
        "llm.temperature", "llm.top_p", "llm.max_tokens", "llm.timeout", "llm.retries", "llm.retry_delay", "llm.random_seed",
        "llm.reasoning_effort", "llm.max_budget_usd", "llm.manual_mode", "llm._manual_queue_dir", "llm.models",
        "llm.evaluator_models", "llm.primary_model", "llm.primary_model_weight", "llm.secondary_model",
        "llm.secondary_model_weight", "prompt.template_dir", "prompt.system_message", "prompt.evaluator_system_message",
        "prompt.programs_as_changes_description", "prompt.system_message_changes_description",
        "prompt.initial_changes_description", "prompt.num_top_programs", "prompt.num_diverse_programs",
        "prompt.use_template_stochasticity", "prompt.template_variations", "prompt.use_meta_prompting",
        "prompt.meta_prompt_weight", "prompt.include_artifacts", "prompt.max_artifact_bytes", "prompt.artifact_security_filter",
        "prompt.suggest_simplification_after_chars", "prompt.include_changes_under_chars",
        "prompt.concise_implementation_max_lines", "prompt.comprehensive_implementation_min_lines",
        "prompt.diff_summary_max_line_len", "prompt.diff_summary_max_lines", "prompt.code_length_threshold",
        "database.db_path", "database.in_memory", "database.log_prompts", "database.population_size", "database.archive_size",
        "database.num_islands", "database.elite_selection_ratio", "database.exploration_ratio", "database.exploitation_ratio",
        "database.diversity_metric", "database.feature_dimensions", "database.feature_bins", "database.diversity_reference_size",
        "database.migration_interval", "database.migration_rate", "database.random_seed", "database.artifacts_base_path",
        "database.artifact_size_threshold", "database.cleanup_old_artifacts", "database.artifact_retention_days",
        "database.max_snapshot_artifacts", "database.novelty_llm", "database.embedding_model", "database.similarity_threshold",
        "evaluator.timeout", "evaluator.max_retries", "evaluator.memory_limit_mb", "evaluator.cpu_limit",
        "evaluator.cascade_evaluation", "evaluator.cascade_thresholds", "evaluator.parallel_evaluations", "evaluator.distributed",
        "evaluator.use_llm_feedback", "evaluator.llm_feedback_weight", "evaluator.enable_artifacts",
        "evaluator.max_artifact_storage", "evolution_trace.enabled", "evolution_trace.format", "evolution_trace.include_code",
        "evolution_trace.include_prompts", "evolution_trace.output_path", "evolution_trace.buffer_size",
        "evolution_trace.compress", "diff_based_evolution", "max_code_length", "diff_pattern", "early_stopping_patience",
        "convergence_threshold", "early_stopping_metric", "max_tasks_per_child"
    };

    [Fact]
    public void Every_openevolve_config_key_is_classified_exactly_once()
    {
        Assert.Equal(103, UpstreamKeys.Length);
        Assert.Equal(UpstreamKeys.OrderBy(key => key, StringComparer.Ordinal),
            OpenEvolveConfigCatalog.Keys.Select(key => key.Name).OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(OpenEvolveConfigCatalog.Keys, key => Assert.False(string.IsNullOrWhiteSpace(key.Ours), key.Name));
    }

    [Fact]
    public void The_migration_guide_lists_every_key_with_its_disposition()
    {
        string guide = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "migration", "OPENEVOLVE_CONFIG.md"));
        foreach (OpenEvolveKey key in OpenEvolveConfigCatalog.Keys)
            Assert.Contains("| `" + key.Name + "` | " + key.Disposition + " |", guide);
    }

    [Fact]
    public void Mapped_keys_land_in_the_run_file_over_openevolve_defaults()
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport imported = Import(directory, """
            max_iterations: 5
            random_seed: 11
            diff_based_evolution: false
            llm:
              api_base: http://localhost:9/v1
              models:
                - name: fast
                  weight: 0.8
                - name: strong
                  weight: 0.2
                  temperature: 0.3
            database:
              num_islands: 3
              exploration_ratio: 0.3
              exploitation_ratio: 0.5
              elite_selection_ratio: 0.5
              feature_dimensions: [complexity, accuracy]
              feature_bins: 8
            prompt:
              num_diverse_programs: 4
              system_message: You are a careful optimiser.
              suggest_simplification_after_chars: 800
            evaluator:
              cascade_thresholds: [0.4, 0.6]
              parallel_evaluations: 2
              use_llm_feedback: true
              llm_feedback_weight: 0.25
            """);
        RunFile run = imported.Run;
        Assert.Equal(6, run.Budget.MaxEvaluations); // five iterations after the seed
        Assert.Equal(11UL, run.Budget.Seed);
        Assert.Equal(2, run.Budget.Parallelism);
        Assert.Equal(ProgramEvolutionMode.FullRewrite, run.Mode);
        Assert.Equal(("fast", 0.8), (run.Model.Name, run.Model.Weight));
        Assert.Equal(("strong", 0.2, 0.3), (run.AdditionalModels.Single().Name, run.AdditionalModels.Single().Weight,
            run.AdditionalModels.Single().Temperature ?? double.NaN));
        Assert.Equal(0.7, run.Model.Temperature); // OpenEvolve's default temperature, inherited by a model that sets none
        Assert.Equal(3, run.Search.Islands);
        Assert.Equal((0.3, 0.5), (run.Search.ExplorationRatio ?? 0, run.Search.ExploitationRatio ?? 0));
        Assert.Equal(0.2, run.Search.EliteRatio ?? 0, 9);
        // n = 4 inspirations: the island best, max(1, int(4 x 0.5)) = 2 top programs, then 1 diverse.
        Assert.Equal((2, 1), (run.Search.TopInspirations ?? -1, run.Search.DiversePrograms ?? -1));
        Assert.Equal(new[] { ("length", 8), ("accuracy", 8) }, run.Search.MetricDescriptors.Select(d => (d.Name, d.Bins)));
        Assert.Equal(new[] { 0.4, 0.6 }, run.OpenEvolveEvaluator?.CascadeThresholds);
        Assert.True(run.OpenEvolveEvaluator?.Cascade); // OpenEvolve's default
        // Not the name of an OpenEvolve template, so it is the text itself, as OpenEvolve would decide.
        Assert.Equal(("You are a careful optimiser.", ProgramPromptSystemMessageMode.Literal),
            (run.Prompt.SystemMessage, run.Prompt.SystemMessageMode));
        Assert.Equal(800, run.Prompt.SuggestSimplificationAfterChars);
        Assert.Equal(0.25, run.LlmFeedback?.Weight);
        Assert.Empty(run.LlmFeedback?.Models ?? new List<RunModel> { run.Model }); // judges with the proposal models
    }

    /// <summary>
    /// V1-50 (#163): a configured evaluator model set judges on its own, and the proposal models do
    /// not leak into it. The empty case is asserted above, where an empty list is what
    /// <c>RunCommand</c> resolves to the proposal models, as OpenEvolve does.
    /// </summary>
    [Fact]
    public void A_configured_evaluator_model_set_judges_instead_of_the_proposal_models()
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport imported = Import(directory, """
            llm:
              models:
                - name: proposer
                  weight: 1.0
              evaluator_models:
                - name: judge-cheap
                  weight: 0.9
                - name: judge-strong
                  weight: 0.1
                  temperature: 0.2
            evaluator:
              use_llm_feedback: true
            """);

        RunFile run = imported.Run;
        Assert.Equal("proposer", run.Model.Name);
        Assert.Empty(run.AdditionalModels);

        List<RunModel> judges = run.LlmFeedback?.Models ?? new List<RunModel>();
        Assert.Equal(new[] { ("judge-cheap", 0.9), ("judge-strong", 0.1) },
            judges.Select(judge => (judge.Name, judge.Weight)));

        // A judge that sets no temperature still inherits OpenEvolve's default, like a proposal model.
        Assert.Equal(0.2, judges[1].Temperature);
        Assert.Equal(0.7, judges[0].Temperature);

        // The proposal model is not one of the judges: the two sets are separate, not merged.
        Assert.DoesNotContain("proposer", judges.Select(judge => judge.Name));
    }

    [Fact]
    public void An_unsupported_key_is_accepted_at_its_default_and_refused_by_name_otherwise()
    {
        using var directory = new TemporaryDirectory();
        const string Base = "llm:\n  name: m\n";
        Assert.NotNull(Import(directory, Base + "prompt:\n  use_meta_prompting: false\n"));
        var refused = Assert.Throws<InvalidDataException>(() => Import(directory, Base + "prompt:\n  use_meta_prompting: true\n"));
        Assert.Contains("prompt.use_meta_prompting = true", refused.Message);
        var twoAtOnce = Assert.Throws<InvalidDataException>(() => Import(directory,
            Base + "evaluator:\n  distributed: true\n  cpu_limit: 2\n"));
        Assert.Contains("evaluator.distributed", twoAtOnce.Message);
        Assert.Contains("evaluator.cpu_limit", twoAtOnce.Message);
    }

    [Fact]
    public void A_key_openevolve_itself_ignores_is_reported_not_refused()
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport imported = Import(directory, "llm:\n  name: m\nallow_full_rewrites: true\n");
        Assert.Contains(imported.Notes, note => note.StartsWith("allow_full_rewrites is not an OpenEvolve 0.3.2 config key", StringComparison.Ordinal));
    }

    [Fact]
    public void An_openevolve_cascade_evaluator_runs_unmodified_with_openevolve_threshold_semantics()
    {
        using var directory = new TemporaryDirectory();
        using var model = new CliRunTests.FakeChatModel(); // answers "X = n" for call n = 1, 2, ...
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 0\n");
        // Stage 1 scores X / 10; a candidate reaching 0.25 (X >= 3) goes on to stage 2, which scores X / 5 and leaves a
        // marker. OpenEvolve merges stage metrics, the later stage winning, and takes combined_score as the fitness.
        // Real evaluators print progress, directly and from processes they start, and it often contains braces (a
        // dict, a format string); stage 1 below does both. None of it may reach the result the shim prints. (No line
        // of this raw string may start with '#': on net471 the file is compiled out, and such a line then reads as a
        // preprocessor directive.)
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), """
            import os, re, subprocess, sys

            def _x(path):
                with open(path) as f:
                    return int(re.search(r"X = (\d+)", f.read()).group(1))

            def evaluate_stage1(path):
                print({"scoring": path})
                subprocess.run([sys.executable, "-c", "print('{child output}')"], check=True)
                return {"combined_score": _x(path) / 10.0}

            def evaluate_stage2(path):
                x = _x(path)
                open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "stage2-%d" % x), "w").close()
                return {"combined_score": x / 5.0, "stage2_ran": 1.0}

            def evaluate(path):
                return evaluate_stage1(path)
            """);
        File.WriteAllText(Path.Combine(directory.Path, "config.yaml"), $"""
            max_iterations: 4
            random_seed: 7
            diff_based_evolution: false
            llm:
              api_base: {model.Endpoint}
              models:
                - name: fake-model
              timeout: 20
              retries: 0
            database:
              num_islands: 1
            evaluator:
              cascade_thresholds: [0.25]
              timeout: 30
            """);
        string? previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "unused-by-the-fake");
        try
        {
            string output = Path.Combine(directory.Path, "out");
            var (code, stdout, error) = CliRunTests.Run("run", "--openevolve-config", Path.Combine(directory.Path, "config.yaml"),
                Path.Combine(directory.Path, "initial_program.py"), Path.Combine(directory.Path, "evaluator.py"),
                "--output", output, "--python", Python);
            Assert.True(code == 0, error);
            using JsonDocument result = JsonDocument.Parse(stdout);
            Assert.Equal(5, result.RootElement.GetProperty("CompletedEvaluations").GetInt64());
            Assert.Equal(4 / 5.0, result.RootElement.GetProperty("BestQuality").GetDouble(), 12);
            string[] stage2 = Directory.GetFiles(directory.Path, "stage2-*").Select(Path.GetFileName).OfType<string>()
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "stage2-3", "stage2-4" }, stage2);
            Assert.True(File.Exists(Path.Combine(output, "run.json")));
            Assert.True(File.Exists(Path.Combine(output, "openevolve-import.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    [Fact]
    public void An_evaluator_timeout_past_the_sandbox_ceiling_is_capped_with_a_note()
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport ordinary = Import(directory, "llm:\n  name: m\nevaluator:\n  timeout: 300\n");
        Assert.Equal(300, ordinary.Run.Budget.EvaluationTimeLimitSeconds);
        Assert.DoesNotContain(ordinary.Notes, note => note.StartsWith("evaluator.timeout", StringComparison.Ordinal));

        // arc_benchmark sets 180,000 s; every stage together must fit the sandbox's ceiling.
        OpenEvolveImport single = Import(directory, "llm:\n  name: m\nevaluator:\n  timeout: 180000\n  cascade_evaluation: false\n");
        Assert.Equal(ProgramSandboxLimitOptions.MaxTimeLimitSeconds - 10, single.Run.Budget.EvaluationTimeLimitSeconds);
        OpenEvolveImport cascade = Import(directory, "llm:\n  name: m\nevaluator:\n  timeout: 180000\n  cascade_evaluation: true\n");
        Assert.Equal((ProgramSandboxLimitOptions.MaxTimeLimitSeconds - 10) / 3, cascade.Run.Budget.EvaluationTimeLimitSeconds);
        Assert.Contains(cascade.Notes, note => note.StartsWith("evaluator.timeout = 180000 s", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(5, 5L, null)]
    [InlineData(0, null, null)]
    [InlineData(-1, null, 0.95)]
    public void Early_stopping_patience_follows_openevolve(int patience, long? expectedPatience, double? expectedTarget)
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport imported = Import(directory,
            "llm:\n  name: m\nearly_stopping_patience: " + patience + "\nconvergence_threshold: 0.95\n");
        Assert.Equal(expectedPatience, imported.Run.Search.EarlyStoppingPatience);
        Assert.Equal(expectedTarget, imported.Run.Search.TargetQuality);
    }

    [Fact]
    public void A_language_without_dedicated_support_is_evolved_as_generic_text()
    {
        using var directory = new TemporaryDirectory();
        Assert.Equal(ProgramLanguage.Python, Import(directory, "llm:\n  name: m\nlanguage: python\n").Run.Language);
        Assert.Equal(ProgramLanguage.Rust, Import(directory, "llm:\n  name: m\nlanguage: rust\n").Run.Language);

        OpenEvolveImport text = Import(directory, "llm:\n  name: m\nlanguage: text\n");
        Assert.Equal(ProgramLanguage.Generic, text.Run.Language);
        Assert.Contains(text.Notes, note => note.StartsWith("language = text", StringComparison.Ordinal));
    }

    [Fact]
    public void A_relative_template_dir_is_resolved_against_the_working_directory_as_openevolve_does()
    {
        using var directory = new TemporaryDirectory();
        string prompts = Path.Combine(directory.Path, "nested", "prompts");
        Directory.CreateDirectory(prompts);
        string relative = Path.GetRelativePath(Environment.CurrentDirectory, prompts).Replace('\\', '/');

        OpenEvolveImport imported = Import(directory, "llm:\n  name: m\nprompt:\n  template_dir: \"" + relative + "\"\n");

        // Resolving it against the config file's folder instead would point somewhere else entirely.
        Assert.Equal(Path.GetFullPath(prompts), imported.Run.Prompt?.TemplateDirectory);
    }

    [Fact]
    public void A_config_only_pyyaml_accepts_is_read_the_way_openevolve_reads_it()
    {
        using var directory = new TemporaryDirectory();
        // A double-quoted scalar continued on a line indented less than its key: the YAML spec refuses it, PyYAML (which
        // OpenEvolve uses) accepts it. attention_optimization's config is written this way.
        const string Yaml = "llm:\n  name: m\nprompt:\n  system_message: \"\n  You optimise attention kernels.\n    Be precise.\"\n";
        bool pyYaml = HasPyYaml();

        if (pyYaml)
        {
            OpenEvolveImport imported = Import(directory, Yaml, Python);
            Assert.Contains("You optimise attention kernels.", imported.Run.Prompt?.SystemMessage, StringComparison.Ordinal);
        }
        else
        {
            var refused = Assert.Throws<InvalidDataException>(() => Import(directory, Yaml, Python));
            Assert.Contains("PyYAML", refused.Message, StringComparison.Ordinal);
        }
    }

    private static bool HasPyYaml()
    {
        try
        {
            using var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Python, "-c \"import yaml\"")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (probe is null) return false;
            probe.WaitForExit(30_000);
            return probe.HasExited && probe.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Importing a feature dimension warns that the evaluator must report it every time.
    /// </summary>
    /// <remarks>
    /// The warning is the cheap half of the fix below: a reader who sees it at import time does not
    /// have to spend a run, and then an investigation, discovering that a declared dimension their
    /// evaluator reports only on its success path ends the run with no candidates. Upstream refuses the
    /// same configuration - its _calculate_feature_coords raises ValueError for a declared dimension that
    /// is absent from the metrics and is not one of complexity, diversity or score - so what the note adds
    /// is the warning at import time rather than a different outcome.
    /// </remarks>
    [Fact]
    public void A_feature_dimension_warns_that_the_evaluator_must_report_it()
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport imported = Import(directory, """
            llm:
              name: m
            database:
              feature_dimensions: [complexity, ssim]
            """);

        // "complexity" is program length, which the engine always computes, so it needs no warning.
        Assert.DoesNotContain(imported.Notes, note => note.Contains("'complexity'", StringComparison.Ordinal));

        string warning = Assert.Single(
            imported.Notes.Where(note => note.Contains("'ssim'", StringComparison.Ordinal)).ToArray());
        Assert.Contains("every completed evaluation", warning, StringComparison.Ordinal);
        Assert.Contains("no candidates", warning, StringComparison.Ordinal);
    }


    /// <summary>
    /// OpenEvolve's "score" feature dimension is the fitness, not a reported metric, so it is
    /// dropped by name rather than demanded of the evaluator.
    /// </summary>
    /// <remarks>
    /// The 16 configs under examples/alphaevolve_math_problems declare
    /// <c>feature_dimensions: ["score", "complexity"]</c> while their evaluators report
    /// <c>combined_score</c> and never <c>score</c>. Carrying the name through would have required a
    /// metric called "score" on every completed evaluation, which no candidate could satisfy, so
    /// every one of those runs would have ended with no candidates.
    /// </remarks>
    [Fact]
    public void The_score_feature_dimension_is_dropped_rather_than_demanded()
    {
        using var directory = new TemporaryDirectory();
        OpenEvolveImport imported = Import(directory, """
            llm:
              name: m
            database:
              feature_dimensions: ["score", "complexity"]
            """);

        // Only program length survives, and nothing asks the evaluator for a "score" metric.
        RunMetricDescriptor descriptor = Assert.Single(imported.Run.Search.MetricDescriptors);
        Assert.Equal("length", descriptor.Name);

        string note = Assert.Single(
            imported.Notes.Where(n => n.Contains("'score'", StringComparison.Ordinal)).ToArray());
        Assert.Contains("fitness itself", note, StringComparison.Ordinal);
        Assert.Contains("dropped", note, StringComparison.Ordinal);
    }


    /// <summary>
    /// A run that produces no candidate reports why, rather than only that it produced none.
    /// </summary>
    /// <remarks>
    /// A declared <c>database.feature_dimensions</c> entry is requested of the evaluator on every
    /// completed evaluation, and <c>ProgramEvolutionTask</c> refuses an evaluation that omits it
    /// rather than filing the program in cell zero. That refusal is correct, but the run previously
    /// surfaced only <c>StopReason: NoCandidates</c>, which is true and useless: the engine was
    /// holding the diagnostic that explained it. Three investigation sessions on #169 went into
    /// recovering by hand what this now prints.
    /// </remarks>
    [Fact]
    public void A_run_that_produces_no_candidate_reports_the_reason()
    {
        using var directory = new TemporaryDirectory();
        using var model = new CliRunTests.FakeChatModel();

        // The evaluator reports a score but never the declared "ssim" dimension, which is the shape
        // upstream's background_blur has on its failure path.
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"),
            "def evaluate(path):\n    return {\"combined_score\": 0.5}\n");
        File.WriteAllText(Path.Combine(directory.Path, "config.yaml"), $"""
            max_iterations: 2
            random_seed: 5
            diff_based_evolution: false
            llm:
              api_base: {model.Endpoint}
              models:
                - name: fake-model
              timeout: 20
              retries: 0
            database:
              num_islands: 1
              feature_dimensions:
                - complexity
                - ssim
            evaluator:
              timeout: 30
            """);

        string? previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "unused-by-the-fake");
        try
        {
            string output = Path.Combine(directory.Path, "out");
            var (code, stdout, error) = CliRunTests.Run(
                "run", "--openevolve-config", Path.Combine(directory.Path, "config.yaml"),
                Path.Combine(directory.Path, "initial_program.py"),
                Path.Combine(directory.Path, "evaluator.py"),
                "--output", output, "--python", Python);

            using JsonDocument result = JsonDocument.Parse(stdout);
            JsonElement failures = result.RootElement.GetProperty("Failures");

            // The point of the change: the reason travels with the result.
            Assert.True(failures.GetArrayLength() > 0,
                "a run with no candidate reported no failures; stdout=" + stdout + " stderr=" + error);
            // The message is deliberately generic; the descriptor's name is in Data, which is the
            // part that tells a reader which dimension their evaluator has to report.
            //
            // The code moved from "descriptor_missing" to "descriptor_metric_missing", and that is the
            // refusal arriving from a different component rather than a different outcome. When this
            // test was written, ProgramProposalOptions.Clone dropped MetricDescriptors, so the task
            // never demanded ssim at all and every candidate reached the archive without the
            // coordinate: the engine refused it there, able to say only that a coordinate was absent.
            // With the promotion list intact the task refuses first, and it is the component that knows
            // *why* - the run declared ssim as a feature dimension and the evaluator did not report it
            // as a metric. Same dimension, same Data["descriptor"], same no-candidate run; the reason
            // is now attributed to the cause. The engine-level diagnostic is still reachable for an
            // evaluator that reports descriptors directly, and EvolutionCoreParityTests covers it.
            Assert.Equal("descriptor_metric_missing", failures[0].GetProperty("Code").GetString());
            Assert.Equal("ssim", failures[0].GetProperty("Data").GetProperty("descriptor").GetString());

            // And on stderr, where a reader looks first.
            Assert.Contains("the run produced no candidate", error, StringComparison.Ordinal);
            Assert.Contains("descriptor=ssim", error, StringComparison.Ordinal);
            // A run that ends with no candidate still completes: it is not the all-model-calls-failed
            // exit (ModelUnavailableExitCode), because the refusal comes before any proposal is asked for.
            Assert.Equal(0, code);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    [Fact]
    public void An_evaluator_importing_its_own_package_needs_the_search_path_declared()
    {
        // circle_packing_with_artifacts imports openevolve.evaluation_result, and OpenEvolve relies on the evaluator
        // inheriting the shell's PYTHONPATH. The sandbox clears the environment before every execution, so the import
        // fails until the directory is named with --python-path. Both arms run here: without the option every
        // evaluation fails on the import, with it the same unmodified evaluator scores.
        using var directory = new TemporaryDirectory();
        using var model = new CliRunTests.FakeChatModel(); // answers "X = n" for call n = 1, 2, ...
        string modules = Path.Combine(directory.Path, "modules");
        Directory.CreateDirectory(Path.Combine(modules, "upstream_package"));
        File.WriteAllText(Path.Combine(modules, "upstream_package", "__init__.py"), "SCALE = 0.5\n");
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 4\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), """
            import re
            from upstream_package import SCALE

            def evaluate(path):
                with open(path) as handle:
                    x = int(re.search(r"X = (\d+)", handle.read()).group(1))
                return {"combined_score": x * SCALE}
            """);
        File.WriteAllText(Path.Combine(directory.Path, "config.yaml"), $"""
            max_iterations: 1
            random_seed: 7
            diff_based_evolution: false
            llm:
              api_base: {model.Endpoint}
              models:
                - name: fake-model
              timeout: 20
              retries: 0
            database:
              num_islands: 1
            evaluator:
              timeout: 30
            """);

        string[] baseArguments =
        {
            "run", "--openevolve-config", Path.Combine(directory.Path, "config.yaml"),
            Path.Combine(directory.Path, "initial_program.py"), Path.Combine(directory.Path, "evaluator.py"),
            "--python", Python
        };

        string? previousKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "unused-by-the-fake");
        try
        {
            var (withoutCode, withoutStdout, withoutError) = CliRunTests.Run(
                baseArguments.Concat(new[] { "--output", Path.Combine(directory.Path, "no-path") }).ToArray());
            using JsonDocument refused = JsonDocument.Parse(withoutStdout);
            JsonElement failures = refused.RootElement.GetProperty("Failures");
            Assert.True(failures.GetArrayLength() > 0,
                "the import was expected to fail; stdout=" + withoutStdout + " stderr=" + withoutError);
            Assert.Contains("script_failed", failures[0].GetProperty("Code").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, refused.RootElement.GetProperty("BestQuality").ValueKind);

            string output = Path.Combine(directory.Path, "with-path");
            var (code, stdout, error) = CliRunTests.Run(
                baseArguments.Concat(new[] { "--output", output, "--python-path", modules }).ToArray());

            Assert.Equal(0, code);
            using JsonDocument result = JsonDocument.Parse(stdout);
            // The seed is X = 4 and the declared module supplies SCALE = 0.5, so a scored seed is 2.0. Reading the
            // value rather than only the status is what proves the import resolved instead of being skipped.
            Assert.Equal(2.0, result.RootElement.GetProperty("BestQuality").GetDouble(), 12);
            Assert.True(result.RootElement.GetProperty("CompletedEvaluations").GetInt64() >= 1,
                "no evaluation completed; stdout=" + stdout + " stderr=" + error);

            // Recorded in the generated run file, so a resume keeps the path rather than failing on the first evaluation.
            using JsonDocument runFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "run.json")));
            Assert.Equal(
                new[] { modules },
                runFile.RootElement.GetProperty("openEvolveEvaluator").GetProperty("pythonPath")
                    .EnumerateArray().Select(entry => entry.GetString()).ToArray());
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", previousKey);
        }
    }

    [Theory]
    [InlineData("Correctness", "correctness", "duplicate")]
    [InlineData(" ", "", "blank")]
    public void A_run_files_judge_rubric_is_parsed_and_validated_rather_than_ignored(
        string first, string second, string why)
    {
        // The rubric was not reachable from a run file at all, and it is the lever that matters most:
        // measured on a task where storing the shown examples scores perfectly, the gap between a documented
        // lookup table and an honest attempt is 0.083 under the default rubric and 0.310 under one naming
        // generality (benchmarks/external/judge_rubric_probe.py).
        //
        // Asserted through the refusal rather than through a completed run, deliberately. The judge's own
        // metric names only become observable when a judge REPLY parses, and the loopback fake answers with
        // a program; a run that merely completes therefore cannot tell an honoured rubric from an ignored
        // one. An unusable rubric coming back named proves the list was parsed, carried into
        // LlmFeedbackOptions and validated there.
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"),
            "import json, sys\n" + "print(json.dumps({\"quality\": 0.5}))  # evaluate\n");
        var run = new
        {
            schema = "aidotnet-evolve-run-v1",
            runId = "judge-rubric-" + why,
            initialProgram = Path.Combine(directory.Path, "initial_program.py"),
            evaluator = Path.Combine(directory.Path, "evaluator.py"),
            output = Path.Combine(directory.Path, "out"),
            model = new { provider = "OpenAiCompatible", endpoint = "http://127.0.0.1:1/v1", name = "fake-model" },
            budget = new { maxEvaluations = 2, seed = 7, parallelism = 1 },
            llmFeedback = new { weight = 0.3, criteria = new[] { first, second } }
        };
        string runFile = Path.Combine(directory.Path, "run.json");
        File.WriteAllText(runFile, JsonSerializer.Serialize(run));

        var (code, _, error) = CliRunTests.Run("run", runFile);

        Assert.NotEqual(0, code);
        Assert.Contains("Criteria", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_judge_metric_prefix_is_refused_rather_than_read_as_the_default(string prefix)
    {
        // The doc said "empty keeps the default" while the code applied any non-null value, so "" stripped
        // the prefix. That matters because the judge's field names are ordinary words - correctness,
        // readability - so an unprefixed judge metric collides with an evaluator metric of the same name.
        // Refused rather than defaulted: a caller who typed "" meant something that cannot be honoured.
        using var directory = new TemporaryDirectory();
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"),
            "import json, sys\n" + "print(json.dumps({\"quality\": 0.5}))  # evaluate\n");
        var run = new
        {
            schema = "aidotnet-evolve-run-v1",
            runId = "judge-prefix",
            initialProgram = Path.Combine(directory.Path, "initial_program.py"),
            evaluator = Path.Combine(directory.Path, "evaluator.py"),
            output = Path.Combine(directory.Path, "out"),
            model = new { provider = "OpenAiCompatible", endpoint = "http://127.0.0.1:1/v1", name = "fake-model" },
            budget = new { maxEvaluations = 2, seed = 7, parallelism = 1 },
            llmFeedback = new { weight = 0.3, metricPrefix = prefix }
        };
        string runFile = Path.Combine(directory.Path, "run.json");
        File.WriteAllText(runFile, JsonSerializer.Serialize(run));

        var (code, _, error) = CliRunTests.Run("run", runFile);

        Assert.NotEqual(0, code);
        Assert.Contains("metricPrefix", error, StringComparison.Ordinal);
        Assert.Contains("collide", error, StringComparison.Ordinal);
    }

    private static OpenEvolveImport Import(TemporaryDirectory directory, string yaml, string? python = null)
    {
        string config = Path.Combine(directory.Path, "config.yaml");
        File.WriteAllText(config, yaml);
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), "def evaluate(path):\n    return {}\n");
        return OpenEvolveConfigImporter.Import(config, Path.Combine(directory.Path, "initial_program.py"),
            Path.Combine(directory.Path, "evaluator.py"), iterations: null, python: python);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "docs", "parity"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
#endif
