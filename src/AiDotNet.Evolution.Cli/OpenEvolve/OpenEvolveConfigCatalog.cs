namespace AiDotNet.Evolution.Cli;

/// <summary>What the importer does with an OpenEvolve config key.</summary>
internal enum OpenEvolveKeyDisposition
{
    /// <summary>Translated into the run file.</summary>
    Mapped,
    /// <summary>Accepted and reported: it changes logging, storage or bookkeeping, never what the search does.</summary>
    NoEffect,
    /// <summary>Accepted at OpenEvolve's default and reported; any other value is refused by name.</summary>
    RefusedUnlessDefault
}

internal sealed record OpenEvolveKey(string Name, OpenEvolveKeyDisposition Disposition, string Ours);

/// <summary>
/// Every key of OpenEvolve 0.3.2's <c>Config</c> (411fb59) and what it becomes here. A key missing from this list is
/// refused, so a newer OpenEvolve option can never be ignored silently. A test pins the list to the upstream key set.
/// </summary>
internal static class OpenEvolveConfigCatalog
{
    public static IReadOnlyList<OpenEvolveKey> Keys { get; } = new OpenEvolveKey[]
    {
        new("max_iterations", OpenEvolveKeyDisposition.Mapped, "budget.maxEvaluations = max_iterations + 1 (ours counts the seed); --iterations overrides it, as in OpenEvolve"),
        new("checkpoint_interval", OpenEvolveKeyDisposition.NoEffect, "ours checkpoints after every batch, so any interval is met"),
        new("log_level", OpenEvolveKeyDisposition.NoEffect, "logging only; every run writes a trace"),
        new("log_dir", OpenEvolveKeyDisposition.NoEffect, "logs, trace and checkpoints go under --output"),
        new("random_seed", OpenEvolveKeyDisposition.Mapped, "budget.seed; null (OpenEvolve's unseeded run) becomes 42 and is reported"),
        new("language", OpenEvolveKeyDisposition.Mapped, "language; null means python"),
        new("file_suffix", OpenEvolveKeyDisposition.Mapped, "the suffix of the candidate file the evaluator receives"),
        new("llm.api_base", OpenEvolveKeyDisposition.Mapped, "model.endpoint for every model that does not set its own"),
        new("llm.api_key", OpenEvolveKeyDisposition.RefusedUnlessDefault, "keys are read from the OPENAI_API_KEY environment variable, never from the config"),
        new("llm.name", OpenEvolveKeyDisposition.Mapped, "model.name when llm.models is absent"),
        new("llm.provider", OpenEvolveKeyDisposition.RefusedUnlessDefault, "the OpenAI-compatible endpoint is the provider; set llm.manual_mode for manual answers"),
        new("llm.init_client", OpenEvolveKeyDisposition.RefusedUnlessDefault, "a Python callable cannot be called from the CLI"),
        new("llm.weight", OpenEvolveKeyDisposition.Mapped, "model.weight, the default weight of each model"),
        new("llm.system_message", OpenEvolveKeyDisposition.RefusedUnlessDefault, "use prompt.system_message, which ours applies to every model"),
        new("llm.temperature", OpenEvolveKeyDisposition.Mapped, "model.temperature"),
        new("llm.top_p", OpenEvolveKeyDisposition.Mapped, "model.topP"),
        new("llm.max_tokens", OpenEvolveKeyDisposition.Mapped, "model.maxOutputTokens"),
        new("llm.timeout", OpenEvolveKeyDisposition.Mapped, "model.timeoutSeconds"),
        new("llm.retries", OpenEvolveKeyDisposition.Mapped, "model.maxRetries"),
        new("llm.retry_delay", OpenEvolveKeyDisposition.Mapped, "model.retryDelaySeconds"),
        new("llm.random_seed", OpenEvolveKeyDisposition.RefusedUnlessDefault, "model calls are seeded from the run seed, per proposal"),
        new("llm.reasoning_effort", OpenEvolveKeyDisposition.Mapped, "model.reasoningEffort"),
        new("llm.max_budget_usd", OpenEvolveKeyDisposition.Mapped, "model.maxBudgetUsd"),
        new("llm.manual_mode", OpenEvolveKeyDisposition.Mapped, "model.provider = Manual"),
        new("llm._manual_queue_dir", OpenEvolveKeyDisposition.Mapped, "model.manualQueue"),
        new("llm.models", OpenEvolveKeyDisposition.Mapped, "model plus additionalModels, sampled by weight"),
        new("llm.evaluator_models", OpenEvolveKeyDisposition.RefusedUnlessDefault, "LLM feedback on programs (evaluator.use_llm_feedback) is not implemented"),
        new("llm.primary_model", OpenEvolveKeyDisposition.Mapped, "model.name (OpenEvolve's older single-model form)"),
        new("llm.primary_model_weight", OpenEvolveKeyDisposition.Mapped, "model.weight"),
        new("llm.secondary_model", OpenEvolveKeyDisposition.Mapped, "a second model in additionalModels"),
        new("llm.secondary_model_weight", OpenEvolveKeyDisposition.Mapped, "its weight"),
        new("prompt.template_dir", OpenEvolveKeyDisposition.RefusedUnlessDefault, "custom prompt templates are not supported"),
        new("prompt.system_message", OpenEvolveKeyDisposition.Mapped, "systemMessage"),
        new("prompt.evaluator_system_message", OpenEvolveKeyDisposition.RefusedUnlessDefault, "LLM feedback on programs is not implemented"),
        new("prompt.programs_as_changes_description", OpenEvolveKeyDisposition.RefusedUnlessDefault, "the changes-description prompt mode is not implemented"),
        new("prompt.system_message_changes_description", OpenEvolveKeyDisposition.RefusedUnlessDefault, "the changes-description prompt mode is not implemented"),
        new("prompt.initial_changes_description", OpenEvolveKeyDisposition.RefusedUnlessDefault, "the changes-description prompt mode is not implemented"),
        new("prompt.num_top_programs", OpenEvolveKeyDisposition.Mapped, "search.topPrograms"),
        new("prompt.num_diverse_programs", OpenEvolveKeyDisposition.Mapped, "search.diversePrograms"),
        new("prompt.use_template_stochasticity", OpenEvolveKeyDisposition.RefusedUnlessDefault, "prompt template variations are not implemented"),
        new("prompt.template_variations", OpenEvolveKeyDisposition.RefusedUnlessDefault, "prompt template variations are not implemented"),
        new("prompt.use_meta_prompting", OpenEvolveKeyDisposition.RefusedUnlessDefault, "meta-prompting is not implemented in the CLI"),
        new("prompt.meta_prompt_weight", OpenEvolveKeyDisposition.RefusedUnlessDefault, "meta-prompting is not implemented in the CLI"),
        new("prompt.include_artifacts", OpenEvolveKeyDisposition.Mapped, "search.includeArtifacts"),
        new("prompt.max_artifact_bytes", OpenEvolveKeyDisposition.Mapped, "search.maxArtifactBytes"),
        new("prompt.artifact_security_filter", OpenEvolveKeyDisposition.RefusedUnlessDefault, "ours always removes secrets from artifacts; it cannot be turned off"),
        new("prompt.suggest_simplification_after_chars", OpenEvolveKeyDisposition.RefusedUnlessDefault, "OpenEvolve's length-based prompt hints are not reproduced"),
        new("prompt.include_changes_under_chars", OpenEvolveKeyDisposition.RefusedUnlessDefault, "OpenEvolve's length-based prompt hints are not reproduced"),
        new("prompt.concise_implementation_max_lines", OpenEvolveKeyDisposition.RefusedUnlessDefault, "OpenEvolve's length-based prompt hints are not reproduced"),
        new("prompt.comprehensive_implementation_min_lines", OpenEvolveKeyDisposition.RefusedUnlessDefault, "OpenEvolve's length-based prompt hints are not reproduced"),
        new("prompt.diff_summary_max_line_len", OpenEvolveKeyDisposition.RefusedUnlessDefault, "diff summaries in prompts are not configurable in the CLI yet"),
        new("prompt.diff_summary_max_lines", OpenEvolveKeyDisposition.RefusedUnlessDefault, "diff summaries in prompts are not configurable in the CLI yet"),
        new("prompt.code_length_threshold", OpenEvolveKeyDisposition.RefusedUnlessDefault, "OpenEvolve's length-based prompt hints are not reproduced"),
        new("database.db_path", OpenEvolveKeyDisposition.NoEffect, "run state lives in checkpoints under --output"),
        new("database.in_memory", OpenEvolveKeyDisposition.NoEffect, "run state lives in checkpoints under --output"),
        new("database.log_prompts", OpenEvolveKeyDisposition.NoEffect, "prompt logging only; prompts are not written to the trace"),
        new("database.population_size", OpenEvolveKeyDisposition.Mapped, "search.archiveCapacity, the most elites the archive holds"),
        new("database.archive_size", OpenEvolveKeyDisposition.Mapped, "search.eliteArchiveSize, the global elite index programs are drawn from"),
        new("database.num_islands", OpenEvolveKeyDisposition.Mapped, "search.islands"),
        new("database.elite_selection_ratio", OpenEvolveKeyDisposition.RefusedUnlessDefault, "the share of top programs used for inspirations is not configurable; ours uses prompt.num_top_programs"),
        new("database.exploration_ratio", OpenEvolveKeyDisposition.Mapped, "search.explorationRatio; the remainder after exploitation becomes search.eliteRatio"),
        new("database.exploitation_ratio", OpenEvolveKeyDisposition.Mapped, "search.exploitationRatio"),
        new("database.diversity_metric", OpenEvolveKeyDisposition.RefusedUnlessDefault, "only OpenEvolve's default diversity metric name is accepted"),
        new("database.feature_dimensions", OpenEvolveKeyDisposition.Mapped, "search.metricDescriptors; complexity becomes program length, diversity is dropped and reported"),
        new("database.feature_bins", OpenEvolveKeyDisposition.Mapped, "the bin count of each descriptor"),
        new("database.diversity_reference_size", OpenEvolveKeyDisposition.RefusedUnlessDefault, "OpenEvolve's population-relative diversity feature is not reproduced"),
        new("database.migration_interval", OpenEvolveKeyDisposition.Mapped, "search.migrationInterval"),
        new("database.migration_rate", OpenEvolveKeyDisposition.Mapped, "search.migrationRate"),
        new("database.random_seed", OpenEvolveKeyDisposition.NoEffect, "the run seed drives every random stream"),
        new("database.artifacts_base_path", OpenEvolveKeyDisposition.NoEffect, "artifacts are kept with the run under --output"),
        new("database.artifact_size_threshold", OpenEvolveKeyDisposition.NoEffect, "artifact storage placement only"),
        new("database.cleanup_old_artifacts", OpenEvolveKeyDisposition.NoEffect, "artifact storage housekeeping only"),
        new("database.artifact_retention_days", OpenEvolveKeyDisposition.NoEffect, "artifact storage housekeeping only"),
        new("database.max_snapshot_artifacts", OpenEvolveKeyDisposition.NoEffect, "artifact storage housekeeping only"),
        new("database.novelty_llm", OpenEvolveKeyDisposition.RefusedUnlessDefault, "LLM novelty judging is not wired into the CLI"),
        new("database.embedding_model", OpenEvolveKeyDisposition.RefusedUnlessDefault, "embedding novelty is not wired into the CLI"),
        new("database.similarity_threshold", OpenEvolveKeyDisposition.RefusedUnlessDefault, "embedding novelty is not wired into the CLI"),
        new("evaluator.timeout", OpenEvolveKeyDisposition.Mapped, "openEvolveEvaluator.timeoutSeconds per stage and budget.evaluationTimeLimitSeconds"),
        new("evaluator.max_retries", OpenEvolveKeyDisposition.Mapped, "search.evaluationRetries"),
        new("evaluator.memory_limit_mb", OpenEvolveKeyDisposition.Mapped, "search.evaluationMemoryLimitMb, enforced by the sandbox (OpenEvolve declares it but does not enforce it)"),
        new("evaluator.cpu_limit", OpenEvolveKeyDisposition.RefusedUnlessDefault, "CPU-time limits are not configurable yet"),
        new("evaluator.cascade_evaluation", OpenEvolveKeyDisposition.Mapped, "openEvolveEvaluator.cascade"),
        new("evaluator.cascade_thresholds", OpenEvolveKeyDisposition.Mapped, "openEvolveEvaluator.cascadeThresholds, with OpenEvolve's threshold rule"),
        new("evaluator.parallel_evaluations", OpenEvolveKeyDisposition.Mapped, "budget.parallelism"),
        new("evaluator.distributed", OpenEvolveKeyDisposition.RefusedUnlessDefault, "distributed evaluation is not configured through this importer"),
        new("evaluator.use_llm_feedback", OpenEvolveKeyDisposition.RefusedUnlessDefault, "LLM feedback on programs is not implemented"),
        new("evaluator.llm_feedback_weight", OpenEvolveKeyDisposition.RefusedUnlessDefault, "LLM feedback on programs is not implemented"),
        new("evaluator.enable_artifacts", OpenEvolveKeyDisposition.Mapped, "artifacts are collected when true and ignored when false"),
        new("evaluator.max_artifact_storage", OpenEvolveKeyDisposition.NoEffect, "artifact storage housekeeping only"),
        new("evolution_trace.enabled", OpenEvolveKeyDisposition.NoEffect, "ours always writes trace-NNN.jsonl under --output"),
        new("evolution_trace.format", OpenEvolveKeyDisposition.NoEffect, "ours always writes JSON lines"),
        new("evolution_trace.include_code", OpenEvolveKeyDisposition.NoEffect, "the trace records identities; programs are in checkpoints and best.py"),
        new("evolution_trace.include_prompts", OpenEvolveKeyDisposition.NoEffect, "the trace records identities; programs are in checkpoints and best.py"),
        new("evolution_trace.output_path", OpenEvolveKeyDisposition.NoEffect, "the trace goes under --output"),
        new("evolution_trace.buffer_size", OpenEvolveKeyDisposition.NoEffect, "the trace is flushed per event"),
        new("evolution_trace.compress", OpenEvolveKeyDisposition.NoEffect, "the trace is not compressed"),
        new("diff_based_evolution", OpenEvolveKeyDisposition.Mapped, "mode = Diff when true, FullRewrite when false"),
        new("max_code_length", OpenEvolveKeyDisposition.Mapped, "budget.maxProgramChars"),
        new("diff_pattern", OpenEvolveKeyDisposition.RefusedUnlessDefault, "ours reads OpenEvolve's default SEARCH/REPLACE block format only"),
        new("early_stopping_patience", OpenEvolveKeyDisposition.Mapped, "search.earlyStoppingPatience, in evaluations"),
        new("convergence_threshold", OpenEvolveKeyDisposition.Mapped, "search.earlyStoppingMinimumImprovement"),
        new("early_stopping_metric", OpenEvolveKeyDisposition.RefusedUnlessDefault, "early stopping watches the fitness (combined_score); other metrics are not supported"),
        new("max_tasks_per_child", OpenEvolveKeyDisposition.NoEffect, "every evaluation already runs in a fresh process"),
    };

    private static readonly Dictionary<string, OpenEvolveKey> ByName = Keys.ToDictionary(key => key.Name, StringComparer.Ordinal);

    public static OpenEvolveKey? Find(string name) => ByName.TryGetValue(name, out OpenEvolveKey? key) ? key : null;
}
