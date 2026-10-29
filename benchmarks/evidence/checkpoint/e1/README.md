# E-1: checkpoint write and resume with the archive journal

`benchmarks/EvolutionCheckpoint` (4 KB Python programs, one per archive cell, 16 checkpoints over the run,
`DirectoryEvolutionCheckpointStore` on the local NVMe SSD), Windows 11 development machine, net10.0. Three launches per
side, A and B interleaved. `base-*` is main `4323954`, which writes inline checkpoints. `new-auto-*` is this branch
with the default `EvolutionCheckpointFormat.Auto`, which moves to segments once an inline payload reaches about 1 MB.
The library source is the same through `438fb75`: later commits touched only tests, docs and whitespace. Write
latency on the new side is `SaveAsync` plus the segment writes since the previous save. The twelve JSON files are the
harness output, unedited.

| elites | side | write p50 | write p95 | resume | final payload | segment chain | resumed state hash |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1,000 | base | 144.4 ms | 252.9 ms | 862 ms | 4,387,490 B | - | matches (3/3) |
| 1,000 | new | 30.6 ms | 66.6 ms | 365 ms | 18,302 B | 4,124,445 B in 11 segments | matches (3/3) |
| 10,000 | base | 718.8 ms | 2,339.1 ms | 4,101 ms | 46,260,532 B | - | matches (3/3) |
| 10,000 | new | 45.2 ms | 99.1 ms | 1,668 ms | 12,529 B | 43,679,676 B in 15 segments | matches (3/3) |

Medians of three launches. Per-launch values are in the JSON files: the new side's 10,000-elite write p95 was 119,
94 and 99 ms, and its resume was 1,618, 1,702 and 1,668 ms.

## What this shows

- **Write p95 at 10,000 elites meets the 250 ms target**: 99 ms, against 2,339 ms inline. A save writes a 12.5 KB
  payload and one delta segment holding the cells that changed, instead of rewriting 46 MB.
- **The chain holds about what one inline checkpoint did** (43.7 MB against 46.3 MB), but it is written once over the
  run rather than once per save.

## What it does not show

- **Resume at 10,000 elites does not meet the 1 s target**: 1.67 s, down from 4.1 s. Resume still reads and decodes
  every elite once (43.7 MB of segments), validates the rebuilt state and saves a checkpoint. That cost is not profiled
  here. The likely candidates are JSON decoding of the genomes and the post-resume save.
- Three launches per side, not nine. The machine was busy with other builds during the runs, and it then crashed with
  a driver fault, so no more timing was taken. At 10,000 elites every launch on the new side beats every launch on the
  base side, in both write p95 and resume. At 1,000 elites the launches overlap: one new resume, 865 ms, was slower
  than one base resume, 623 ms. The absolute figures carry this machine's run-to-run variance.
