# V1-75 (#180): in-flight utilisation against a slow model

Measured at commit `4c31b32` on one Windows 11 workstation, .NET 10 Release.
The model latency is log-normal: median 100 ms, p95 400 ms, capped at 2 s. That is the story's 10 s / 40 s scaled down 100x,
so a run finishes in minutes. The evaluator is constant-cost arithmetic, so the only thing that can stretch wall-clock
beyond the model's own latency is how well the engine keeps calls in flight.

- **Utilisation**: summed model-call time / (wall-clock x concurrency). Target >= 0.95.
- **Wall / ideal**: wall-clock / (summed model-call time / concurrency). Target <= 1.05.

Every run uses 2,000 proposals at concurrency 16. Raw rows: `runs.jsonl` (ours) and `openevolve.jsonl`.

| Configuration | Runs | Utilisation | Wall / ideal |
| --- | --- | --- | --- |
| Batch dispatch | 1 | 0.062 | 16.01 |
| `Dispatch = Auto` (resolves to Pipeline, wave 64) | 1 | 0.576 | 1.735 |
| Pipeline, wave 256 | 1 | 0.881 | 1.135 |
| Continuous, deterministic, window 16 | 1 | 0.240 | 4.161 |
| **Continuous, deterministic, window 512** | 5 | **0.961-0.963** | **1.039-1.041** |
| **Continuous, opportunistic, window 16** | 5 | **0.961-0.962** | **1.040-1.041** |
| OpenEvolve 0.3.2 (411fb59), `parallel_evaluations = 16` | 3 | 0.402-0.416 | 2.401-2.488 |

## What meets the target, and what does not

- **Continuous dispatch meets both targets** when the variation operator is concurrency-safe
  (`IDeterministicConcurrentVariationOperator`, which `LlmProgramVariationOperator` implements when
  `LlmProgramVariationOptions.ConcurrentProposals` is set). It keeps up to `Pipeline.MaxProposalConcurrency`
  model calls running.
  - Opportunistic execution (commits in completion order) gets there with a window equal to the concurrency.
  - Deterministic execution commits in identifier order, so one slow call holds up the rest. It needs a window of
    about 32x the concurrency (512 here). A proposal is admitted once the commits reach its identifier minus half
    the window, which is what makes the run replay exactly whatever order the calls finish in.
- **`Dispatch = Auto` does not meet the target.** It still resolves to Pipeline for a latency-bound operator,
  as the story's third criterion requires (`DispatchAutoTests`). Pipeline waits at every wave boundary, so it
  reaches 0.88 only at wave 256. Choose Continuous explicitly for model-bound runs.
- **Batch dispatch serialises** a latency-bound operator: one call at a time.

## OpenEvolve

`benchmarks/external/openevolve_latency_run.py` runs the same latency distribution through OpenEvolve's controller with
an in-process slow LLM and the same constant-cost evaluator. OpenEvolve keeps about 6.5 of 16 slots busy.

## Reproduce

```text
dotnet build benchmarks/EvolutionLatency -c Release
LATENCY_MODE=Opportunistic LATENCY_WINDOW_MULTIPLE=1 dotnet benchmarks/EvolutionLatency/bin/Release/net10.0/EvolutionLatency.dll 2000 16 Continuous
LATENCY_WINDOW_MULTIPLE=32 dotnet benchmarks/EvolutionLatency/bin/Release/net10.0/EvolutionLatency.dll 2000 16 Continuous
python benchmarks/external/openevolve_latency_run.py <openevolve checkout> 2000 16
```
