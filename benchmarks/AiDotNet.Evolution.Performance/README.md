# Engine performance suite

BenchmarkDotNet 0.15.8 is confined to this executable; the core package has no benchmark dependency.
These measure orchestration and data-structure cost, not the quality or runtime speed of evolved algorithms.

| Fixture | Factors | What is timed |
| --- | --- | --- |
| Engine | 1/4 workers; batch/continuous; 0/1 ms asynchronous evaluator delay; checkpoints off/on; 2/8 genome dimensions; 100/1,000 cell capacity; 1/4 islands | Complete 256-evaluation run, including eight seeds, proposal/canonicalization/archive work, final state and optional in-memory checkpoints |
| Evaluator-only control | 1/4 workers; 0/1 ms delay; 2/8 genome dimensions | Evaluation of precomputed genomes with parallel scheduling; a lower bound, not an evolutionary algorithm or quality baseline |
| Archive | 100/1,000/10,000 occupied cells; 1/8/32 descriptor dimensions | Cell lookup, uniform sampling and immutable snapshot creation; construction excluded; extra axes have one bin to hold actual occupied cells fixed |
| Checkpoint | 32/256/2,048 prior evaluations; 1/4 islands; 1/8 genome dimensions | Checksum envelope creation, in-memory clone load, or fresh-store population plus engine restore without new evaluations |

The engine and the evaluator-only control are **separate benchmark classes** because they apply different factors.
Dispatch, checkpoint, archive-capacity and island parameters change nothing in an evaluator-only loop, so declaring
them there would have produced 120 repeated cases with different labels rather than 120 measurements. The cost of
that split is that BenchmarkDotNet no longer prints a built-in baseline ratio; compare the two classes, or use the
isolated profiler below, which measures both under one protocol.

Every fixture verifies its claimed work. Engine counters must reach the evaluation cap, archives must fill the
declared cells, and restored checkpoints must preserve the exact state hash. Fixtures do not accumulate archives
or mutate the next invocation's starting checkpoint. Numeric genomes include their real typed-space costs.

