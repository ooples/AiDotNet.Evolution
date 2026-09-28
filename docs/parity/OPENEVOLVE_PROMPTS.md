# OpenEvolve parity: prompt context (V1-52, #165)

Every `PromptConfig` option of OpenEvolve 0.3.2 (commit 411fb59), with its equivalent in
`ProgramEvolutionPromptOptions` (or the variation options) and the test that proves it.

| OpenEvolve option | AiDotNet.Evolution equivalent | Proven by |
| --- | --- | --- |
| `template_dir` | `TemplateDirectory` | `ProgramPromptBuilderTests` |
| `system_message` / `evaluator_system_message` | `SystemMessage` (+ `SystemMessageMode`) / `EvaluatorSystemMessage` | `ProgramPromptBuilderTests` |
| `num_top_programs` / `num_diverse_programs` | `NumTopPrograms` / `NumDiversePrograms` | `ProgramPromptBuilderTests` |
| previous attempts | `IncludePreviousAttempts`, `NumPreviousAttempts` | `ProgramPromptBuilderTests` |
| `diff_summary_max_lines` / `diff_summary_max_line_len` | `DiffSummaryMaxLines` / `DiffSummaryMaxLineLength` (opt-in) | `ProgramPromptBuilderTests` |
| `include_artifacts` / `max_artifact_bytes` / `artifact_security_filter` | `IncludeArtifacts` / `MaxArtifactBytes` / `ArtifactSecurityFilter` | `ProgramPromptBuilderTests` |
| `use_template_stochasticity` / `template_variations` | `UseTemplateStochasticity` / `TemplateVariations` (drawn from the seeded stream) | `ProgramPromptBuilderTests` |
| `programs_as_changes_description` / `initial_changes_description` | `ProgramsAsChangesDescription` / `InitialChangesDescription` | `ProgramPromptBuilderTests` |
| `suggest_simplification_after_chars`, `include_changes_under_chars`, `concise_implementation_max_lines`, `comprehensive_implementation_min_lines` | the options of the same names | `ProgramPromptBuilderTests` |
| `use_meta_prompting` | not implemented in OpenEvolve; tracked as V1-53 (#166) | n/a |

## Beyond OpenEvolve: experience memory
`LlmProgramVariationOptions.Experience` (a `ProgramExperienceBinding`) appends lessons for this task and version to each
proposal prompt, within a character budget, and a `ProgramExperienceRecorder` attached to the engine records every
evaluated program as a lesson. Lessons never cross task identity or version, and final-test evidence never enters the
store. Proven by `ProgramExperienceRecorderTests` and `LlmProgramVariationOperatorTests`.