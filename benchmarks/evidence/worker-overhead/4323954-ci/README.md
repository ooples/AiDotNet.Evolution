# E-1: reference-worker overhead at main `4323954`, after the append-only work journal

Uploaded by the `TypeScript binding` workflow, run
[40556265534](https://github.com/ooples/AiDotNet.Evolution/actions/runs/40556265534), on `main` at `4323954` (release
0.7.0), against each platform's native AOT host. The harness and method are unchanged from [`../3f73e03-ci`](../3f73e03-ci):
950 leases after 50 warm-up, overhead = `serve(max_leases=1)` wall time minus the evaluator's own time. The six JSON files
are the workflow's `worker-overhead-<rid>` artifacts, copied byte for byte.

| platform | worker | overhead p50 | overhead p95 | p50, first tenth | p50, last tenth | p95, first tenth | p95, last tenth | IPC round trip p95 |
|---|---|---:|---:|---:|---:|---:|---:|---:|
| linux-x64 | Python | 0.94 | 5.38 | 0.90 | 0.95 | 1.11 | 1.11 | 0.154 |
| linux-x64 | TypeScript | 0.74 | 1.03 | 0.78 | 0.72 | 0.91 | 1.12 | 0.068 |
| osx-arm64 | Python | 3.54 | 7.50 | 2.93 | 3.45 | 9.13 | 5.10 | 0.576 |
| osx-arm64 | TypeScript | 3.26 | 6.44 | 3.09 | 3.38 | 5.27 | 5.91 | 0.371 |
| win-x64 | Python | 9.17 | 68.22 | 10.92 | 8.54 | 60.69 | 16.45 | 0.623 |
| win-x64 | TypeScript | 7.42 | 11.74 | 7.55 | 7.40 | 11.97 | 10.21 | 0.240 |

All values in ms.

## What this shows

**The drift is gone.** At `3f73e03` the linux-x64 Python p50 rose from 18.7 ms in the first tenth of the run to 44.0 ms
in the last. Here it is 0.90 ms and 0.95 ms. On every platform and worker the last tenth is within the first tenth's
spread: on three of the six the last tenth's p95 is the lower one, on one it is equal, and on two it is higher by
0.2 and 0.6 ms. The cause recorded at `3f73e03` was the host rewriting and fsyncing its whole delivery state on every claim and commit. The append-only work journal with group
commit (`EvolutionWorkJournal`, `work.log` beside the `work.current` snapshot) replaced that rewrite before this run:
each claim or commit appends one record whose size depends on what it changed, not on how much work is retained.

So the per-lease drift needed no further host change. It was a stale measurement: the comparison doc still quoted the
`3f73e03` figures.

## What it does not show

- **The 2 ms p95 target is still not met everywhere.** linux-x64 TypeScript meets it, and linux-x64 Python's p50 is
  under 1 ms. Python's p95 (5.4 ms) comes from a tail of slow leases spread across the run, not from drift, because the
  first and last tenths both have p95 1.11 ms. macOS sits at 3-7 ms and Windows at 7-12 ms p50. A likely cause is the
  cost of a flush to disk on the hosted runners: each lease makes two durable acknowledgements (claim, commit). That is
  not profiled here and is stated as a hypothesis. The two flushes are the durability contract ("an acknowledged commit
  survives a crash"), and this phase does not weaken it.
- One CI run per platform is a single launch. The flatness claim rests on the first and last tenth of each run, 95 leases
  each, on six independent runners. It is not a median of repeated launches.
