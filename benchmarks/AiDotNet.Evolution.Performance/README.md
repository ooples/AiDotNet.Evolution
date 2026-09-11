# Engine performance suite

BenchmarkDotNet 0.15.8 is confined to this executable; the core package has no benchmark dependency.
These measure orchestration and data-structure cost, not the quality or runtime speed of evolved algorithms.

| Fixture | Factors | What is timed |
| --- | --- | --- |
| Engine | 1/4 workers; batch/continuous; 0/1 ms asynchronous evaluator delay; checkpoints off/on; 2/8 genome dimensions; 100/1,000 cell capacity; 1/4 islands | Complete 256-evaluation run, including eight seeds, proposal/canonicalization/archive work, final state and optional in-memory checkpoints |
| Evaluator-only control | Same worker count, evaluator and 256-call count | Evaluation of precomputed genomes with parallel scheduling; a lower bound, not an evolutionary algorithm or quality baseline |
| Archive | 100/1,000/10,000 occupied cells; 1/8/32 descriptor dimensions | Cell lookup, uniform sampling and immutable snapshot creation; construction excluded; extra axes have one bin to hold actual occupied cells fixed |
| Checkpoint | 32/256/2,048 prior evaluations; 1/4 islands; 1/8 genome dimensions | Checksum envelope creation, in-memory clone load, or fresh-store population plus engine restore without new evaluations |

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
`OperationsPerInvoke=256`; archive/checkpoint results are per operation. The separate isolated profiler below records peak memory.

For a regression comparison, pin both source revisions, SDK/runtime, OS/architecture, hardware, power policy,
worker count and selected parameters. Run on an otherwise idle host with the same environment and retain raw JSON,
failure output and all scheduled cases. The asynchronous delay is simulated latency subject to OS timer resolution;
it is not a CPU/GPU workload or a calibrated one-millisecond busy loop. The evaluator-only ratio includes the
engine's necessary extra work and is not evidence that search quality improved.

## Isolated profiling protocol

```powershell
dotnet test tests/AiDotNet.Evolution.Performance.Tests -c Release
dotnet build benchmarks/AiDotNet.Evolution.Performance -c Release
$profileRevision = git rev-parse HEAD
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release --no-build -- --profile TestResults/profile-smoke $profileRevision --smoke
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release --no-build -- --profile TestResults/profile-full $profileRevision
```

Build from clean committed source and supply that exact revision. Output directories must be new or empty;
the profiler uses create-new writes and never overwrites an earlier experiment. `--smoke` runs 11 cases once;
the full predeclared suite runs 44 cases three times (132 fresh child processes), rotating case order between
repetitions. Every attempt has an input, raw measurement, process log and outcome. A crash, timeout, omitted
attempt, changed runtime or deterministic-state mismatch fails the campaign; successful subsets cannot pass it.
`plan.json` is durable before the first worker, and `report.json` contains every outcome plus descriptive medians.

The full suite includes 1/2/4 workers, both dispatchers, checkpoints on/off, cheap and mixed-duration evaluation,
32/256/4,096 archive capacities, 2/8/32 genome dimensions and 1/2/4 islands. These are one-factor scaling contrasts,
not every Cartesian combination. Separate archive fixtures really fill 100/1,000/10,000 cells and vary descriptor
dimensions at fixed occupancy; checkpoint restore uses 32/256/2,048 prior evaluations. Engine observed occupancy
is reported separately from configured cell capacity. Numeric work is an authored negative-sphere task, not
generated C# or a model-driven competitor benchmark.

Each owned child is restricted to the same first four available affinity bits (or fewer when unavailable), with
`DOTNET_PROCESSOR_COUNT` matching that mask and tiered compilation disabled before runtime startup. Windows
CPU-group use/assignment is also disabled in the child: otherwise newly created pool threads can expand a mask
that was initially set successfully. Runtime/OS/architecture/CPU identity, observed final affinity, processor count,
GC mode and these controls are recorded and validated. This supports Windows and Linux; unsupported platforms
fail explicitly. No global environment, other process, power policy or machine-wide setting is changed.
[Microsoft documents the CPU-group controls](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading).
On Windows, an unnamed job constrains the owned worker and future threads to the controller-selected CPU group
and mask; group identity is validated across cases. It has no security/time/memory/kill-on-close limits and is not
an untrusted-code sandbox. On Linux, `/usr/bin/taskset` (util-linux) sets affinity before CLR startup so runtime
threads inherit it; setting only the main thread after startup is insufficient. The current mask representation
is bounded to the first 64 group-relative logical CPUs; an empty/unrepresentable permitted mask fails explicitly.

Measurement starts after fixture setup, one warmup invocation and a full GC. Process-wide managed allocation,
wall time, process CPU time, before/after working set and the fresh process's **lifetime peak working set** are
separate fields. The peak includes startup/setup/warmup and must not be described as measured-phase memory;
`peakBeforeMeasurementBytes` exposes that baseline. The
[Process.PeakWorkingSet64 contract](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.peakworkingset64)
is a high-water mark, not an allocation counter. Observer instrumentation is included in engine timing/allocation.

For mixed durations, deterministic genome-dependent 0/1/8 ms asynchronous delays simulate unequal latency.
`evaluatorSlotUtilization` is summed occupied evaluator time divided by workers times elapsed time, not CPU
utilization. Every completed evaluation records best-so-far quality and elapsed time. Compare both fields for
batch versus continuous; high utilization alone does not establish better search. Continuous comparisons keep
`MaxInFlight=8`. Worker-count comparisons require identical state hashes within fixed dispatch semantics and
also verify checkpoint-on/off invariance. Different dispatch policies are not asserted to have identical trajectories.

Checkpoint store timing measures cloning/storage only, excluding serialization. Paired whole-run checkpoint-on/off
elapsed and allocated-byte contrasts include serialization, hashing and the store. Payload bytes and save counts
are retained. The evaluator-only control has the same call budget but precomputed candidates, so it is a lower-bound
orchestration contrast, not a search-quality baseline.

Host background load, frequency and power policy are not controlled. Three repetitions of one fixed seed on this
host provide diagnostic scaling evidence, not calibrated release regression thresholds or statistically powered
optimizer superiority. CI runs the contract tests and isolated smoke, retaining failures as artifacts. Representative
workload coverage and statistical release decisions remain separate dependencies (US-01, US-04, US-12).
