# Migrating from OpenEvolve: config.yaml and evaluators

idotnet-evolve runs an OpenEvolve 0.3.2 task, meaning its `config.yaml`, initial program and evaluator, without
changes:

```text
aidotnet-evolve run --openevolve-config config.yaml initial_program.py evaluator.py [--iterations N] [--output DIR] [--python EXE]
```

The arguments mirror `openevolve-run.py`. The command translates the config into a normal run file
(`<output>/run.json`) and writes a per-key report (`<output>/openevolve-import.json`). It then runs the task, which you
can later continue with `aidotnet-evolve resume <output>/run.json`. The default output directory is
`openevolve_output`.

## How the config is read

- The file is laid over **OpenEvolve's own defaults**, so a key you leave out behaves as it does in OpenEvolve. For
  example, a config that never mentions islands gets OpenEvolve's 5 islands.
- Every key of OpenEvolve's `Config` has one of three dispositions, listed below:
  - **Mapped** means it is translated into the run file.
  - **NoEffect** means it is accepted and reported. It governs logging, storage or bookkeeping, never what the search
    does.
  - **RefusedUnlessDefault** means it is accepted at OpenEvolve's default value, where it changes nothing. Any other
    value stops the import with the key's name and the reason. Nothing is ignored silently.
- A key that isn't in OpenEvolve 0.3.2's `Config` at all is ignored by OpenEvolve too, because it loads the file with
  dacite, which drops unknown keys. The import accepts it and names it in the report, in case it is a misspelt real key.
- API keys are never read from the file. As in OpenEvolve's fallback, the key comes from `OPENAI_API_KEY`, and the base
  URL from `OPENAI_API_BASE` when `llm.api_base` is unset.

## How the evaluator runs

The evaluator runs unmodified, inside this CLI's process sandbox: a fresh process per evaluation, with the process tree
killed on a timeout and a memory cap enforced. A bundled shim reproduces OpenEvolve 0.3.2's evaluation semantics:

- `evaluate(program_path)`, or, when `evaluator.cascade_evaluation` is on and the file defines `evaluate_stage1`,
  the `evaluate_stage1..3` cascade.
- A cascade stage runs only when the previous stage passes its `cascade_thresholds` entry. The check uses
  `combined_score`, or else the mean of the numeric metrics other than `error`.
- Stage metrics are merged, with a later stage's value winning. A failed or timed-out stage keeps the earlier results and
  records `stageN_passed = 0`.
- The fitness is `combined_score`, or else the mean of numeric, non-boolean, non-NaN metrics outside
  `database.feature_dimensions`.
- Metrics and artifacts (dict returns or `EvaluationResult`) are carried into the run.

The shim records the evaluator's SHA-256. Editing the evaluator therefore changes the run's identity, and a changed file
is refused rather than mixed into an ongoing run.

## What differs, and is reported

- **Prompts.** OpenEvolve's prompt keys map onto our prompt options: templates, changes-description mode, template
  variations and the length-based hints. A ``system_message`` that names one of OpenEvolve's shipped templates (or a file
  in ``template_dir``) is used as a template; anything else is literal text. That is OpenEvolve's own guess, recorded
  explicitly.
- **LLM feedback.** ``evaluator.use_llm_feedback`` scores each program with ``llm.evaluator_models`` (or the proposal
  models) and blends the result in with ``llm_feedback_weight``.
- **Diversity axis.** OpenEvolve's built-in `diversity` feature is measured against the current population, so it has
  no deterministic equivalent. It is dropped, and `complexity` becomes program length.
- **Memory limit.** OpenEvolve declares `evaluator.memory_limit_mb` but does not enforce it. Here it is enforced, and
  when it is null the sandbox still caps each evaluation at 4096 MiB.
- **Checkpoints.** Ours checkpoints after every batch, so any `checkpoint_interval` is met.

## Every key

