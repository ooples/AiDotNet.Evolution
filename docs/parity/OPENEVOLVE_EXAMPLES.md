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
supports is mapped, and any key it does not support is refused by name. On its own it does **not**
mean a 3-iteration search has been run end to end. That is the smoke run below, which covers three
examples (`circle_packing`, `function_minimization`, `signal_processing`) against a loopback stub
model; the other examples have import coverage only.

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

Each case asserts at least one evaluation, at least one model call, and a **positive** best
quality. The last one is what makes the test mean anything: these evaluators return a zero score on
error, so evaluations would also be counted by a run where upstream's code never executed. It shows
that upstream's evaluator scored upstream's seed and that the model was asked; it does not show that
a proposal was scored, because these examples validate a proposal first and refuse the stub's fixed
answer.

**Five of 21 examples are smoked.** Which ones was measured, not assumed, with
`eng/probe-openevolve-examples.py`: it runs every example's evaluator directly on its own seed
program in a Linux container carrying the dependency set the upstream-parity job installs (numpy,
scipy, pyyaml, dacite, openai and the `openevolve` package). An example whose own seed scores zero
cannot support the positive-quality assertion, so it is not smoked - and a positive seed score is
necessary but not sufficient, as the three rows below the smoked ones show.

| example | seed score | smoked | why not |
| --- | --- | --- | --- |
| `circle_packing` | 0.364 | yes | |
| `function_minimization` | 1.398 | yes | also run end to end against the real provider, 4 completed evaluations and `combined_score` 1.4221 at the seed to 1.4988 at best, with only `llm.provider` and the two model names changed - see [evidence](../../benchmarks/evidence/openevolve-examples/767b31a-function-minimization/README.md) |
| `signal_processing` | 0.420 | yes | |
| `claude_code_quickstart` | 1.424 | no (stub) | sets `llm.provider: "claude_code"`, so proposals route to the Claude Code CLI rather than an HTTP endpoint and a loopback stub has nothing to answer. **It is not that the example fails:** with the real provider it runs unmodified and the search improves the program, 1.0567 -> 1.4837 -> 1.4956 over two proposals, with no config key rewritten - see [evidence](../../benchmarks/evidence/openevolve-examples/767b31a-claude-code-quickstart/README.md). It stays out of the smoke list because that list is stub-driven and CI spends nothing |
| `background_blur` | 0.992 | yes | |
| `circle_packing_with_artifacts` | 0.364 | yes | |
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

The smoke runs on Linux only. *Some* upstream evaluators write a wrapper script that embeds the
program's absolute path into a single-quoted Python literal, so a Windows path becomes an invalid
escape - `SyntaxError: (unicode error) 'unicodeescape' codec can't decode bytes ... truncated
\UXXXXXXXX escape` - and every candidate scores zero. Measured by running
`examples/circle_packing/evaluator.py` directly on its own seed program on both platforms.

**Through this CLI the path *we* supply is no longer the problem**, because the shim hands the evaluator
a path whose separators are forward slashes: valid inside a single-quoted literal, accepted by every
Windows path API, and already what the separator is on Linux, so it changes nothing there. See *The path
the shim hands an evaluator* below.

**That is not the whole story for `circle_packing`, and an earlier version of this section overclaimed.**
Its evaluator embeds **two** paths in single-quoted literals: `program_path` at `evaluator.py:99`, which
the shim now makes safe, and **its own** `NamedTemporaryFile` path at lines 123 and 131
(`open('{temp_file.name}.results', 'wb')`). The second is created inside the evaluator from the system
temp directory, so nothing this CLI passes can influence it. Measured on Windows at the pinned ref,
through the CLI with the real provider: 4 completed evaluations, **every one `combined_score: 0`,
`validity: 0`**, including upstream's own seed - and running the evaluator directly reproduces the same
`truncated \UXXXXXXXX escape`, this time on `C:\Users\...\tmpXXXXXXXX.py.results`.

The evaluator reports that as a completed evaluation scoring zero rather than as a failure, which is why
a run looks healthy - no retained failures, four completed evaluations - while measuring nothing.

**`circle_packing_with_artifacts` is the same**, measured the same way: its evaluator carries six
occurrences of the `NamedTemporaryFile`-in-a-literal shape, and on Windows its seed reports
`combined_score` 0.0 with `validity` 0.0 and the same `truncated \UXXXXXXXX escape`. So of the five smoked
examples, **two smoke on Linux only for a reason inside the evaluator** rather than anything about this
CLI, and `background_blur` was chosen for the real-provider run precisely because it has none of that
shape.