## Run

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --smoke
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*ArchiveBenchmarks*' --job Short --exporters json
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*EngineBenchmarks*' --exporters json
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*EvaluatorBaselineBenchmarks*' --exporters json
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --filter '*CheckpointBenchmarks*' --exporters json
```

`--smoke` executes every fixture once per representative configuration, including one variant of each engine factor
(genome dimensions, archive capacity, islands) and the evaluator-only factors, and prints the configuration counts it
actually ran. It makes no timing claim.

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

Build from clean committed source and supply that exact revision. The revision is **not taken on trust**: it is
compared with the commit SourceLink compiled into the measured `AiDotNet.Evolution` assembly, and a campaign whose
supplied revision is absent, malformed or different fails before any directory is created. The tracked working-tree
state (`clean`, `modified-tracked-files=N` or `unavailable`) is recorded in the plan and report as disclosure, not as a gate.

Output directories must be new or empty; the profiler uses create-new writes and never overwrites an earlier
experiment. `--smoke` runs 13 cases once; the full predeclared suite runs 47 cases three times (141 fresh child
processes) in a seeded per-repetition shuffle, so no dispatcher always gets the coldest host. Every attempt has an
input, raw measurement, process log and outcome. A crash, timeout, omitted attempt, changed runtime, excessive
host contention, excessive repetition spread or deterministic-state mismatch fails the campaign; successful subsets
cannot pass it. `plan.json` is durable before the first worker, and `report.json` contains every outcome plus
descriptive medians with their minimum and maximum repetitions.

The full suite includes 1/2/4 workers, both dispatchers, checkpoints on/off, cheap and mixed-duration evaluation,
32/256/4,096 archive capacities, 2/8/32 genome dimensions, 1/2/4 islands, and a parent-dependent mutation operator
at 1/2/4 workers alongside the parent-independent restart proposer. These are one-factor scaling contrasts, not
every Cartesian combination. A case may only set the factors its kind applies (`appliedFactors`); every other factor
is pinned to a canonical value and validated, so a recorded field can never imply an untested contrast. Separate
archive fixtures really fill 100/1,000/10,000 cells and vary descriptor dimensions at fixed occupancy; checkpoint
restore uses 32/256/2,048 prior evaluations. Engine observed occupancy is reported separately from configured cell
capacity. Numeric work is an authored negative-sphere task, not generated C# or a model-driven competitor benchmark.

### CPU pinning, host contention and dispersion

"Isolated" means a fresh child process with a fixed CPU mask; it does not mean exclusive hardware. The controller
selects **one logical processor per physical core**, skipping the core that owns CPU 0, using
`GetLogicalProcessorInformationEx` on Windows and `thread_siblings_list` on Linux, so pinned CPUs never share an
SMT core with each other or with CPU 0's interrupt-heavy core. The selected mask, core layout
(`smtTopology`) and power plan/governor (`powerPolicy`) are recorded per attempt.

Because other processes may still run on those CPUs, the controller samples system-wide per-processor busy counters
(`NtQuerySystemInformation`/`SystemProcessorPerformanceInformation` on Windows, `/proc/stat` on Linux) immediately
before and after each attempt, subtracts the owned child's own CPU time, and records the remaining foreign share of
the pinned capacity. Above 5% an attempt is flagged; above 25% it fails. This is an aggregate counter read, not a
per-process enumeration, and it cannot attribute load to a particular owner, and the subtracted worker CPU time
carries the platform's coarse clock resolution, so a short attempt's foreign share is an estimate.

Host contention is the **only** retryable attempt failure. A contended attempt is retained in the report with its
measured load and status `contended`, the controller then waits (bounded, up to 60 s) for the pinned CPUs to fall
back below the flag threshold, and retries at most twice. Every other failure fails the campaign immediately.
A campaign is complete only when each declared case and repetition has exactly one passed attempt.

Each case reports the median, minimum and maximum of its repetitions and the slowest-to-fastest ratio; a ratio above
2.50x fails the campaign rather than publishing a median that hides it. That threshold is declared for a shared
developer workstation, where fresh-process repetitions of a short cheap case were measured spreading up to about
2.0x; a dedicated quiet host should tighten it.

`DOTNET_PROCESSOR_COUNT` matches the mask and tiered compilation is disabled before runtime startup. Disabling
tiered compilation also disables Dynamic PGO, which is built on tiering: these measurements describe fully
optimizing-compiled code without profile-guided re-optimization, which is not the default production configuration.
Windows CPU-group use/assignment is also disabled in the child: otherwise newly created pool threads can expand a mask
that was initially set successfully. Runtime/OS/architecture/CPU identity (from `PROCESSOR_IDENTIFIER`, or
`/proc/cpuinfo` where that variable does not exist), observed final affinity, processor count, GC mode and these
controls are recorded and validated by one pure function, `ProfileValidation.ValidateRuntimeControls`, which the
contract tests exercise directly. This supports Windows and Linux; unsupported platforms fail explicitly. No global
environment, other process, power policy or machine-wide setting is changed.
[Microsoft documents the CPU-group controls](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading).
On Windows, an unnamed job constrains the owned worker and future threads to the controller-selected CPU group
and mask; group identity is validated across cases. It has no security/time/memory/kill-on-close limits and is not
an untrusted-code sandbox. On Linux, `/usr/bin/taskset` (util-linux) sets affinity before CLR startup so runtime
threads inherit it; setting only the main thread after startup is insufficient. The current mask representation
is bounded to the first 64 group-relative logical CPUs; an empty/unrepresentable permitted mask fails explicitly.

### Timing, repetition and memory

Measurement starts after fixture setup, one warmup invocation and a full GC. The warmup's duration **and the
operations it actually executed** are recorded, and a measurement that reports no warmup work is rejected.

Sub-millisecond single-shot timing is not published: every case repeats its whole measured body until at least
**250 ms** of wall time has been measured (`ProfileProtocol.MinimumMeasuredMilliseconds`), and reports
`iterations`, `millisecondsPerIteration` and per-operation throughput/allocation. Engine cases keep the first
iteration's complete quality curve and assert that every repeated iteration produced the identical state hash.

Process-wide managed allocation, wall time, process CPU time, before/after working set and the fresh process's
**lifetime peak working set** are separate fields. The peak includes startup/setup/warmup and must not be described
as measured-phase memory; `peakBeforeMeasurementBytes` exposes that baseline. The
[Process.PeakWorkingSet64 contract](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.peakworkingset64)
is a high-water mark, not an allocation counter. Observer instrumentation is included in engine timing/allocation.

Process CPU time comes from the platform's coarse clock (15.625 ms on this Windows host, USER_HZ ticks on Linux);
that resolution is recorded in `processorTimeResolutionMilliseconds`, and a measurement whose CPU time exceeds
processors x elapsed + one resolution unit fails. Windows attempts additionally record unhalted process cycles from
`QueryProcessCycleTime` and Linux attempts record nanosecond CPU time from `/proc/self/schedstat`, so the coarse
field is never the only evidence.

### Simulated evaluation latency

For mixed durations, deterministic genome-dependent 0/1/8 ms asynchronous delays simulate unequal latency.
Windows rounds `Task.Delay` to the current timer tick, which is 15.6 ms by default: the 0/1/8 ms schedule would
silently become 0/15.6/15.6 ms. The owned worker therefore raises the platform timer resolution
(`timeBeginPeriod(1)`), records the resolution actually in force (`timerResolutionMilliseconds`, validated to be
1 ms or finer), **measures every simulated delay it awaits**, and reports per-bucket count, mean, minimum and
maximum. A bucket whose mean falls outside its request −0.25 ms to +2.0 ms fails the attempt, so a collapsed timer
cannot be published as the declared schedule.

`evaluatorSlotUtilization` is summed occupied evaluator time divided by workers times elapsed time, not CPU
utilization. Every completed evaluation records best-so-far quality and elapsed time. Compare both fields for
batch versus continuous; high utilization alone does not establish better search. Continuous comparisons keep
`MaxInFlight=8`. Worker-count comparisons require identical state hashes within fixed dispatch, checkpoint and
operator settings. Continuous checkpoints drain the in-flight window before saving; this changes subsequent proposal
context and lineage even with a deterministic evaluator. Different checkpoint or dispatch policies are therefore not asserted
to have identical trajectories. Their quality curves are reported, not silently treated as equal-workload timing samples.

Checkpoint store timing measures cloning/storage only, excluding serialization. Paired whole-run checkpoint-on/off
elapsed and allocated-byte contrasts include serialization, hashing, the store and continuous queue-drain effects. Payload bytes and save counts
are retained. The evaluator-only control has the same call budget but precomputed candidates, so it is a lower-bound
orchestration contrast, not a search-quality baseline.

### Published evidence

A complete raw report is large (roughly 9 MB for 141 attempts with full quality curves), so the repository commits a
compact summary instead and publishes the raw report as a release asset:

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release --no-build -- --evidence TestResults/profile-full/report.json benchmarks/evidence/performance/<revision>-summary.json <raw-download-url>
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release --no-build -- --evidence-tables benchmarks/evidence/performance/<revision>-summary.json
```

The summary keeps every per-attempt scalar and state hash, plus columnar quality curves for the mixed-duration
dispatch cases the published tables slice. `--evidence-tables` regenerates both published tables from that summary
alone, and `--evidence-failure` reduces a failed campaign to its diverging state-hash groups. The raw report's
SHA-256 and download URL are recorded in the summary and in the evidence README.

Host background load is measured and gated, but frequency and power policy are recorded, not controlled. Three
repetitions of one fixed seed on one host provide diagnostic scaling evidence, not calibrated release regression
thresholds or statistically powered optimizer superiority. CI runs the contract tests and isolated smoke, retaining
failures as artifacts. Representative workload coverage and statistical release decisions remain separate
dependencies (US-01, US-04, US-12).
