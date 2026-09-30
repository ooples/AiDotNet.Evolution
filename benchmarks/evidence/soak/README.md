# V1-73 (#178): long-run soak

Measured on one workstation: AMD Ryzen 9 3950X (16 cores), 64 GB RAM, Windows 11, .NET 10 Release. Every run is built
from `test/v1-73-soak` merged with the open performance PRs #198, #200 and #201, so the numbers describe the engine as it
will be once those land. Harness: `benchmarks/EvolutionSoak` (modes `memory`, `resume`, `sandbox`).

## Memory, handles, threads, child processes (200,000 null evaluations, checkpoint every 1,000)

Each sample is taken after a full compacting collection, so it shows what the process retains rather than where the
collector happened to be. Raw samples: `memory-capped-10000.json`, `memory-uncapped.json`.

| evaluations | working set, `DeduplicationCapacity = 10000` | working set, unbounded (default) |
| --- | --- | --- |
| 10,000 | 227.6 MB | 226.5 MB |
| 50,000 | 263.6 MB | 605.2 MB |
| 100,000 | 247.8 MB | 1,210.5 MB |
| 150,000 | 242.7 MB | 1,969.2 MB |
| 200,000 | 221.1 MB | 2,799.0 MB |
| run time | 95 s | 576 s |

| metric | target | capped | unbounded |
| --- | --- | --- | --- |
| working-set growth after warm-up (from 30,000) | <= 10% | peak +6.5%, final -10.7% | +1,031% |
| GC heap after warm-up | - | 198.6 MB, then 142.6 MB | grows ~1.9 KB/evaluation |
| handle / thread / child-process growth | 0 | 213-218 / 6-9 / 0 | 213-218 / 5-10 / 0 |

Handles and threads move within a small band with the thread pool and never trend; no child process is ever present.

**Why the default grows.** Duplicate detection and the evaluation cache keep every distinct evaluated genome, by design,
so an unbounded run retains about 0.7 KB per distinct evaluation, and each checkpoint re-serialises that whole state.
`DeduplicationCapacity` (new) bounds both, forgetting the oldest committed genome first but never one an archive,
elite index, history or pending-artifact queue still holds. Incremental checkpoints, which remove the per-save
re-serialisation for unbounded runs, are a separate change.

**A quadratic cost this found.** After every batch the engine serialised its whole state, including the growing seen
set and cache, even when no checkpoint was due: 20,000 checkpointed evaluations took 84 s. The capture now keeps only
the bounded parts and logs changes to the seen set and cache since the boundary; a save reconstructs the boundary from
that log. The same run takes 9.9 s.

## Resume fidelity (200,000 evaluations, capacity 10,000, 5 kill points)

The harness runs once uninterrupted, then for each of five random kill points starts a fresh process, kills its whole
process tree once it reports that many evaluations, and resumes from the checkpoint store to completion. Raw:
`resume-capped-10000.json`.

| kill target | killed after | resumed state hash equals uninterrupted |
| --- | --- | --- |
| 42,456 | 42,500 | yes |
| 112,734 | 112,800 | yes |
| 123,294 | 123,300 | yes |
| 140,439 | 140,500 | yes |
| 176,660 | 176,700 | yes |

**5 of 5.**

## Orphaned processes (20,000 sandboxed executions)

`ProcessProgramExecutionEngine` with a 2 s time limit, 8 concurrent, running CPython 3.11. Every program starts a
detached grandchild (`DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP`) that would sleep for 120 s, and 1 in 100 programs
sleeps past the time limit. Survivors are found by a marker in their command line, whatever their parent now is.

Two controls run first and must pass, or the run aborts: the counter must see a marked sleeper started outside the
sandbox, and the same program run outside the sandbox must leave its grandchild behind. So a zero is the sandbox's
doing, not a spawn that never happened. Raw: `sandbox-20000.json`.

| executions | completed | timed out | other | marked processes seen during the run | orphans after |
| --- | --- | --- | --- | --- | --- || 20,000 | 19,787 | 213 | 0 | 1 (a tree mid-teardown) | **0** |

## Reproduce

```text
dotnet build benchmarks/EvolutionSoak -c Release
SOAK_DEDUP_CAPACITY=10000 dotnet benchmarks/EvolutionSoak/bin/Release/net10.0/EvolutionSoak.dll memory 200000 1000 <dir>
SOAK_DEDUP_CAPACITY=10000 dotnet benchmarks/EvolutionSoak/bin/Release/net10.0/EvolutionSoak.dll resume 200000 1000 <dir> 5
dotnet benchmarks/EvolutionSoak/bin/Release/net10.0/EvolutionSoak.dll sandbox 20000 <python.exe> 8
```
