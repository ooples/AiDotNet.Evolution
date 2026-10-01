# V1-73 (#178): soak with segmented, streamed checkpoints

Measured at `a1d5d2f` on one workstation (AMD Ryzen 9 3950X, 64 GB, Windows 11, .NET 10 Release) with
`benchmarks/EvolutionSoak`: 200,000 null evaluations, a checkpoint every 1,000, `DirectoryEvolutionCheckpointStore`,
default `EvolutionCheckpointFormat.Auto`. "Recommended GC" is `DOTNET_GCConserveMemory=7`, as `RUNNING_AND_RESUMING.md`
advises for long runs. Every sample follows a full compacting collection. Growth is measured from 30,000 evaluations.

## Memory, handles, threads, child processes

| run | time | working set at 30k | working-set peak growth | working-set final growth | live heap (30k -> 200k) | handles | threads | children |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| capped 10,000, recommended GC | 83 s | 123 MB | +20.1% | +6.3% | 47 -> 47 MB | 218-221 | 7-10 | 0 |
| capped 10,000, recommended GC (run 2) | | 116 MB | +64.8% | +64.8% | 15 -> 47 MB | | | 0 |
| capped 10,000, recommended GC (run 3) | | 134 MB | +10.2% | -1.8% | 47 -> 47 MB | | | 0 |
| capped 10,000, default GC | 59 s | 441 MB | +8.6% | +3.2% | 47 -> 47 MB | 218-221 | 7-9 | 0 |
| unbounded, recommended GC | 35 s | 226 MB | +40.4% | +40.1% | 244 -> 343 MB | 252-272 | 18-24 | 0 |
| unbounded, default GC | 34 s | 269 MB | +17.7% | +17.7% | 244 -> 342 MB | 252-253 | 16-19 | 0 |

Before this change (`benchmarks/evidence/soak/README.md`): unbounded 2,799 MB working set and 576 s; capped live heap 143-199 MB.

**What the numbers say.** With a deduplication capacity, the run's live memory has a ceiling: the heap after a full
collection never exceeds 46.6 MiB (47 MB) in any sample of any capped run. Three of the four capped runs are at that
ceiling from the first sample. Run 2 is the exception: it held 14.6 MiB from 30,000 to 160,000 evaluations, then
stepped to 38.6 and 46.6 MiB at the next two samples and stayed there. That one-time step of about 32 MiB lands on the
same ceiling the other runs hold. Its cause was not identified, and it is why run 2's live heap reads 15 -> 47 MB.

The working set moves by tens of megabytes between identical runs (+10% to +65% peak under the recommended GC),
because it records how much freed memory the collector keeps committed, not what the run holds. The default GC keeps
more resident (441 MB) but steadier (+8.6%). The unbounded runs grow because they remember every distinct genome by
design (about 0.5 KB each in the heap).

**Against the #178 memory target.** The target is judged on the live heap after a full collection, which is what the
run holds. With a deduplication capacity, three of the four capped runs read 47 MB at both 30,000 and 200,000
evaluations under both GC settings, so they grew by 0%. Run 2 did not hold flat from its own 30,000-evaluation
sample: it rose from 15 to 47 MB. It never exceeded the 47 MB the other runs hold from the start, and it did not grow
after the step. Handle, thread and child-process counts do not trend in any run.
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