The smoke stays on Linux because one Theory covers every example, because `rust_adaptive_sort` needs a
Rust toolchain and `r_robust_regression` needs R, **and** because `circle_packing` remains zero-scoring on
Windows for the reason above.

## The same catalogue measured on Windows, for free

The smoke above runs in a Linux container. Preflight makes **no model calls**, so the same examples can be
scored on Windows at no cost, and all **11 of 21 that ship a config, an evaluator and a seed program** have
been: [`benchmarks/evidence/openevolve-examples/86d56c9-windows-preflight`](../../benchmarks/evidence/openevolve-examples/86d56c9-windows-preflight/README.md).

It separates two failure modes that the smoke's pass/fail cannot:

| mode | examples | what a caller sees |
| --- | --- | --- |
| **fails loudly** - a missing toolchain or module | `attention_optimization` (`mlir-opt`), `mlx_metal_kernel_opt` (Apple MLX) | `Failed`, `program_script_script_failed`, traceback in the report when the artifact flags are on |
| **completes at zero** - a missing dataset, or a path the evaluator cannot embed | `arc_benchmark`, `circle_packing_with_artifacts`, `k_module_problem`, `online_judge_programming`, `web_scraper_optillm` | `Completed`, quality 0, **no failure of any kind** |

**Five of the eleven are in the second mode**, which is the one worth knowing about: nothing in the status
distinguishes it from a working example whose candidate was simply bad. Three of those five are the ones
this ledger already excludes for *"its own seed scores zero"* - a judgement made before the measurement,
and confirmed by it.

`claude_code_quickstart` scores 1.3905 under preflight, because what stops it smoking is the provider
rather than the evaluator.

## What `circle_packing_with_artifacts` needs that the others do not

It smokes, and it is the one smoked example whose evaluator imports **upstream's own package**:
`from openevolve.evaluation_result import EvaluationResult`, which in turn imports `dacite`. On a bare
checkout that fails before any scoring happens, so the dependency is part of the example rather than an
accident of our harness - the CI job installs the package, which is why the smoke passes there.

Measured in a Linux container with `PYTHONPATH` at the pinned checkout and `dacite` installed: the seed
evaluates and reports **both** declared feature dimensions - `radius_variance` 0.0266, `spatial_spread`
0.2381, `combined_score` 0.3642, matching the seed score in the table above. So "the evaluator omits a
declared dimension" never described this example; the archive was demanding coordinates nobody promoted,
which is the clone defect fixed in #243.

