# OpenEvolve examples: import ledger

Every example in OpenEvolve 0.3.2 at the pinned CI ref `411fb59c886c18704caaffb611e17cf9e7d824d2`,
with whether `OpenEvolveConfigImporter` accepts its config unmodified, or the tracked reason it does
not (V1-56, #169).

`OpenEvolveExampleLedgerTests` parses this file, so a row cannot be edited without the counts being
rechecked, and with `EVOLUTION_OPENEVOLVE_CHECKOUT` set it re-measures every row against the
checkout rather than trusting the table.

**19 of 21 import. Target: at least 18.**

## What "imports" means here, and what it does not

It means the importer accepts the example's own config and produces a run file: every key it
supports is mapped, and any key it does not support is refused by name. It does **not** mean a
3-iteration search has been run end to end - that needs a model, and no model is contacted in CI.
The remainder of #169's third criterion is the smoke run itself, which is still open.

The config chosen for each example is the shortest path among its `*.yaml` and `*.yml` files, which
is the one an example's own README points at. Examples carrying several (`algotune`,
`alphaevolve_math_problems`, `circle_packing`, `sldbench`) have their whole set covered by
`test_openevolve_configs.py` for the two families it knows; this ledger is one row per example.

## The ledger

| example | config | imports | notes or reason |
| --- | --- | --- | --- |
| `algotune` | `examples/algotune/fft_convolution/config.yaml` | yes | 3 mapping note(s) |
| `alphaevolve_math_problems` | `examples/alphaevolve_math_problems/matmul/config.yaml` | yes | 2 mapping note(s) |
| `arc_benchmark` | `examples/arc_benchmark/config.yaml` | yes | 3 mapping note(s) |
| `attention_optimization` | `examples/attention_optimization/config.yaml` | yes | 4 mapping note(s) |
| `background_blur` | `examples/background_blur/config.yaml` | yes | 1 mapping note(s) |
| `circle_packing` | `examples/circle_packing/config_phase_1.yaml` | yes | 3 mapping note(s) |
| `circle_packing_with_artifacts` | `examples/circle_packing_with_artifacts/config.yaml` | yes | 2 mapping note(s) |
| `claude_code_quickstart` | `examples/claude_code_quickstart/config.yaml` | yes | 2 mapping note(s) |
| `function_minimization` | `examples/function_minimization/config.yaml` | yes | 2 mapping note(s) |
| `k_module_problem` | `examples/k_module_problem/config.yaml` | yes | 2 mapping note(s) |
| `llm_prompt_optimization` | `examples/llm_prompt_optimization/config.yaml` | yes | 2 mapping note(s) |
| `lm_eval` | `examples/lm_eval/config.yml` | yes | 4 mapping note(s) |
| `mlx_metal_kernel_opt` | `examples/mlx_metal_kernel_opt/config.yaml` | yes | 3 mapping note(s) |
| `online_judge_programming` | `examples/online_judge_programming/config.yaml` | yes | 4 mapping note(s) |
| `r_robust_regression` | `examples/r_robust_regression/config.yaml` | yes | 1 mapping note(s) |
| `rust_adaptive_sort` | `examples/rust_adaptive_sort/config.yaml` | yes | 1 mapping note(s) |
| `signal_processing` | `examples/signal_processing/config.yaml` | yes | 2 mapping note(s) |
| `sldbench` | `examples/sldbench/configs/moe_scaling_law.yaml` | no | Every `configs/*.yaml` sets `llm.primary_model: null`; the example supplies the model through `run.sh` environment variables instead. An explicitly null model is refused by name rather than silently defaulted. |
| `symbolic_regression` | none | no | Ships no config: `scripts.sh` expects a per-problem `config.yaml` the user generates, and skips the problem when it is absent, so there is nothing in the repository to import. |
| `tsp_tour_minimization` | `examples/tsp_tour_minimization/config.yaml` | yes | 4 mapping note(s) |
| `web_scraper_optillm` | `examples/web_scraper_optillm/config.yaml` | yes | 2 mapping note(s) |

## The smoke run

`OpenEvolveExampleSmokeTests` takes the other half of the criterion: an example's own `evaluator.py`
and `initial_program.*`, unmodified, driven through `aidotnet-evolve run --openevolve-config` for
three iterations against a loopback stub model. Three keys in a *copy* of the config change, all
about not spending money or reaching the network - `llm.api_base`, the model names, and
`diff_based_evolution` so the stub's whole-program answer is usable.
`benchmarks/external/test_openevolve_configs.py` treats an endpoint rewrite the same way, exempting
`llm.api_base/api_key` from its verbatim check.

Each case asserts four evaluations (the seed plus three), at least one model call, and a
**positive** best quality. The last one is what makes the test mean anything: these evaluators
return a zero score on error, so four evaluations would also be counted by a run where upstream's
code never executed.

