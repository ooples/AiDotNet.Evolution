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
Bytes per elite is about 1.25x the program size (target ≤ 2x): met. At 1,000 elites writes meet the 250 ms p95.
At 10,000 elites a full 51 MB snapshot per checkpoint misses both targets (p95 ≤ 250 ms, resume ≤ 1 s). Meeting them
needs incremental checkpoints (write only the archive changes since the last snapshot), which is a format change and
is left open on #179.
