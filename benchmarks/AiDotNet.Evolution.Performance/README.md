# Engine performance suite

BenchmarkDotNet 0.15.8 is confined to this executable; the core package has no benchmark dependency.
These measure orchestration and data-structure cost, not the quality or runtime speed of evolved algorithms.

| Fixture | Factors | What is timed |
| --- | --- | --- |
| Engine | 1/4 workers; batch/continuous; 0/1 ms asynchronous evaluator delay; checkpoints off/on | Complete 256-evaluation run, including eight seeds, proposal/canonicalization/archive work, final state and optional in-memory checkpoints |
| Evaluator-only control | Same worker count, evaluator and 256-call count | Evaluation of precomputed genomes with parallel scheduling; a lower bound, not an evolutionary algorithm or quality baseline |
| Archive | 100/1,000/10,000 occupied cells | Cell lookup, uniform sampling and immutable snapshot creation; construction excluded |
| Checkpoint | 32/256/2,048 prior evaluations | Checksum envelope creation, in-memory clone load, or fresh-store population plus engine restore without new evaluations |

Every fixture verifies its claimed work. Engine counters must reach the evaluation cap, archives must fill the
declared cells, and restored checkpoints must preserve the exact state hash. Fixtures do not accumulate archives
or mutate the next invocation's starting checkpoint. Numeric genomes include their real typed-space costs.

## Run

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --smoke
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*ArchiveBenchmarks*' --job Short --exporters json
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*EngineBenchmarks*' --exporters json
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*CheckpointBenchmarks*' --exporters json
```

The default BenchmarkDotNet job performs adaptive warmup/measurement; `Short` is exploratory and `Dry` verifies
execution only. [BenchmarkDotNet's job documentation](https://benchmarkdotnet.org/articles/configs/jobs.html)
describes these tradeoffs. Do not convert a Dry result into a performance baseline or a release threshold.

MemoryDiagnoser reports managed allocations and GC activity, **not peak resident/process memory or all native
allocations**. [Diagnoser documentation](https://benchmarkdotnet.org/articles/configs/diagnosers.html) explains
the collection model. Engine timings and allocation columns are normalized per evaluation through
`OperationsPerInvoke=256`; archive/checkpoint results are per operation. Peak-memory profiling remains separate work.

For a regression comparison, pin both source revisions, SDK/runtime, OS/architecture, hardware, power policy,
worker count and selected parameters. Run on an otherwise idle host with the same environment and retain raw JSON,
failure output and all scheduled cases. The asynchronous delay is simulated latency subject to OS timer resolution;
it is not a CPU/GPU workload or a calibrated one-millisecond busy loop. The evaluator-only ratio includes the
engine's necessary extra work and is not evidence that search quality improved.

Current acceptance: builds and local Dry/smoke validation. Hosted CI, controlled repeated timing baselines,
peak memory, dimensionality/island scaling and calibrated regression thresholds are still required by US-10.
