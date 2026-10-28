# E-1: allocation per evaluation on the null-task path

`EvolutionOverhead allocation 4000` repeats `AllocationBudgetTests`' workload exactly: 4 real parameters, a MAP-Elites
archive with 64 bins, batch 8, one worker, no checkpoint store, a 500-evaluation warm-up run, then a measured
4,000-evaluation run, with `GC.GetTotalAllocatedBytes(precise: true)` around the measured run. Windows 11, net10.0. The
same harness source was built against main `4323954` and against this branch at `438fb75`. Three launches each, in
`runs.jsonl`.

| side | bytes per evaluation |
| --- | --- |
| `4323954` | 16,759 (16,759 / 16,759 / 16,759) |
| `438fb75` | 10,573 (10,573 / 10,573 / 10,573) |

That is 6,186 bytes per evaluation less. A dotnet-trace allocation profile of the base side attributed the saving to:
`EvolutionLineage` presizing two 256-entry lists (about 4.1 KB), `EvolutionCellKey` building bins through a 64-entry list
and LINQ (about 0.85 KB), a carried-call document recorded with no checkpoint store (about 0.4 KB), and a result allocated
per carried-call miss (about 0.4 KB).

`AllocationBudgetTests` committed 19,300 bytes, measured by CI on Linux. It now commits 13,100: the old figure less the
saving above, since every removed allocation is a fixed per-evaluation cost that does not depend on the platform. Its 20%
headroom puts the failure line at 15,720 bytes, under the 16 KB target. Linux CI has not yet measured the new figure
directly. It only reports a value when the test fails.
