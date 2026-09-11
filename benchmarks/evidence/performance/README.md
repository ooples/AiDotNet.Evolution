# US-10 engine profiling evidence

Source: `4a7b50e996d62838f51081170a2870b818ce9e8a`. Recorded September 11, 2026, 13:15:11–13:18:13 UTC.
[Complete passing report](4a7b50e.json): **44 declared cases × 3 repetitions = 132 passed attempts; eight worker-determinism groups passed.**
All raw measurements and quality curves are embedded in the report. The local campaign directory also retains
per-attempt inputs, worker logs and outputs; CI uploads that complete directory for its smoke runs.
This is engine orchestration/scaling evidence, not evolved-algorithm speedup or competitor superiority.

## Controls and limits

.NET 10.0.12, Windows build 26200, x64, AMD family 23/model 49/stepping 0; CPU group 0,
affinity mask F (four logical processors), workstation GC, tiered compilation disabled, automatic CPU-group
assignment/use disabled. Each measured attempt ran in a fresh child with a group-and-mask-constrained unnamed job.
Setup, warmup and a full GC precede the measured phase. Peak working set is the **process-lifetime high-water mark**,
including startup/setup/warmup, not measured-phase allocation. Before/after and pre-measurement peak baselines are retained.
No global machine settings or other processes were changed. Background host load, frequency and power policy were
not controlled. One authored sphere objective and one fixed seed are diagnostic, not calibrated regression thresholds.
The [protocol](../../AiDotNet.Evolution.Performance/README.md) explains all factors, instrumentation and limitations.

## Main observations

- Cheap eight-dimensional batch search with one worker completed about **6,617 evaluations/s**, versus 393,604
  precomputed evaluator-only calls/s. The engine includes proposals, owned genomes, validation, archive updates,
  observers and final state; this is a lower-bound overhead comparison, not a quality comparison.
- Increasing workers from one to four did not materially improve cheap batch throughput (6,617 versus 6,672/s).
  Mixed asynchronous latency did benefit from more workers and continuous dispatch; both utilization and quality
  over elapsed time are shown below. These are simulated waits, not CPU/GPU/model workloads.
- Checkpoint-on cheap batch elapsed time rose from 38.686 to 179.674 ms for 256 evaluations; managed allocation
  rose from 37,537,032 to 166,177,152 bytes. Storage-call timing alone was only 3.113 ms: it excludes serialization
  and other checkpoint work, so it would substantially understate total overhead.
- Checkpoint-on batch and continuous runs wrote **nine and seven checkpoints**, respectively, under interval 32.
  Continuous mode drains its queue before saving, so its on/off contrast includes changed scheduling/context.
  Do not interpret its lower checkpoint total as a like-for-like faster serializer.

## Mixed-duration dispatch: utilization and quality together

Checkpoints off, 256 evaluations, eight-dimensional objective, MaxInFlight=8. Higher quality is better;
all six configurations ended at −13.398181. The −25 threshold and 500 ms view below are descriptive slices of the
retained complete curves, not predeclared significance tests. All repetitions reached the threshold.

| Dispatch / workers | Median run ms | Median evaluator-slot utilization | Median ms to quality ≥ −25 | Median quality at 500 ms |
| --- | ---: | ---: | ---: | ---: |
| Batch / 1 | 2684.223 | 98.04% | 246.921 | -23.697618 |
| Continuous / 1 | 2663.143 | 99.20% | 196.339 | -23.697618 |
| Batch / 2 | 1475.800 | 87.04% | 138.821 | -23.585469 |
| Continuous / 2 | 1327.933 | 99.05% | 102.247 | -18.228556 |
| Batch / 4 | 883.120 | 71.12% | 90.980 | -13.398181 |
| Continuous / 4 | 647.301 | 95.98% | 53.830 | -13.398181 |

Slot utilization includes asynchronous waits; it is not CPU utilization. Raw process CPU milliseconds are separate.
The restart proposer is parent-independent, so this fixture's final qualities matching across dispatchers does not
establish equivalent trajectories for parent-dependent or adaptive operators.

## Full scaling summary

Throughput and managed KiB are per evaluation for engine/evaluator cases, per snapshot for archive cases, and
per restore for checkpoint cases. Elapsed time covers each entire measured invocation (256 evaluations,
16 snapshots or two restores). Throughput/elapsed/allocation are medians; peak memory is the maximum of the
three fresh-process lifetime peaks. Archive fixture cell counts are actual occupancy; engine cell factors are capacity.

