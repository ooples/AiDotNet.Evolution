# Preflight across eleven examples on Windows, and what the zero-seed notice catches

Measured with `aidotnet-evolve preflight` at main `86d56c9` against upstream's pinned ref `411fb59`. **No
model calls**: preflight scores the seed and nothing else, so this costs nothing and can be re-run freely.

Each example was staged with its own `config.yaml`, `evaluator.py` and `initial_program.py` unmodified, and
a minimal run file naming only the interpreter and the module path.

| example | seed status | seed quality | zero-seed notice |
| --- | --- | ---: | --- |
| `background_blur` | Completed | 1.0021 | - |
| `function_minimization` | Completed | 1.3919 | - |
| `signal_processing` | Completed | 9.1175 | - |
| `circle_packing_with_artifacts` | Completed | **0** | **yes** |
| `k_module_problem` | Completed | **0** | **yes** |
| `online_judge_programming` | Completed | **0** | **yes** |
| `web_scraper_optillm` | Completed | **0** | **yes** |

The remaining four examples with a complete config/evaluator/seed triple, measured the same way:

| example | ledger's reason | seed status | seed quality | notice |
| --- | --- | --- | ---: | --- |
| `claude_code_quickstart` | routes to the Claude Code CLI, so a loopback stub has nothing to answer | Completed | 1.3905 | - |
| `arc_benchmark` | reads a dataset from an absolute path | Completed | **0** | **yes** |
| `attention_optimization` | needs `mlir-opt` | **Failed** | - | `program_script_script_failed` |
| `mlx_metal_kernel_opt` | macOS only, Apple MLX | **Failed** | - | `program_script_script_failed` |

`claude_code_quickstart` scores because preflight makes no model calls at all: the thing that stops it
smoking is the provider, not the evaluator.

## Two failure modes, and only one of them is loud

That completes all **11 of 21** examples that ship a config, an evaluator and a seed program, and the split
is the useful part:

- **A missing toolchain or module fails** - `attention_optimization` and `mlx_metal_kernel_opt` both report
  `Failed` with `program_script_script_failed`, and with `search.collectArtifacts` and
  `search.retainArtifactText` on, the traceback reaches the run report.
- **A missing dataset completes at zero** - `arc_benchmark` reads from an absolute path that does not exist
  here, and its evaluator turns that into a completed evaluation scoring 0 rather than a failure. Nothing
  about the status distinguishes it from a working example whose candidate was simply bad.

The second mode is the one the zero-seed notice exists for, and it is not hypothetical: **5 of the 11**
land in it.

## Why this calibrates the notice rather than just exercising it

Three of the first table's four zeros - `k_module_problem`, `online_judge_programming` and
`web_scraper_optillm` - are
examples [the ledger already excludes](../../../docs/parity/OPENEVOLVE_EXAMPLES.md) for exactly this
reason: *"its own seed scores zero, so a positive-quality assertion cannot distinguish a working run from a
broken one"*. That judgement was made independently of this guard, and the guard agrees with it on every
one.

The fourth, `circle_packing_with_artifacts`, is the Windows temp-path case: its evaluator embeds its own
`NamedTemporaryFile` path in a single-quoted Python literal, so on Windows every candidate scores zero
while the evaluation still reports as completed.

**Nine of the eleven rows report `Completed`, and five of those nine scored 0.** Only the two
missing-toolchain rows are loud. So a caller reading `seed_status` alone counts nine working examples where
four actually produced a positive seed score - which is the whole point: status does not identify a healthy
example.

## What this is not

- **Not a comparison of example difficulty.** `signal_processing` scores 9.1175 here and 13.73 in the
  real-provider run beside it, because that run used the example's own config while this one uses a minimal
  run file; the two numbers are not the same measurement and neither is a baseline for the other.
- **Not a Windows verdict on the other fourteen examples**: four have no complete config/evaluator/seed
  triple, and the rest need toolchains, datasets or a real provider that preflight deliberately avoids.
