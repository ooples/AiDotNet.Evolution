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
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), """
            import os, re

            def _x(path):
                with open(path) as f:
                    return int(re.search(r"X = (\d+)", f.read()).group(1))

            def evaluate_stage1(path):
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

    private static OpenEvolveImport Import(TemporaryDirectory directory, string yaml)
    {
        string config = Path.Combine(directory.Path, "config.yaml");
        File.WriteAllText(config, yaml);
        File.WriteAllText(Path.Combine(directory.Path, "initial_program.py"), "X = 0\n");
        File.WriteAllText(Path.Combine(directory.Path, "evaluator.py"), "def evaluate(path):\n    return {}\n");
        return OpenEvolveConfigImporter.Import(config, Path.Combine(directory.Path, "initial_program.py"),
            Path.Combine(directory.Path, "evaluator.py"), iterations: null, python: null);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "docs", "parity"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    }
}
#endif
