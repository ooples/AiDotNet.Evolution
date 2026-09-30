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
        new("language", OpenEvolveKeyDisposition.Mapped, "language; null means python, and a language without dedicated support (text, a prompt) is evolved as generic text"),
        new("file_suffix", OpenEvolveKeyDisposition.Mapped, "the suffix of the candidate file the evaluator receives"),
        new("llm.api_base", OpenEvolveKeyDisposition.Mapped, "model.endpoint for every model that does not set its own; OPENAI_API_BASE when unset, as in OpenEvolve"),
        new("llm.api_key", OpenEvolveKeyDisposition.NoEffect, "never read from the file (examples ship placeholders); the key comes from OPENAI_API_KEY, as OpenEvolve falls back to"),
        new("llm.name", OpenEvolveKeyDisposition.Mapped, "model.name when llm.models is absent"),
        new("llm.provider", OpenEvolveKeyDisposition.Mapped, "model.provider: null or openai -> OpenAiCompatible, claude_code -> ClaudeCode; others are refused"),
        new("llm.init_client", OpenEvolveKeyDisposition.RefusedUnlessDefault, "a Python callable cannot be called from the CLI"),
        new("llm.weight", OpenEvolveKeyDisposition.Mapped, "model.weight, the default weight of each model"),
        new("llm.system_message", OpenEvolveKeyDisposition.NoEffect, "OpenEvolve's prompt sampler always passes prompt.system_message, so a model's own is never used"),
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
        new("llm.evaluator_models", OpenEvolveKeyDisposition.Mapped, "llmFeedback.models, the judging models; empty uses the proposal models, as in OpenEvolve"),
        new("llm.primary_model", OpenEvolveKeyDisposition.Mapped, "model.name (OpenEvolve's older single-model form)"),
        new("llm.primary_model_weight", OpenEvolveKeyDisposition.Mapped, "model.weight"),
        new("llm.secondary_model", OpenEvolveKeyDisposition.Mapped, "a second model in additionalModels"),
        new("llm.secondary_model_weight", OpenEvolveKeyDisposition.Mapped, "its weight"),
        new("prompt.template_dir", OpenEvolveKeyDisposition.Mapped, "prompt.templateDirectory, relative to the working directory as in OpenEvolve; templates layered over ours by stem; a missing directory is an error"),
        new("prompt.system_message", OpenEvolveKeyDisposition.Mapped, "prompt.systemMessage; a template name when a template of that name exists, literal text otherwise (OpenEvolve's guess, made explicit)"),
        new("prompt.evaluator_system_message", OpenEvolveKeyDisposition.Mapped, "prompt.evaluatorSystemMessage, the LLM-feedback judge's system message"),
        new("prompt.programs_as_changes_description", OpenEvolveKeyDisposition.Mapped, "prompt.programsAsChangesDescription"),
        new("prompt.system_message_changes_description", OpenEvolveKeyDisposition.Mapped, "prompt.systemMessageChangesDescription, replacing that template's text as OpenEvolve does"),
        new("prompt.initial_changes_description", OpenEvolveKeyDisposition.Mapped, "prompt.initialChangesDescription"),
        new("prompt.num_top_programs", OpenEvolveKeyDisposition.Mapped, "prompt.numTopPrograms and search.topPrograms"),
        new("prompt.num_diverse_programs", OpenEvolveKeyDisposition.Mapped, "the inspiration count n: island best, then top programs, then diverse ones up to n, as OpenEvolve fills it"),
        new("prompt.use_template_stochasticity", OpenEvolveKeyDisposition.Mapped, "prompt.useTemplateStochasticity"),
        new("prompt.template_variations", OpenEvolveKeyDisposition.Mapped, "prompt.templateVariations"),
        new("prompt.use_meta_prompting", OpenEvolveKeyDisposition.RefusedUnlessDefault, "meta-prompting is not implemented in the CLI"),
        new("prompt.meta_prompt_weight", OpenEvolveKeyDisposition.RefusedUnlessDefault, "meta-prompting is not implemented in the CLI"),
        new("prompt.include_artifacts", OpenEvolveKeyDisposition.Mapped, "prompt.includeArtifacts and search.includeArtifacts"),
        new("prompt.max_artifact_bytes", OpenEvolveKeyDisposition.Mapped, "prompt.maxArtifactBytes and search.maxArtifactBytes"),
        new("prompt.artifact_security_filter", OpenEvolveKeyDisposition.Mapped, "prompt.artifactSecurityFilter"),
        new("prompt.suggest_simplification_after_chars", OpenEvolveKeyDisposition.Mapped, "prompt.suggestSimplificationAfterChars"),
        new("prompt.include_changes_under_chars", OpenEvolveKeyDisposition.Mapped, "prompt.includeChangesUnderChars"),
        new("prompt.concise_implementation_max_lines", OpenEvolveKeyDisposition.Mapped, "prompt.conciseImplementationMaxLines"),
        new("prompt.comprehensive_implementation_min_lines", OpenEvolveKeyDisposition.Mapped, "prompt.comprehensiveImplementationMinLines"),
        new("prompt.diff_summary_max_line_len", OpenEvolveKeyDisposition.RefusedUnlessDefault, "diff summaries in prompts are not configurable in the CLI yet"),
        new("prompt.diff_summary_max_lines", OpenEvolveKeyDisposition.RefusedUnlessDefault, "diff summaries in prompts are not configurable in the CLI yet"),
        new("prompt.code_length_threshold", OpenEvolveKeyDisposition.Mapped, "prompt.suggestSimplificationAfterChars when suggest_simplification_after_chars is unset (its older name)"),
        new("database.db_path", OpenEvolveKeyDisposition.NoEffect, "run state lives in checkpoints under --output"),
        new("database.in_memory", OpenEvolveKeyDisposition.NoEffect, "run state lives in checkpoints under --output"),
        new("database.log_prompts", OpenEvolveKeyDisposition.NoEffect, "prompt logging only; prompts are not written to the trace"),
        new("database.population_size", OpenEvolveKeyDisposition.Mapped, "search.archiveCapacity, the most elites the archive holds"),
        new("database.archive_size", OpenEvolveKeyDisposition.Mapped, "search.eliteArchiveSize, the global elite index programs are drawn from"),
        new("database.num_islands", OpenEvolveKeyDisposition.Mapped, "search.islands"),
        new("database.elite_selection_ratio", OpenEvolveKeyDisposition.Mapped, "search.topInspirations = max(1, int(num_diverse_programs x ratio)), OpenEvolve's top-program inspirations"),
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
        new("evaluator.timeout", OpenEvolveKeyDisposition.Mapped, "openEvolveEvaluator.timeoutSeconds per stage and budget.evaluationTimeLimitSeconds, capped with a note so all stages fit the 24 h sandbox ceiling"),
        new("evaluator.max_retries", OpenEvolveKeyDisposition.Mapped, "search.evaluationRetries"),
        new("evaluator.memory_limit_mb", OpenEvolveKeyDisposition.Mapped, "search.evaluationMemoryLimitMb, enforced by the sandbox (OpenEvolve declares it but does not enforce it)"),
        new("evaluator.cpu_limit", OpenEvolveKeyDisposition.RefusedUnlessDefault, "CPU-time limits are not configurable yet"),
        new("evaluator.cascade_evaluation", OpenEvolveKeyDisposition.Mapped, "openEvolveEvaluator.cascade"),
        new("evaluator.cascade_thresholds", OpenEvolveKeyDisposition.Mapped, "openEvolveEvaluator.cascadeThresholds, with OpenEvolve's threshold rule"),
        new("evaluator.parallel_evaluations", OpenEvolveKeyDisposition.Mapped, "budget.parallelism"),
        new("evaluator.distributed", OpenEvolveKeyDisposition.RefusedUnlessDefault, "distributed evaluation is not configured through this importer"),
        new("evaluator.use_llm_feedback", OpenEvolveKeyDisposition.Mapped, "llmFeedback: a judge model's score blended into each fitness"),
        new("evaluator.llm_feedback_weight", OpenEvolveKeyDisposition.Mapped, "llmFeedback.weight"),
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
        new("early_stopping_patience", OpenEvolveKeyDisposition.Mapped, "search.earlyStoppingPatience in evaluations when positive; negative stops at convergence_threshold (search.targetQuality); zero never stops early"),
        new("convergence_threshold", OpenEvolveKeyDisposition.Mapped, "search.earlyStoppingMinimumImprovement, or the target fitness when early_stopping_patience is negative"),
        new("early_stopping_metric", OpenEvolveKeyDisposition.RefusedUnlessDefault, "early stopping watches the fitness (combined_score); other metrics are not supported"),
        new("max_tasks_per_child", OpenEvolveKeyDisposition.NoEffect, "every evaluation already runs in a fresh process"),
    };

    private static readonly Dictionary<string, OpenEvolveKey> ByName = Keys.ToDictionary(key => key.Name, StringComparer.Ordinal);

    public static OpenEvolveKey? Find(string name) => ByName.TryGetValue(name, out OpenEvolveKey? key) ? key : null;
}
