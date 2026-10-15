# V1-74: checkpoint write and resume cost (#179)

Measured with `benchmarks/EvolutionCheckpoint` (4 KB Python programs, one per archive cell, 16 checkpoints over the run,
`DirectoryEvolutionCheckpointStore`), on the Windows 11 development machine, net10.0, main 5699058 plus this change.
Write latency is the store's `SaveAsync` alone; resume is a fresh engine restoring the final checkpoint.

| elites | write p50 | write p95 | resume | final checkpoint | bytes per elite | resumed state hash |
| --- | --- | --- | --- | --- | --- | --- |
| 1,000 (before) | 329 ms | 925 ms | 1,249 ms | 5.0 MB | 5,028 | matches |
| 1,000 (after) | 79 ms | 179 ms | 540 ms | 5.0 MB | 5,028 | matches |
| 10,000 (before) | n/a | n/a | n/a | could not be written | n/a | n/a |
| 10,000 (after) | 1,183 ms | 3,128 ms | 5,218 ms | 51 MB | 5,105 | matches |

The `final checkpoint` and `bytes per elite` columns above are the transcribed `5699058` figures. The
re-measurement below did not reproduce them, so use its values for any current comparison.

## Re-measured at current main, with the output committed

The table above was transcribed from a run whose output was never committed, which US-04 (#22)
classified as unsupported: every number in it existed nowhere else in the repository. The two
*after* rows are now re-measured at main `86d56c9`, and the raw benchmark output is in
[`86d56c9/re-measurement.json`](86d56c9/re-measurement.json).

| elites | write p50 | write p95 | resume | final checkpoint | bytes per elite | resumed state hash |
| --- | --- | --- | --- | --- | --- | --- |
| 1,000 | 79.8 ms | 158.5 ms | 545.9 ms | 4,387,450 B | 4,387 | matches |
| 10,000 | 1,000.5 ms | 2,959.4 ms | 5,287.7 ms | 46,262,658 B | 4,626 | matches |

The latencies reproduce - 79.8 against 79, 545.9 against 540, 5,287.7 against 5,218, with both p95
figures lower - and `ResumedStateMatches` is true in both runs.

**The byte figures do not reproduce, and that is stated rather than rounded away.** The checkpoint is
smaller than the card recorded: 4,387 and 4,626 bytes per elite against 5,028 and 5,105, so 4.4 MB
and 46 MB rather than 5.0 MB and 51 MB. The card does not say what the format was at `5699058`, so
this is recorded as a change between the two commits and not as a match.

**The *before* rows are not re-measurable here.** They are `5699058` without its fix; reproducing
them needs that commit reverted, which this run does not do. They stay in the table as history.

## What changed
- **Saves stop re-reading the previous snapshot.** Each save loaded and fully re-validated the newest snapshot (the
  previous multi-megabyte file) to check succession. The store now keeps the checkpoint it last wrote with a SHA-256 of
  its exact bytes, and reuses it while the file on disk is still byte-for-byte that file. Anything else, and every
  resume, takes the full validating path.
- **Large checkpoints can be written.** The document checksum passed the whole payload through
  `EvolutionHash.Combine`, bounded at 16 MiB of characters, while checkpoints may be 256 MB; about 10,000 elites of
  4 KB programs failed to checkpoint. Payloads Combine accepted keep the identical inline checksum (existing snapshots
  still verify); only ones it refused use a payload digest.

## Against the targets
Bytes per elite is 1.07x the program size at 1,000 elites (4,387 / 4,096) and 1.13x at 10,000 (4,626 / 4,096),
against a target of 2x: met. The 1.25x this section used to claim came from the transcribed byte figures,
which the re-measurement did not reproduce. At 1,000 elites writes meet the 250 ms p95. At 10,000 elites a
full 46 MB snapshot per checkpoint misses both targets (p95 ≤ 250 ms, resume ≤ 1 s). Meeting them
needs incremental checkpoints (write only the archive changes since the last snapshot), which is a format change and
is left open on #179.
