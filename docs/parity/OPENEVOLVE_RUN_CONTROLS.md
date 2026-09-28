# OpenEvolve parity: run controls (V1-60, #173)

Every run-control option of OpenEvolve 0.3.2 (commit 411fb59), with its equivalent here and the tests that prove it.
All of these already existed; this page is the verified mapping the v1 plan asked for.

| OpenEvolve option | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- |
| `max_iterations` | `EvolutionEngineOptions.MaxEvaluationAttempts` (also `MaxProposals`, `MaxGenerations`) | `EvolutionEngineTests`, `ProgramVariationPortfolioTests` |
| `checkpoint_interval` | `EvolutionEngineOptions.CheckpointInterval` with a checkpoint store | `DirectoryEvolutionCheckpointStoreTests`, `CliTests` |
| `early_stopping_patience` (positive) | `EvolutionEarlyStoppingOptions.PatienceEvaluations` | `EvolutionEarlyStoppingCriterionTests` |
| `early_stopping_patience` (negative: stop at a target) | `EvolutionEngineOptions.TargetQuality`, stop reason `TargetReached` | `EvolutionEvaluationParityTests`, `EvolutionSearchQualityProofTests` |
| `convergence_threshold` | `EvolutionEarlyStoppingOptions.MinimumImprovement` | `EvolutionEarlyStoppingCriterionTests` |
| `early_stopping_metric` | `EvolutionEarlyStoppingOptions.MetricName` (+ `MetricIsLowerBetter`, which OpenEvolve lacks) | `EvolutionEarlyStoppingCriterionTests` |
| `max_code_length` | `ProgramTaskOptions.MaxProgramChars`: rejected before evaluation, never truncated | `ProgramFoundationAdversarialTests`, `LlmProgramVariationOperatorTests` |
| `diff_based_evolution` | `ProgramEvolutionMode` (diff or full rewrite) | `LlmProgramVariationOperatorTests` |
| `diff_pattern` | `ProgramDiffOptions.SearchMarker` / `DividerMarker` / `ReplaceMarker` | `ProgramDiffTests` |
| `language` / `file_suffix` | `ProgramLanguage`; the CLI derives the file extension from it | `ProgramDiffTests`, `CliTests` |
| `random_seed` | `EvolutionEngineOptions.Seed` (deterministic replay, including with several workers) | `EvolutionEngineTests` |
| `database.num_islands` | `EvolutionEngineOptions.IslandCount` | `EvolutionCoreParityTests` |
| `database.migration_interval` / `migration_rate` | `MigrationInterval` / `MigrationRate` (+ `MigrationTopology`, `MigrationTrigger`) | `EvolutionMigrationTopologyTests`, `EvolutionCoreParityTests` |
| `database.population_size` | archive capacity and `MaxRetainedFailures` | `MapElitesArchiveTests`, `EvolutionCoreParityTests` |
| `max_tasks_per_child` | not needed: each sandboxed evaluation runs in its own process, so nothing accumulates in a worker | `ProcessProgramExecutionEngineTests` |

## Differences worth knowing
- A time limit (`EvolutionEngineOptions.TimeLimit`, stop reason `TimeLimitReached`) has no OpenEvolve equivalent.
- OpenEvolve's early stopping always maximises; `MetricIsLowerBetter` lets a lower-is-better metric drive it here.