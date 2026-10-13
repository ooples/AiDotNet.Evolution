# V1-25 (#119): reference-worker overhead on the hosted runners

Uploaded by the `TypeScript binding` workflow, run
[38453044192](https://github.com/ooples/AiDotNet.Evolution/actions/runs/38453044192), at PR #239 head `3f73e03`,
against each platform's native AOT host. The method is the same as in [`../a8a20f0-local`](../a8a20f0-local): 950 leases
after 50 warm-up, overhead = `serve(max_leases=1)` wall time minus the evaluator's own time.

| platform | worker | overhead p50 | overhead p95 | p95, first tenth | p95, last tenth | IPC round trip p95 |
|---|---|---:|---:|---:|---:|---:|
| linux-x64 | Python | 31.22 | **45.13** | 22.49 | 48.67 | 0.362 |
| linux-x64 | TypeScript | 31.37 | **46.52** | 23.33 | 50.62 | 0.212 |
| osx-arm64 | Python | 27.90 | **44.71** | 18.36 | 50.83 | 0.259 |
| osx-arm64 | TypeScript | 27.39 | **47.60** | 17.53 | 61.07 | 0.211 |
| win-x64 | Python | 69.96 | **109.13** | 68.41 | 132.96 | 0.624 |
| win-x64 | TypeScript | 67.18 | **89.95** | 63.29 | 92.12 | 0.290 |

All values in ms. **Target p95 < 2 ms: not met on any platform.** The clients' own request/reply costs 0.2-0.6 ms.
The rest is the host's fsync'd whole-state rewrite on every claim and commit, which also grows with retained work; see
the local README for the cause and the design change it would take.