For a run of your own, the sandbox clears the child environment and does not inherit `PYTHONPATH`, so name
the checkout explicitly (added in #245):

```text
aidotnet-evolve run --openevolve-config config.yaml initial_program.py evaluator.py --python-path /path/to/openevolve
```

Installing the package into the interpreter passed to `--python` works too and needs no option.

**It is a property of those evaluators, not of the platform**, and the distinction matters for
anyone wanting a real run without a container: run directly, the same shape defeats
`online_judge_programming`, `k_module_problem` and `web_scraper_optillm`, while
`examples/function_minimization/evaluator.py` runs correctly on Windows, scoring its own seed
`combined_score` **1.4546** there (measured directly, with `openevolve` and `dacite` importable).

## The path the shim hands an evaluator

The shim gives `evaluate(program_path)` a path with forward slashes, not the platform separator. An
evaluator that only opens the file cannot tell the difference; an evaluator that embeds the path in a
generated script can, and that is the whole failure above.

Measured on Windows, both ways, with an evaluator that reproduces the embedding shape:
`combined_score` **1.0** with the replacement and **0.0** without it, the failure being the
`SyntaxError` on the embedded path. The test asserts the contract directly - the path holds no
separator a literal would reinterpret, and still opens - so on Linux it holds trivially and on
Windows it is the real check. It is **not** a measurement of `circle_packing` itself through the CLI
on Windows: that needs an upstream checkout plus `dacite` on the interpreter, and the number that run
would report is the example's score rather than this contract.

## The smoke covers the cascade path

All five smoked examples import with cascade evaluation enabled and their own thresholds mapped —
`circle_packing` 0.5/0.75, `function_minimization` 1.3, `signal_processing` 0.3/0.6,
`background_blur` 0.5/0.5, `circle_packing_with_artifacts` 0.5/0.8 — and each evaluator defines
`evaluate_stage1` and `evaluate_stage2`; `background_blur` defines a third.

**`function_minimization`'s config never mentions `cascade_evaluation`**, which looks like a discrepancy
until you check the default: upstream declares `cascade_evaluation: bool = True` (`openevolve/config.py`),
so an absent key means enabled, and this import agrees - measured by importing that config unchanged, which
yields `cascade: true` with `cascadeThresholds: [1.3]`. So the smoke drives
V1-56's cascade criterion against real upstream evaluators, not only against the synthetic config in
`OpenEvolveImportTests`, which is where the stage ordering and threshold semantics are actually
pinned by assertion.

Stated precisely, because the distinction matters: the smoke asserts that such a run **completes**
with a positive quality, not that stage two ran on a particular candidate. Counting stages would
need an observable the run result does not currently expose; the synthetic test proves the stage
semantics by side effect (`stage2-3`, `stage2-4` files) and that remains the evidence for the
criterion itself.

## The feature-dimension gap, and what it was hiding

**Closed.** Both examples now smoke. The section is kept because the two causes behind it are the
kind that look identical from the outside, and only one was the one everybody suspected.

`background_blur` and `circle_packing_with_artifacts` were not blocked by anything about their
evaluators. They were blocked by `database.feature_dimensions`.

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

**The cause was ours and it was one line.** `ProgramProposalOptions.Clone()` hand-listed seven of
its base's eight properties and omitted `MetricDescriptors`. `ProgramEvolutionTask` snapshots its
options through `Clone`, so the CLI's promotion list - every declared OpenEvolve feature dimension -
was empty before the first evaluation, while the archive still demanded those coordinates. Nothing
was wrong with the dimension, the evaluator or the bin edges. The four tests covering descriptor
promotion all used the base options type, which is why none of them caught it.

**And it was hiding a second, unrelated cause in the harness.** With the clone fixed,
`circle_packing_with_artifacts` smoked immediately; `background_blur` still failed, now with
`all 9 model calls failed` and its seed scored - so the descriptor promotion was working and
something else was wrong. The smoke rewrites `llm.api_base` to a loopback stub, but
`OpenEvolveConfigImporter.ToModel` reads an entry's **own** `api_base` in preference to the
section's, and `background_blur` is the one example in the corpus that sets it per model. Every call
went to `openrouter.ai`. Redirecting per-model endpoints too makes it pass.

Worth stating plainly because the first fix would otherwise have looked incomplete: one example
proved the engine fix, the other needed the engine fix **and** a correct instrument, and a single
run of the pair could not have told those apart.

## Every feature dimension the corpus declares

`database.feature_dimensions` is the key that cost the most to get right, so its coverage is a
census rather than a sample. Every value declared anywhere under `examples/` at the pinned ref:

| dimension | declarations | treatment |
| --- | --- | --- |
| `complexity` | 27 | mapped to `length`, which the engine always computes |
| `score` | 17 | dropped with a note: it is the fitness itself, not a reported metric, and this archive has no quality axis |
| `diversity` | 9 | dropped with a note: OpenEvolve's axis is population-relative and has no deterministic equivalent |
| `combined_score` | 8 | passed through as an evaluator metric |
| `prompt_length`, `reasoning_strategy` | 3 each | passed through as evaluator metrics |
| `ssim`, `radius_variance`, `spatial_spread`, `performance_score`, `adaptability_score`, `outlier_robustness`, `execution_time` | 1 each | passed through as evaluator metrics |

The two dropped axes are dropped **by name, with a note** — the same treatment the parity checklist
requires for any key we do not support, rather than silent omission. Everything passed through is
requested of the evaluator on every completed evaluation, which is what the import note warns about:
an evaluator that reports its metric only on a success path will end the run with no candidates.

`score` was the interesting one. The importer's own comment already said it was the fitness rather
than a metric, while the code beside it passed the name through — so all 17 declarations would have
demanded a metric literally called `score`, which none of those evaluators report. That was found by
asking which real configs the new import note would fire on, not by reading the examples that had
already failed.

## The two that do not

Neither is a defect in the importer, and both are refusals by name rather than silent acceptance:

- **`sldbench`** configures no model at all. Upstream's own configs set `llm.primary_model: null`
  and the example's `run.sh` supplies the model through environment variables. Importing it would
  mean inventing a model the config does not name.
- **`symbolic_regression`** ships no config. Its `scripts.sh` expects a per-problem `config.yaml`
  that the user generates and skips the problem when the file is absent, so there is nothing in the
  repository to import.
