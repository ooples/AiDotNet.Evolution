# US-10 engine profiling evidence

Source: `b6303d6208bf8f7a06d0cb9cee483e685ee5e2f2`, verified against the commit SourceLink compiled into the measured
assembly (`0.1.0-preview.1+b6303d6208bf8f7a06d0cb9cee483e685ee5e2f2`), tracked working tree clean.
Recorded September 12, 2026, 00:20:54–00:24:49 UTC.

**47 declared cases x 3 repetitions = 141 planned attempts, all passed; nine worker-determinism groups passed.**
A 142nd attempt is retained: `checkpoint-evaluations-256` repetition 1 measured 26.40% foreign CPU on its pinned
processors, above the declared 25% limit, so it was recorded with status `contended` and retried after the host went
quiet. Contention is the only retryable failure; every other failure fails the campaign.

This is engine orchestration/scaling evidence, not evolved-algorithm speedup or competitor superiority.

## What is committed, and where the raw report lives

The complete raw report is 5.2 MB, so the repository commits a compact, regenerable summary instead:

| Committed file | Bytes | Contents |
| --- | ---: | --- |
| [`b6303d6-summary.json`](b6303d6-summary.json) | 442,352 | Every per-attempt scalar and state hash for all 142 attempts, per-bucket measured delays, per-attempt foreign-CPU share, the environment block, all case summaries, and columnar quality curves for the 18 mixed-duration dispatch attempts the tables below slice |
| [`0bed72d-failed-summary.json`](0bed72d-failed-summary.json) | 3,332 | The superseded failed campaign reduced to its two diverging state-hash groups |

