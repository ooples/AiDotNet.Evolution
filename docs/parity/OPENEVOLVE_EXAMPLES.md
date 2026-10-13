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

## The two that do not

Neither is a defect in the importer, and both are refusals by name rather than silent acceptance:

- **`sldbench`** configures no model at all. Upstream's own configs set `llm.primary_model: null`
  and the example's `run.sh` supplies the model through environment variables. Importing it would
  mean inventing a model the config does not name.
- **`symbolic_regression`** ships no config. Its `scripts.sh` expects a per-problem `config.yaml`
  that the user generates and skips the problem when the file is absent, so there is nothing in the
  repository to import.
