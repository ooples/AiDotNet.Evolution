# V1-73 (#178): soak with segmented, streamed checkpoints

Measured at `a1d5d2f` on one workstation (AMD Ryzen 9 3950X, 64 GB, Windows 11, .NET 10 Release) with
`benchmarks/EvolutionSoak`: 200,000 null evaluations, a checkpoint every 1,000, `DirectoryEvolutionCheckpointStore`,
default `EvolutionCheckpointFormat.Auto`. "Recommended GC" is `DOTNET_GCConserveMemory=7`, as `RUNNING_AND_RESUMING.md`
advises for long runs. Every sample follows a full compacting collection. Growth is measured from 30,000 evaluations.

## Memory, handles, threads, child processes

| run | time | working set at 30k | working-set peak growth | working-set final growth | live heap (30k -> 200k) | handles | threads | children |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| capped 10,000, recommended GC | 83 s | 123 MB | +20.1% | +6.3% | 47 -> 47 MB | 218-221 | 7-10 | 0 |
| capped 10,000, recommended GC (run 2) | | 116 MB | +64.8% | +64.8% | 47 -> 47 MB | | | 0 |
| capped 10,000, recommended GC (run 3) | | 134 MB | +10.2% | -1.8% | 47 -> 47 MB | | | 0 |
| capped 10,000, default GC | 59 s | 441 MB | +8.6% | +3.2% | 47 -> 47 MB | 218-221 | 7-9 | 0 |
| unbounded, recommended GC | 35 s | 226 MB | +40.4% | +40.1% | 244 -> 343 MB | 252-272 | 18-24 | 0 |
| unbounded, default GC | 34 s | 269 MB | +17.7% | +17.7% | 244 -> 342 MB | 252-253 | 16-19 | 0 |

Before this change (`benchmarks/evidence/soak/README.md`): unbounded 2,799 MB working set and 576 s; capped live heap 143-199 MB.

**What the numbers say.** With a deduplication capacity, the run's live memory is flat: the heap after a full
collection is 47 MB at every sample of every run. The working set is not: it moves by tens of megabytes between
identical runs (+10% to +65% peak under the recommended GC), because it records how much freed memory the collector
keeps committed, not what the run holds. The default GC keeps more resident (441 MB) but steadier (+8.6%). The
unbounded runs grow because they remember every distinct genome by design (about 0.5 KB each in the heap).

**Against the #178 memory target.** The target is judged on the live heap after a full collection, which is what the
run holds: with a deduplication capacity it is 47 MB at 30,000 evaluations and 47 MB at 200,000, in every run, under
both GC settings, so it grows by 0%. Working set is reported alongside but is not the gate, because it also measures the
collector's retention policy: under the recommended GC three identical runs peaked at +10.2% to +64.8% with the same
47 MB live heap. Handle, thread and child-process counts do not trend in any run.

## Resume fidelity (5 kill points, recommended GC)

Each kill point starts a fresh process, kills its whole tree once it reports that many evaluations, and resumes.

| run | kills (evaluations reached) | resumed state hash equals uninterrupted |
| --- | --- | --- |
| capped 10,000 | 42,500; 112,800; 123,300; 140,500; 176,700 | 5 of 5 |
| unbounded (segmented checkpoints) | 42,500; 112,800; 123,300; 140,500; 176,700 | 5 of 5 |

## Orphaned processes (20,000 sandboxed executions, recommended GC)

`ProcessProgramExecutionEngine`, 2 s limit, 8 concurrent, CPython 3.11; every program starts a detached grandchild and
1 in 100 outlives the limit. 19,345 completed, 651 timed out, 4 other; **0 orphans** (`sandbox-20000-conserve.json`).

## Reproduce

```
dotnet build benchmarks/EvolutionSoak -c Release
set DOTNET_GCConserveMemory=7            (omit for the default GC)
set SOAK_DEDUP_CAPACITY=10000            (omit for unbounded)
dotnet benchmarks/EvolutionSoak/bin/Release/net10.0/EvolutionSoak.dll memory 200000 1000 <storeDir>
dotnet benchmarks/EvolutionSoak/bin/Release/net10.0/EvolutionSoak.dll resume 200000 1000 <workDir> 5
dotnet benchmarks/EvolutionSoak/bin/Release/net10.0/EvolutionSoak.dll sandbox 20000 <python> 8
```