The raw reports are published as assets of the [`evidence-us-10-b6303d6`](https://github.com/ooples/AiDotNet.Evolution/releases/tag/evidence-us-10-b6303d6)
prerelease. That tag is deliberately not a version tag, and no workflow in this repository triggers on tags or on
release events, so publishing it cannot start a NuGet release.

| Release asset | Bytes | SHA-256 |
| --- | ---: | --- |
| [`b6303d6-report.json`](https://github.com/ooples/AiDotNet.Evolution/releases/download/evidence-us-10-b6303d6/b6303d6-report.json) | 5,229,569 | `31eb2e267a335620fb748c56e5219774ff195702bdd006eaefa7ca3859180ae7` |
| [`0bed72d-failed-report.json`](https://github.com/ooples/AiDotNet.Evolution/releases/download/evidence-us-10-b6303d6/0bed72d-failed-report.json) | 4,404,769 | `a0753a2056794e40061df92beec21aa1ad543326f5a830aac9c66d7792f21bd0` |
| [`4a7b50e-report.json`](https://github.com/ooples/AiDotNet.Evolution/releases/download/evidence-us-10-b6303d6/4a7b50e-report.json) | 4,405,842 | `df97c7587083ae8345e010ba98ddac90e56106c0907c1029e0727a8e1e4c3a9a` |

`4a7b50e-report.json` is the earlier campaign this one supersedes. Its 0/1/8 ms delay schedule really ran
0/15.6/15.6 ms on this host, so its mixed-duration timings measured the Windows timer tick rather than the declared
schedule; it is published for provenance, not as a comparable baseline.

Both raw files were removed from Git in a new commit rather than by rewriting history. They remain in this branch's
history; squash-merging this pull request keeps them out of the default branch's history.

## Controls and limits

.NET 10.0.12, Windows build 26200, x64, AMD64 family 23/model 49/stepping 0 (Ryzen Threadripper 3990X), CPU group 0,
workstation GC, tiered compilation disabled, automatic CPU-group assignment/use disabled.

Affinity mask `154`: **one logical processor per physical core, away from CPU 0** —
`cores=32;threadsPerCore=2;selected=core1[2+3]:cpu2,core2[4+5]:cpu4,core3[6+7]:cpu6,core4[8+9]:cpu8`. Each measured
attempt ran in a fresh child with a group-and-mask-constrained unnamed job.

- **Timer resolution 0.5 ms in force** (raised per owned child). Across every mixed-duration attempt the requested
  1 ms delays actually took **0.939–1.249 ms** and the 8 ms delays **7.925–8.228 ms**, measured per await and
  validated per bucket against a declared −0.25/+2.0 ms tolerance. The previous campaign's 15.6 ms rounding would
  now fail the attempt.
- **Process CPU-time resolution 15.625 ms**, recorded; the bound checked is processors x elapsed plus two resolution
  units, because the counter is sampled immediately around the measured window and each endpoint is quantized.
  Windows attempts also record unhalted process cycles.
- **Every case repeats to at least 250 ms of measured time**; throughput and allocation are per operation, and the
  tables report iterations and per-iteration time. No published number rests on a single sub-millisecond shot.
- **Host contention was measured per attempt**, not assumed: the maximum foreign share of the pinned CPUs was
  26.40% (the retained contended attempt) and 118 of 142 attempts were above the 5% flag threshold. This
  workstation is shared, and that is disclosed rather than described as an idle host. The estimate is clamped at
  zero, so 0.00% means no foreign load was detected, not that there provably was none.
- **Repetition spread is gated**: the worst slowest-to-fastest ratio was **2.270x**, against a declared 2.50x limit,
  and every case reports its minimum and maximum alongside its median.
- Peak working set is the **process-lifetime high-water mark**, including startup/setup/warmup, not measured-phase
  allocation. Before/after and pre-measurement peak baselines are retained in the summary.
- Host CPU frequency, power plan (`scheme=1cc35fdb-3870-4d7d-bd31-f0507f65dda6`, a custom plan) and thermal state
  are recorded, not controlled. One authored sphere objective and one fixed seed are diagnostic, not calibrated
  regression thresholds.

The [protocol](../../AiDotNet.Evolution.Performance/README.md) explains all factors, instrumentation and limitations.

## Main observations

- Cheap eight-dimensional batch search with one worker completed about **7,049 evaluations/s** (36.315 ms per
  256-evaluation iteration, seven iterations), against **526,767 precomputed evaluator-only calls/s**. The engine
  includes proposals, owned genomes, validation, archive updates, observers and final state; this is a lower-bound
  overhead comparison, not a quality comparison.
- Raising workers from one to four did not improve cheap batch throughput on four pinned processors
  (7,049 versus 5,676/s median, whose 36.621–45.105 ms spread overlaps the one-worker case). Mixed asynchronous
  latency did benefit from more workers and from continuous dispatch, in both utilization and time to a quality
  threshold. These are simulated waits, not CPU/GPU/model workloads.
- Checkpoint-on cheap batch rose from 36.315 to 179.074 ms per iteration and from 143.140 to 627.893 managed KiB per
  evaluation. Under interval 32, batch saved nine checkpoints per iteration and continuous seven: continuous drains
  its in-flight window before saving, so its on/off contrast also changes scheduling and lineage. Do not read its
  lower checkpoint count as a faster serializer.
- Engine cell counts are **capacity, not occupancy**: the 256-cell engine cases ended with between **57 and 233**
  cells occupied. Only the archive fixtures really fill 100/1,000/10,000 cells.
- A parent-dependent mutation operator (`engine-mutation-w1/w2/w4`) produced identical state hashes across worker
  counts, so worker-count determinism is no longer established only for the parent-independent restart proposer.

## Published tables, regenerated from the committed summary

Mixed-duration cases, checkpoints off, 256 evaluations, eight dimensions, MaxInFlight=8. Higher quality is better;
all six configurations ended at −13.398181. With the timer fixed, every configuration also reaches that final
quality within 500 ms, so the 500 ms column now saturates and no longer discriminates between dispatchers — the
time-to-threshold column does. Both columns are descriptive slices of the retained complete curves, not predeclared
significance tests.

In the scaling table, throughput and managed KiB are per evaluation for engine/evaluator cases, per snapshot for
archive cases and per restore for checkpoint cases. Elapsed time is per iteration, with the minimum and maximum
across three fresh-process repetitions; peak memory is the maximum of the three process lifetime peaks.

Regenerate both tables from the committed summary alone with:

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release -- --evidence-tables benchmarks/evidence/performance/b6303d6-summary.json
./eng/Test-EvidenceTables.ps1
```

<!-- BEGIN GENERATED TABLES -->
| Dispatch / workers | Median run ms | Median evaluator-slot utilization | Median ms to quality >= -25 | Median quality at 500 ms |
| --- | ---: | ---: | ---: | ---: |
| Batch / 1 | 897.989 | 92.71% | 77.973 | -13.398181 |
| Continuous / 1 | 855.529 | 97.92% | 65.300 | -13.398181 |
| Batch / 2 | 528.254 | 78.68% | 45.349 | -13.398181 |
| Continuous / 2 | 439.965 | 95.52% | 33.288 | -13.398181 |
| Batch / 4 | 358.355 | 57.56% | 29.839 | -13.398181 |
| Continuous / 4 | 264.987 | 77.75% | 19.588 | -13.398181 |

| Case | Operations/s | Iterations | Ms/iteration | Min-max ms/iteration | Managed KiB/op | Max lifetime peak MiB | Max foreign CPU |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| engine-w1-Batch-cheap-cp0 | 7049.41 | 7 | 36.315 | 36.162-46.392 | 143.140 | 60.22 | 14.32% |
| engine-w1-Batch-cheap-cp1 | 1429.58 | 2 | 179.074 | 176.318-186.458 | 627.893 | 65.98 | 17.09% |
| engine-w1-Batch-mixed-cp0 | 285.08 | 1 | 897.989 | 877.598-899.204 | 144.084 | 57.51 | 9.58% |
| engine-w1-Batch-mixed-cp1 | 246.82 | 1 | 1037.198 | 1016.110-1072.983 | 633.618 | 64.85 | 13.35% |
| engine-w1-Continuous-cheap-cp0 | 6712.32 | 7 | 38.139 | 37.988-38.731 | 145.230 | 60.68 | 10.36% |
| engine-w1-Continuous-cheap-cp1 | 3185.69 | 4 | 80.359 | 63.873-80.359 | 273.383 | 65.46 | 11.32% |
| engine-w1-Continuous-mixed-cp0 | 299.23 | 1 | 855.529 | 854.672-863.351 | 148.203 | 57.99 | 10.71% |
| engine-w1-Continuous-mixed-cp1 | 284.22 | 1 | 900.696 | 900.528-953.320 | 279.341 | 64.80 | 10.57% |
| engine-w2-Batch-cheap-cp0 | 6997.22 | 7 | 36.586 | 36.375-40.310 | 143.140 | 60.39 | 6.67% |
| engine-w2-Batch-cheap-cp1 | 1416.18 | 2 | 180.769 | 131.753-180.769 | 627.878 | 65.42 | 12.15% |
| engine-w2-Batch-mixed-cp0 | 484.62 | 1 | 528.254 | 525.056-550.658 | 144.009 | 57.76 | 8.91% |
| engine-w2-Batch-mixed-cp1 | 319.86 | 1 | 800.345 | 675.816-815.665 | 635.512 | 67.87 | 12.48% |
| engine-w2-Continuous-cheap-cp0 | 6650.43 | 7 | 38.494 | 38.011-39.767 | 145.230 | 60.57 | 9.04% |
| engine-w2-Continuous-cheap-cp1 | 3265.83 | 4 | 78.387 | 63.717-78.387 | 273.384 | 65.40 | 20.33% |
| engine-w2-Continuous-mixed-cp0 | 581.86 | 1 | 439.965 | 437.039-448.888 | 148.157 | 57.92 | 8.82% |
| engine-w2-Continuous-mixed-cp1 | 519.99 | 1 | 492.318 | 485.558-516.301 | 280.309 | 64.67 | 10.75% |
| engine-w4-Batch-cheap-cp0 | 5675.61 | 7 | 45.105 | 36.621-45.105 | 143.140 | 60.46 | 7.93% |
| engine-w4-Batch-cheap-cp1 | 1007.95 | 2 | 253.982 | 132.762-253.982 | 627.899 | 65.33 | 24.80% |
| engine-w4-Batch-mixed-cp0 | 714.38 | 1 | 358.355 | 348.157-359.665 | 143.860 | 57.82 | 8.25% |
| engine-w4-Batch-mixed-cp1 | 520.80 | 1 | 491.550 | 481.376-559.514 | 635.510 | 66.75 | 8.77% |
| engine-w4-Continuous-cheap-cp0 | 6619.41 | 7 | 38.674 | 38.570-39.832 | 145.230 | 60.68 | 9.41% |
| engine-w4-Continuous-cheap-cp1 | 3243.03 | 4 | 78.938 | 66.737-78.938 | 273.382 | 65.54 | 10.75% |
| engine-w4-Continuous-mixed-cp0 | 966.08 | 1 | 264.987 | 262.285-272.399 | 147.721 | 57.96 | 7.45% |
| engine-w4-Continuous-mixed-cp1 | 697.64 | 1 | 366.949 | 327.109-372.348 | 281.282 | 65.31 | 8.21% |
| engine-mutation-w1 | 7002.49 | 7 | 36.558 | 36.324-38.563 | 145.082 | 58.20 | 23.04% |
| engine-mutation-w2 | 6583.29 | 7 | 38.886 | 36.090-38.886 | 145.069 | 58.07 | 5.96% |
| engine-mutation-w4 | 7010.89 | 7 | 36.515 | 36.346-43.939 | 145.069 | 58.10 | 9.10% |
| evaluation-w1-cheap | 526767.28 | 515 | 0.486 | 0.485-0.486 | 1.221 | 54.13 | 9.32% |
| evaluation-w1-mixed | 347.92 | 1 | 735.794 | 732.939-737.872 | 1.588 | 49.46 | 10.00% |
| evaluation-w2-cheap | 1041287.63 | 1017 | 0.246 | 0.246-0.246 | 1.224 | 54.25 | 7.22% |
| evaluation-w2-mixed | 692.29 | 1 | 369.786 | 368.739-371.169 | 1.589 | 49.55 | 12.82% |
| evaluation-w4-cheap | 934634.60 | 913 | 0.274 | 0.274-0.274 | 1.265 | 54.27 | 6.96% |
| evaluation-w4-mixed | 1372.66 | 2 | 186.499 | 186.067-186.566 | 1.569 | 49.67 | 10.49% |
| engine-cells-32 | 7443.32 | 8 | 34.393 | 31.703-34.393 | 139.690 | 57.68 | 5.96% |
| engine-cells-4096 | 6691.96 | 7 | 38.255 | 38.109-38.378 | 145.723 | 61.45 | 8.24% |
| engine-dimensions-2 | 16703.38 | 17 | 15.326 | 15.149-15.326 | 59.430 | 58.71 | 9.09% |
| engine-dimensions-32 | 2071.48 | 3 | 123.583 | 121.895-163.418 | 469.476 | 58.70 | 7.53% |
| engine-islands-2 | 6753.03 | 7 | 37.909 | 37.507-39.585 | 144.134 | 60.77 | 8.16% |
| engine-islands-4 | 3165.38 | 4 | 80.875 | 63.354-80.875 | 144.698 | 59.11 | 11.00% |
| archive-cells-100 | 88018.05 | 1376 | 0.182 | 0.182-0.182 | 7.368 | 53.93 | 23.36% |
| archive-cells-1000 | 7769.11 | 122 | 2.059 | 2.058-2.065 | 66.246 | 56.43 | 4.77% |
| archive-cells-10000 | 552.17 | 9 | 28.976 | 28.850-29.698 | 628.920 | 85.39 | 8.72% |
| archive-dimensions-8 | 7487.93 | 117 | 2.137 | 2.137-2.154 | 66.300 | 58.27 | 9.17% |
| archive-dimensions-32 | 4966.91 | 78 | 3.221 | 3.211-3.255 | 66.488 | 64.82 | 9.30% |
| checkpoint-evaluations-32 | 196.86 | 25 | 10.160 | 10.066-10.858 | 3988.375 | 62.07 | 8.42% |
| checkpoint-evaluations-256 | 24.87 | 4 | 80.410 | 63.269-80.410 | 28275.323 | 69.76 | 8.62% |
| checkpoint-evaluations-2048 | 5.39 | 1 | 371.257 | 346.253-391.123 | 114899.336 | 108.00 | 12.68% |
<!-- END GENERATED TABLES -->

Slot utilization includes asynchronous waits; it is not CPU utilization. Raw process CPU milliseconds and cycles are
separate fields in the summary. The restart proposer is parent-independent, so this fixture's final qualities
matching across dispatchers does not establish equivalent trajectories for adaptive operators; the mutation cases
exercise a parent-dependent operator for determinism, not for quality.

## Failed approaches retained

The [original campaign's compact record](0bed72d-failed-summary.json), source `0bed72d64516f244ed59bbcea2c6816caf7bc9b7`,
completed all 132 individual attempts but failed its final comparison, and remains failed. It incorrectly required
equal state hashes across checkpoint-on and checkpoint-off runs. The retained record names exactly the two
fixed-semantics groups whose state hashes diverged — continuous dispatch with cheap evaluation, and continuous
dispatch with mixed durations — each splitting into two nine-attempt hash groups. Within each checkpoint setting,
every worker count and repetition matched. The corrected protocol adds checkpoint settings to the fixed-semantics
key. Its raw report is published as a release asset above; it was not rewritten.

Earlier uncommitted smoke diagnostics also rejected (1) affinity masks expanding after asynchronous work,
(2) identical masks in different Windows CPU groups, and (3) reaffinitizing existing threads without constraining
future threads. The final unnamed-job approach constrains the owned worker and its future threads. These development
smokes are debugging evidence, not source-pinned timing baselines. Linux starts through taskset before CLR startup;
its hosted result must be checked independently, not inferred from these Windows measurements.

Two campaigns on this same source were also discarded before this one, and both failures were fixed rather than
retried away: the first measured 28.19% foreign CPU on an attempt and a 2.02x repetition spread, which produced the
250 ms minimum measured phase, the declared 2.50x spread gate and the bounded contention-only retry; the second
failed its own CPU-time bound because the CPU counter was sampled outside the measured window, which produced the
sampling fix in `b6303d6`.

## Reproduce

Use a clean checkout of the passing source revision, with .NET 10 and the same disclosed hardware/runtime controls:

```powershell
dotnet build benchmarks/AiDotNet.Evolution.Performance -c Release
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release --no-build -- --profile TestResults/profile b6303d6208bf8f7a06d0cb9cee483e685ee5e2f2
```

The output directory must be new or empty, and the revision must match the built assembly. For regression decisions,
use an otherwise idle pinned machine and predeclared representative workloads/thresholds; do not promote these
diagnostic medians into a release gate.

## Evidence location convention

Other pilots in this repository keep their narrative under `docs/benchmarks/` with raw JSON beside it, while this
story keeps protocol evidence under `benchmarks/evidence/performance/`. Both conventions currently coexist;
unifying them would move other stories' files and is left to a separate change.