**Three of 21 examples are smoked.** Which ones was measured, not assumed, with
`eng/probe-openevolve-examples.py`: it runs every example's evaluator directly on its own seed
program in a Linux container carrying the dependency set the upstream-parity job installs (numpy,
scipy, pyyaml, dacite, openai and the `openevolve` package). An example whose own seed scores zero
cannot support the positive-quality assertion, so it is not smoked - and a positive seed score is
necessary but not sufficient, as the three rows below the smoked ones show.

| example | seed score | smoked | why not |
| --- | --- | --- | --- |
| `circle_packing` | 0.364 | yes | |
| `function_minimization` | 1.398 | yes | |
| `signal_processing` | 0.420 | yes | |
| `claude_code_quickstart` | 1.424 | no | sets `llm.provider: "claude_code"`, so proposals route to the Claude Code CLI rather than an HTTP endpoint and a loopback stub has nothing to answer: all nine attempts fail |
| `background_blur` | 0.992 | no | **its declared `database.feature_dimensions` empty the archive.** The run stops with `StopReason: NoCandidates` after one evaluation, a null best quality and zero model calls; removing only that key from the config copy makes the same example run and pass. See *The feature-dimension gap* below |
| `circle_packing_with_artifacts` | 0.364 | no | same mechanism as `background_blur` |
| `k_module_problem` | 0.0 | no | its own seed scores zero, so a positive-quality assertion cannot distinguish a working run from a broken one |
| `online_judge_programming` | 0.0 | no | same, and its evaluator needs `requests` |
| `web_scraper_optillm` | 0.0 | no | same |
| `r_robust_regression` | 0.0 | no | same; its `evaluate` is a coroutine and needs R |
| `rust_adaptive_sort` | 0.0 | no | same; needs a Rust toolchain to score above zero |
| `lm_eval` | n/a | no | its evaluator is a stub returning `not_implemented` |
| `arc_benchmark` | n/a | no | reads a dataset from the absolute path `/workspaces/ARC-Evolve/data/arc-prize-2025/...` |
| `attention_optimization` | n/a | no | `RuntimeError: Required tool not found: mlir-opt` |
| `mlx_metal_kernel_opt` | n/a | no | needs Apple MLX (`No module named 'mlx'`) |
| `sldbench` | n/a | no | needs HuggingFace `datasets` |
| `tsp_tour_minimization` | n/a | no | its `initial_program` is a directory of per-problem programs, so there is no single seed; its evaluator also needs `psutil` |
| `algotune` | n/a | no | no top-level evaluator; one per problem subdirectory |
| `alphaevolve_math_problems` | n/a | no | same |
| `llm_prompt_optimization` | n/a | no | no seed program; it evolves a prompt file |
| `symbolic_regression` | n/a | no | no config and no seed program in the repository |

The smoke runs on Linux only, and that is upstream's constraint rather than a choice: their
evaluator writes a wrapper script that embeds the program's absolute path into a single-quoted
Python literal, so on Windows the path becomes an invalid escape - `SyntaxError: (unicode error)
'unicodeescape' codec can't decode bytes ... truncated \UXXXXXXXX escape` - and every candidate
scores zero. Measured by running `examples/circle_packing/evaluator.py` directly on its own seed
program on both platforms.

## The feature-dimension gap

`background_blur` and `circle_packing_with_artifacts` are not blocked by anything about their
models, their endpoints or their evaluators. They are blocked by `database.feature_dimensions`.

Measured with a within-run control: the same example ran twice in one process against one build,
once with its config as upstream ships it and once with only the `feature_dimensions` key removed.
The first stops with `StopReason: NoCandidates`, `CompletedEvaluations: 1`, `BestQuality: null` and
`ModelUsage.Proposals: 0`. The second completes three iterations and passes every assertion. Nothing
else differed, so a build, environment or ordering difference cannot explain it.

A declared feature dimension therefore leaves the seed candidate unplaceable, the archive empty, and
the run with no parent to propose from. Upstream runs these examples with those dimensions, so this
is a parity gap on our side rather than a property of the examples — which makes it #169's business
("every supported key maps to ours"), not a reason to exclude them permanently.

The engine was holding the answer all along: it retains a `descriptor_missing` diagnostic whose
`data.descriptor` names the dimension, and the CLI never printed it - a run reported
`StopReason: NoCandidates` and nothing else. The result now carries `Failures` (code, message and
data per retained failure) and stderr names the first one, so this diagnosis takes one run instead
of the three sessions it took here.

**Still open:** which of the declared dimensions does it (`background_blur` names
`complexity` and `ssim`) and why the candidate ends up unplaceable rather than clamped into a bin.
The two examples stay out of the smoke until that is fixed, rather than being asserted on.

## The two that do not

Neither is a defect in the importer, and both are refusals by name rather than silent acceptance:

- **`sldbench`** configures no model at all. Upstream's own configs set `llm.primary_model: null`
  and the example's `run.sh` supplies the model through environment variables. Importing it would
  mean inventing a model the config does not name.
- **`symbolic_regression`** ships no config. Its `scripts.sh` expects a per-problem `config.yaml`
  that the user generates and skips the problem when the file is absent, so there is nothing in the
  repository to import.
