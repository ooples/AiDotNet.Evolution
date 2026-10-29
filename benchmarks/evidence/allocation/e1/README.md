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

## Over 8,000 evaluations, after the bounded-copy change

#176's criterion asks for at least 8,000 evaluations. `EvolutionOverhead allocation 8000`, same workload and machine,
built from this branch with the resume work (`687f075` plus the bounded-copy commit `586e100`): three launches, in
`runs-8000.jsonl`.

| evaluations | bytes per evaluation |
| --- | --- |
| 4,000 | 9,979 (one launch) |
| 8,000 | **9,947** (9,948 / 9,947 / 9,947) |

**9,947 bytes per evaluation over 8,000 evaluations, against the 16 KB target.** The drop from 10,573 at 4,000 is the
bounded-copy change (`EvolutionCollection.ToBoundedArray` and `EvolutionTaskResult` size one array from the
reported count instead of filling a list and copying it, and `CopyFinite` no longer allocates a closure): 594 bytes
per evaluation, a fixed cost per result. `AllocationBudgetTests` now commits 12,500 bytes, the previous figure less
that saving; its failure line is 15,000 bytes.
