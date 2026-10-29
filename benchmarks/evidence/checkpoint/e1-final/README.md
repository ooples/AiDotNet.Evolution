# E-1: checkpoint write and resume at 10,000 elites, final

`benchmarks/EvolutionCheckpoint` at `e70ae6d` (4 KB Python programs, one per archive cell, 16 checkpoints over the run,
`DirectoryEvolutionCheckpointStore` on local NVMe), AMD 3990X, Windows 11, net10.0, plain JIT (no ReadyToRun for the
Evolution assemblies). Nine launches, run back to back on an otherwise idle machine. Each launch:

1. `EvolutionCheckpoint 10000 <dir>`: the full run, reporting write latency and an in-process resume
   (`run-10000-<n>.json`). That resume runs in a process that has already done the whole run, so its code is warm.
2. `EvolutionCheckpoint resume 10000 <copy>` on a fresh copy of that directory, twice: with the default no-GC region
   (`resume-10000-region-<n>.jsonl`) and with `EVOLUTION_CHECKPOINT_COLLECT=1`, which sets
   `SuspendGarbageCollectionDuringResume = false` (`resume-10000-collect-<n>.jsonl`). Each process resumes three
   times. **Repeat 0 is the cold resume a restarted process pays**, including JIT. Repeats 1 and 2 are warm.

All files are the harness output, unedited.

| measure | median of 9 | range | target |
| --- | ---: | --- | ---: |
| checkpoint write p50 | 59.9 ms | 52.0-64.3 | |
| checkpoint write p95 | **115.7 ms** | 110.2-120.1 | <= 250 ms (met) |
| resume, cold process, no-GC region (default) | **868.8 ms** | 851.0-883.9 | <= 1 s (met) |
| resume, cold process, collection on | 1,078.1 ms | 1,051.1-1,155.1 | |
| resume, warm (repeat 2), no-GC region | 536.1 ms | 417.3-584.5 | |
| resume, in-process after the run | 635.0 ms | 523.5-722.3 | |

Allocation per cold resume is 258 MB. Collections during it are 1/1/1 (gen0/gen1/gen2) with the region, against
33/27/5 without. The resumed state hash is `71b4407d...` on all 27 resume-only launches, and `ResumedStateMatches`
is true on all 9 full runs.

## Where the resume time went, and what changed

The earlier figure in [`../e1`](../e1), 1,668 ms, was the in-process resume. Measured cold at the start of this work it
was 1,229 ms (median of 9), with 742 ms warm. A `dotnet-trace` thread-time profile of the cold resume showed:

- **Single-threaded rebuild of every archive entry** (decoding genomes, copying evaluations), about 400 ms. It now runs
  with `Parallel.For`, capped at the segment decode window of 8 threads. Uncapped, about 100 threads stalled on every
  gen0 collection. The first failing entry in document order is still the one reported.
- **Garbage collection of a heap that is almost entirely the restored state**, about 190 ms. The resume now runs in a
  no-GC region sized at 8x the segment bytes (6.4x measured), which ends in `finally`.
- **A state hash that grew one StringBuilder over all 10,000 entries.** It now flushes inside every loop.
- **A fresh 4 MB large-object buffer per segment**, because the shared pool cached each returned buffer on the decode
  thread. A per-resume pool now hands it to the next read (282 -> 260 MB).
- **Reflection-emit metadata for the snapshot envelope.** It is now source-generated.

Measured on the way (median of 9 cold launches): 1,229 -> 1,103 ms (rebuild, hash, snapshot and bounded copies) ->
882 ms (no-GC region) -> 862 ms (buffer pool).

## What it does not show

- **Without the region, a cold resume is 1,078 ms**, over the target. A host that turns the option off needs either
  ReadyToRun code for the Evolution assemblies or less allocation. Publishing the benchmark with ReadyToRun gave cold
  resumes of 874-1,008 ms with collection on (five launches, exploratory, not committed). Both are planned follow-ups.
- One machine. Linux and macOS were not measured.
