# US-10 current-stack before/after evidence

Baseline runtime: `2a3ccb5` (integrated, corrected profiler; original snapshot implementation).
Optimized runtime: `f840f8ce841b3ae38df4066a36efb7a699f9bb3f` (owned-array sort and best-entry scan).
Each campaign ran 47 declared cases × three fresh-process repetitions: **282 attempts total, all passed**,
with no failed or contended retries. Nine worker-determinism groups passed in each campaign.
The cross-revision comparison verifies identical cases, runtime/host controls, search/checkpoint state hashes
and engine quality. There is no search-quality change; this is an orchestration optimization.

## Measured improvement

All numbers are medians of individually normalized attempts, not ratios of aggregate totals.
Snapshot time below is microseconds per immutable snapshot, not whole-run time.

| Occupied cells | Before bytes/snapshot | After bytes/snapshot | Allocation reduction | Before µs | After µs | Elapsed reduction |
|---|---:|---:|---:|---:|---:|---:|
| 100 | 7,544.38 | 4,296.22 | 43.05% | 11.46 | 7.02 | 38.76% |
| 1,000 | 67,835.41 | 39,386.03 | 41.94% | 128.76 | 81.75 | 36.51% |
| 10,000 | 643,969.44 | 363,492.27 | 43.55% | 1,981.59 | 1,271.97 | 35.81% |

Rounded display; exact measured values are in [comparison.json](comparison.json).
The 1,000-cell eight/32-descriptor cases also reduced allocations by41.90%/41.79%, and elapsed time by33.93%/33.68%.
The implementation avoids extra sorting arrays while preserving bounded owned copies, detached snapshots,
ordinal cell ordering, deterministic best-entry tie breaks and duplicate-cell rejection.

## Whole-engine results and limits

The cheap single-worker batch case changed by only0.40% elapsed and0.01% allocation. Do not advertise the snapshot
result as a whole-engine speedup. All47 case contrasts remain in the comparison, including worse elapsed cases:
single-worker continuous cheap/checkpoint-on was2.41% slower; two-worker batch cheap/checkpoint-on1.51% slower.
Other whole-engine changes are small/mixed and these sequential measurements do not establish their cause.

After optimization, mixed-duration checkpoint-off median whole-run times were:

| Workers | Batch ms | Continuous ms | Batch slot utilization | Continuous slot utilization |
|---|---:|---:|---:|---:|
| 1 |885.74|858.48|95%|98%|
| 2 |527.36|436.59|79%|96%|
| 4 |342.39|263.98|60%|78%|

Full quality-versus-time curves, best quality, peak memory, allocation, CPU time, observed delay buckets, occupied
cells, checkpoint counts/payload/store timing and all individual repetitions are retained in the raw reports.
Evaluator-only controls isolate lower-bound scheduling costs, not optimizer quality. CPU/memory costs of the typed
genome/task implementation are included. The delay schedule is simulated asynchronous0/1/8ms latency.

Controls: .NET10.0.12, Windows26200, x64, AMD64 Family23 Model49, four logical CPUs on distinct physical cores,
CPU group1/mask154, workstation GC, tiering off, CPU-group redistribution off, observed timer resolution0.5ms.
Both campaigns recorded the same custom power plan. Per-attempt foreign CPU was gated at25%; dispersion at2.5×.
Host frequency, thermal conditions and power policy were not locked. Runs were sequential before/after, not randomized
across revisions; three repetitions on one host provide descriptive observations, not statistical superiority.

Lifetime peak working set includes setup/warmup/startup. Managed allocation is measured-phase process-wide allocation.
Checkpoint store timing excludes serialization; inclusive checkpoint comparisons include queue-drain effects.
Island scaling covers1/2/4 islands at one worker, batch, no migration—not every interaction.
No OpenEvolve comparison, representative optimizer-quality gain or release threshold is established. No paid/model calls.

## Reproducibility and integrity

- [Before raw report](before-report.json.gz) and [summary](before-summary.json).
- [After raw report](after-report.json.gz) and [summary](after-summary.json).
- [Every case's before/after contrast](comparison.json), with uncompressed SHA256 hashes.

Gzip reports are lossless. Tests verify raw hashes against summaries and comparison, then recompute all contrasts.
The historical PR53 evidence is preserved separately. Its derived table columns were corrected, not its raw measurements.
No release asset or package was published for this study.

Build each runtime revision separately, then run its already-built profiler with a fresh output directory:

```powershell
dotnet benchmarks/AiDotNet.Evolution.Performance/bin/Release/net10.0/AiDotNet.Evolution.Performance.dll --profile TestResults/new-profile FULL_COMMIT_SHA
./eng/Export-ProfileComparison.ps1 -BeforeReport BEFORE/report.json -AfterReport AFTER/report.json -OutputDirectory TestResults/new-comparison
```

Outputs refuse overwrites. Only contention is retryable under the predeclared bounded protocol; failed evidence is retained.