| key | disposition | here |
| --- | --- | --- |
| `max_iterations` | Mapped | budget.maxEvaluations = max_iterations + 1 (ours counts the seed); --iterations overrides it, as in OpenEvolve |
| `checkpoint_interval` | NoEffect | ours checkpoints after every batch, so any interval is met |
| `log_level` | NoEffect | logging only; every run writes a trace |
| `log_dir` | NoEffect | logs, trace and checkpoints go under --output |
| `random_seed` | Mapped | budget.seed; null (OpenEvolve's unseeded run) becomes 42 and is reported |
| `language` | Mapped | language; null means python, and a language without dedicated support (text, a prompt) is evolved as generic text |
| `file_suffix` | Mapped | the suffix of the candidate file the evaluator receives |
| `llm.api_base` | Mapped | model.endpoint for every model that does not set its own; OPENAI_API_BASE when unset, as in OpenEvolve |
| `llm.api_key` | NoEffect | never read from the file (examples ship placeholders); the key comes from OPENAI_API_KEY, as OpenEvolve falls back to |
| `llm.name` | Mapped | model.name when llm.models is absent |
| `llm.provider` | Mapped | model.provider: null or openai -> OpenAiCompatible, claude_code -> ClaudeCode; others are refused |
| `llm.init_client` | RefusedUnlessDefault | a Python callable cannot be called from the CLI |
| `llm.weight` | Mapped | model.weight, the default weight of each model |
| `llm.system_message` | NoEffect | OpenEvolve's prompt sampler always passes prompt.system_message, so a model's own is never used |
| `llm.temperature` | Mapped | model.temperature |
| `llm.top_p` | Mapped | model.topP |
| `llm.max_tokens` | Mapped | model.maxOutputTokens |
| `llm.timeout` | Mapped | model.timeoutSeconds |
| `llm.retries` | Mapped | model.maxRetries |
| `llm.retry_delay` | Mapped | model.retryDelaySeconds |
| `llm.random_seed` | RefusedUnlessDefault | model calls are seeded from the run seed, per proposal |
| `llm.reasoning_effort` | Mapped | model.reasoningEffort |
| `llm.max_budget_usd` | Mapped | model.maxBudgetUsd |
| `llm.manual_mode` | Mapped | model.provider = Manual |
| `llm._manual_queue_dir` | Mapped | model.manualQueue |
| `llm.models` | Mapped | model plus additionalModels, sampled by weight |
| `llm.evaluator_models` | Mapped | llmFeedback.models, the judging models; empty uses the proposal models, as in OpenEvolve |
| `llm.primary_model` | Mapped | model.name (OpenEvolve's older single-model form) |
| `llm.primary_model_weight` | Mapped | model.weight |
| `llm.secondary_model` | Mapped | a second model in additionalModels |
| `llm.secondary_model_weight` | Mapped | its weight |
| `prompt.template_dir` | Mapped | prompt.templateDirectory, relative to the working directory as in OpenEvolve; templates layered over ours by stem; a missing directory is an error |
| `prompt.system_message` | Mapped | prompt.systemMessage; a template name when a template of that name exists, literal text otherwise (OpenEvolve's guess, made explicit) |
| `prompt.evaluator_system_message` | Mapped | prompt.evaluatorSystemMessage, the LLM-feedback judge's system message |
| `prompt.programs_as_changes_description` | Mapped | prompt.programsAsChangesDescription |
| `prompt.system_message_changes_description` | Mapped | prompt.systemMessageChangesDescription, replacing that template's text as OpenEvolve does |
| `prompt.initial_changes_description` | Mapped | prompt.initialChangesDescription |
| `prompt.num_top_programs` | Mapped | prompt.numTopPrograms and search.topPrograms |
| `prompt.num_diverse_programs` | Mapped | the inspiration count n: island best, then top programs, then diverse ones up to n, as OpenEvolve fills it |
| `prompt.use_template_stochasticity` | Mapped | prompt.useTemplateStochasticity |
| `prompt.template_variations` | Mapped | prompt.templateVariations |
| `prompt.use_meta_prompting` | RefusedUnlessDefault | meta-prompting is not implemented in the CLI |
| `prompt.meta_prompt_weight` | RefusedUnlessDefault | meta-prompting is not implemented in the CLI |
| `prompt.include_artifacts` | Mapped | prompt.includeArtifacts and search.includeArtifacts |
| `prompt.max_artifact_bytes` | Mapped | prompt.maxArtifactBytes and search.maxArtifactBytes |
| `prompt.artifact_security_filter` | Mapped | prompt.artifactSecurityFilter |
| `prompt.suggest_simplification_after_chars` | Mapped | prompt.suggestSimplificationAfterChars |
| `prompt.include_changes_under_chars` | Mapped | prompt.includeChangesUnderChars |
| `prompt.concise_implementation_max_lines` | Mapped | prompt.conciseImplementationMaxLines |
| `prompt.comprehensive_implementation_min_lines` | Mapped | prompt.comprehensiveImplementationMinLines |
| `prompt.diff_summary_max_line_len` | RefusedUnlessDefault | diff summaries in prompts are not configurable in the CLI yet |
| `prompt.diff_summary_max_lines` | RefusedUnlessDefault | diff summaries in prompts are not configurable in the CLI yet |
| `prompt.code_length_threshold` | Mapped | prompt.suggestSimplificationAfterChars when suggest_simplification_after_chars is unset (its older name) |
| `database.db_path` | NoEffect | run state lives in checkpoints under --output |
| `database.in_memory` | NoEffect | run state lives in checkpoints under --output |
| `database.log_prompts` | NoEffect | prompt logging only; prompts are not written to the trace |
| `database.population_size` | Mapped | search.archiveCapacity, the most elites the archive holds |
| `database.archive_size` | Mapped | search.eliteArchiveSize, the global elite index programs are drawn from |
| `database.num_islands` | Mapped | search.islands |
| `database.elite_selection_ratio` | Mapped | search.topInspirations = max(1, int(num_diverse_programs x ratio)), OpenEvolve's top-program inspirations |
| `database.exploration_ratio` | Mapped | search.explorationRatio; the remainder after exploitation becomes search.eliteRatio |
| `database.exploitation_ratio` | Mapped | search.exploitationRatio |
| `database.diversity_metric` | RefusedUnlessDefault | only OpenEvolve's default diversity metric name is accepted |
| `database.feature_dimensions` | Mapped | search.metricDescriptors; complexity becomes program length, diversity is dropped and reported |
| `database.feature_bins` | Mapped | the bin count of each descriptor |
| `database.diversity_reference_size` | RefusedUnlessDefault | OpenEvolve's population-relative diversity feature is not reproduced |
| `database.migration_interval` | Mapped | search.migrationInterval |
| `database.migration_rate` | Mapped | search.migrationRate |
| `database.random_seed` | NoEffect | the run seed drives every random stream |
| `database.artifacts_base_path` | NoEffect | artifacts are kept with the run under --output |
| `database.artifact_size_threshold` | NoEffect | artifact storage placement only |
| `database.cleanup_old_artifacts` | NoEffect | artifact storage housekeeping only |
| `database.artifact_retention_days` | NoEffect | artifact storage housekeeping only |
| `database.max_snapshot_artifacts` | NoEffect | artifact storage housekeeping only |
| `database.novelty_llm` | RefusedUnlessDefault | LLM novelty judging is not wired into the CLI |
| `database.embedding_model` | RefusedUnlessDefault | embedding novelty is not wired into the CLI |
| `database.similarity_threshold` | RefusedUnlessDefault | embedding novelty is not wired into the CLI |
| `evaluator.timeout` | Mapped | openEvolveEvaluator.timeoutSeconds per stage and budget.evaluationTimeLimitSeconds, capped with a note so all stages fit the 24 h sandbox ceiling |
| `evaluator.max_retries` | Mapped | search.evaluationRetries |
| `evaluator.memory_limit_mb` | Mapped | search.evaluationMemoryLimitMb, enforced by the sandbox (OpenEvolve declares it but does not enforce it) |
| `evaluator.cpu_limit` | RefusedUnlessDefault | CPU-time limits are not configurable yet |
| `evaluator.cascade_evaluation` | Mapped | openEvolveEvaluator.cascade |
| `evaluator.cascade_thresholds` | Mapped | openEvolveEvaluator.cascadeThresholds, with OpenEvolve's threshold rule |
| `evaluator.parallel_evaluations` | Mapped | budget.parallelism |
| `evaluator.distributed` | RefusedUnlessDefault | distributed evaluation is not configured through this importer |
| `evaluator.use_llm_feedback` | Mapped | llmFeedback: a judge model's score blended into each fitness |
| `evaluator.llm_feedback_weight` | Mapped | llmFeedback.weight |
| `evaluator.enable_artifacts` | Mapped | artifacts are collected when true and ignored when false |
| `evaluator.max_artifact_storage` | NoEffect | artifact storage housekeeping only |
| `evolution_trace.enabled` | NoEffect | ours always writes trace-NNN.jsonl under --output |
| `evolution_trace.format` | NoEffect | ours always writes JSON lines |
| `evolution_trace.include_code` | NoEffect | the trace records identities; programs are in checkpoints and best.py |
| `evolution_trace.include_prompts` | NoEffect | the trace records identities; programs are in checkpoints and best.py |
| `evolution_trace.output_path` | NoEffect | the trace goes under --output |
| `evolution_trace.buffer_size` | NoEffect | the trace is flushed per event |
| `evolution_trace.compress` | NoEffect | the trace is not compressed |
| `diff_based_evolution` | Mapped | mode = Diff when true, FullRewrite when false |
| `max_code_length` | Mapped | budget.maxProgramChars |
| `diff_pattern` | RefusedUnlessDefault | ours reads OpenEvolve's default SEARCH/REPLACE block format only |
| `early_stopping_patience` | Mapped | search.earlyStoppingPatience, in evaluations |
| `convergence_threshold` | Mapped | search.earlyStoppingMinimumImprovement |
| `early_stopping_metric` | RefusedUnlessDefault | early stopping watches the fitness (combined_score); other metrics are not supported |
| `max_tasks_per_child` | NoEffect | every evaluation already runs in a fresh process |