| Case | Operations/s | Total ms | Managed KiB/op | Max lifetime peak MiB |
| --- | ---: | ---: | ---: | ---: |
| engine-w1-Batch-cheap-cp0 | 6617.31 | 38.686 | 143.192 | 52.20 |
| engine-w1-Batch-cheap-cp1 | 1424.81 | 179.674 | 633.916 | 58.46 |
| engine-w1-Batch-mixed-cp0 | 95.37 | 2684.223 | 144.077 | 52.46 |
| engine-w1-Batch-mixed-cp1 | 93.16 | 2748.014 | 649.026 | 61.24 |
| engine-w1-Continuous-cheap-cp0 | 6279.30 | 40.769 | 145.283 | 52.54 |
| engine-w1-Continuous-cheap-cp1 | 3222.45 | 79.443 | 276.906 | 58.53 |
| engine-w1-Continuous-mixed-cp0 | 96.13 | 2663.143 | 148.120 | 52.77 |
| engine-w1-Continuous-mixed-cp1 | 95.78 | 2672.724 | 282.071 | 60.13 |
| engine-w2-Batch-cheap-cp0 | 6722.25 | 38.083 | 143.192 | 52.21 |
| engine-w2-Batch-cheap-cp1 | 1406.12 | 182.062 | 633.916 | 58.50 |
| engine-w2-Batch-mixed-cp0 | 173.47 | 1475.800 | 143.991 | 52.38 |
| engine-w2-Batch-mixed-cp1 | 164.87 | 1552.721 | 652.366 | 61.25 |
| engine-w2-Continuous-cheap-cp0 | 6147.37 | 41.644 | 145.283 | 52.50 |
| engine-w2-Continuous-cheap-cp1 | 3116.83 | 82.135 | 276.906 | 58.31 |
| engine-w2-Continuous-mixed-cp0 | 192.78 | 1327.933 | 147.913 | 52.78 |
| engine-w2-Continuous-mixed-cp1 | 185.53 | 1379.796 | 284.429 | 60.37 |
| engine-w4-Batch-cheap-cp0 | 6671.97 | 38.370 | 143.192 | 52.14 |
| engine-w4-Batch-cheap-cp1 | 1393.57 | 183.701 | 633.916 | 58.54 |
| engine-w4-Batch-mixed-cp0 | 289.88 | 883.120 | 143.861 | 52.40 |
| engine-w4-Batch-mixed-cp1 | 282.64 | 905.737 | 655.153 | 62.13 |
| engine-w4-Continuous-cheap-cp0 | 6277.68 | 40.779 | 145.283 | 52.43 |
| engine-w4-Continuous-cheap-cp1 | 3218.68 | 79.536 | 276.906 | 58.21 |
| engine-w4-Continuous-mixed-cp0 | 395.49 | 647.301 | 147.376 | 52.73 |
| engine-w4-Continuous-mixed-cp1 | 361.91 | 707.358 | 284.391 | 60.34 |
| evaluation-w1-cheap | 393603.94 | 0.650 | 1.268 | 44.02 |
| evaluation-w1-mixed | 95.32 | 2685.566 | 1.582 | 44.21 |
| evaluation-w2-cheap | 484573.16 | 0.528 | 1.278 | 44.18 |
| evaluation-w2-mixed | 189.16 | 1353.350 | 1.584 | 44.38 |
| evaluation-w4-cheap | 543524.42 | 0.471 | 1.291 | 44.18 |
| evaluation-w4-mixed | 381.56 | 670.927 | 1.594 | 44.25 |
| engine-cells-32 | 6927.76 | 36.953 | 139.720 | 51.87 |
| engine-cells-4096 | 6315.72 | 40.534 | 145.878 | 52.41 |
| engine-dimensions-2 | 12719.99 | 20.126 | 59.489 | 51.33 |
| engine-dimensions-32 | 2019.47 | 126.766 | 469.514 | 52.93 |
| engine-islands-2 | 5933.98 | 43.141 | 144.183 | 52.30 |
| engine-islands-4 | 5955.95 | 42.982 | 144.740 | 52.49 |
| archive-cells-100 | 40160.64 | 0.398 | 7.392 | 40.91 |
| archive-cells-1000 | 6957.13 | 2.300 | 66.267 | 46.72 |
| archive-cells-10000 | 500.14 | 31.991 | 628.817 | 66.21 |
| archive-dimensions-8 | 6392.58 | 2.503 | 66.322 | 49.29 |
| archive-dimensions-32 | 5972.82 | 2.679 | 66.509 | 55.89 |
| checkpoint-evaluations-32 | 166.06 | 12.043 | 4047.195 | 56.65 |
| checkpoint-evaluations-256 | 24.71 | 80.926 | 29293.527 | 63.64 |
| checkpoint-evaluations-2048 | 5.21 | 384.081 | 118997.477 | 90.68 |

## Failed approaches retained

[Original committed campaign](0bed72d-failed.json), source `0bed72d64516f244ed59bbcea2c6816caf7bc9b7`,
completed all 132 individual attempts but failed its final comparison. That profiler incorrectly required equal
state hashes across checkpoint-on/off runs. Within each checkpoint setting, every worker count and repetition
matched; across settings, continuous queue draining changed lineage/context. The passing revision adds checkpoint
settings to the fixed-semantics key. The original report remains failed and has not been rewritten.

Earlier uncommitted smoke diagnostics also rejected (1) affinity masks expanding after asynchronous work,
(2) identical masks in different Windows CPU groups, and (3) reaffinitizing existing threads without constraining
future threads. The final unnamed-job approach constrains the owned worker and its future threads. These development
smokes are debugging evidence, not source-pinned timing baselines. Linux starts through taskset before CLR startup;
its hosted result must be checked independently, not inferred from these Windows measurements.

## Reproduce

Use a clean checkout of the passing source revision, with .NET 10 and the same disclosed hardware/runtime controls:

```powershell
dotnet build benchmarks/AiDotNet.Evolution.Performance -c Release
dotnet run --project benchmarks/AiDotNet.Evolution.Performance -c Release --no-build -- --profile TestResults/profile 4a7b50e996d62838f51081170a2870b818ce9e8a
```

The output directory must be new or empty. For regression decisions, use an otherwise idle pinned machine and
predeclared representative workloads/thresholds; do not promote these diagnostic medians into a release gate.

