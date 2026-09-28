# V1-70 (#175): engine overhead, AiDotNet.Evolution vs pinned OpenEvolve (null evaluators)

Supersedes `../46e1141/`. **Not a headline claim (R12):** a C# controller against a Python one is expected to win on overhead.

Measured at main `5699058` on one workstation: AMD Ryzen 9 3950X (16 cores), 64 GB RAM, Windows 11, .NET 10 Release,
OpenEvolve 0.3.2 (`411fb59`) on Python 3.13. Nothing else ran during the measurement. Raw rows: `head-to-head.json`.

## Method

Each system runs a null variation/LLM and a null evaluator in-process, in fresh processes. Steady-state cost per
evaluation is (T(2N) - T(N)) / (evaluations completed by the 2N run minus those completed by the N run), which cancels
startup. N is 8,000 for ours and 300 for OpenEvolve: large enough that the difference is always positive. A
non-positive difference would be re-measured, never averaged in, and none occurred.

- 9 repeats per worker count. The two systems run back to back within a repeat.
- The ratio is the median of the per-repeat paired ratios. Its interval is a 95% percentile bootstrap (4,000 resamples).
- Evaluations are counted, not requested. OpenEvolve's are counted in per-process files, because concurrent appends to
  one file lost lines on Windows.

## Results

| workers | ours us/eval | OpenEvolve us/eval | ours / theirs [95% CI] | ours peak MB | OpenEvolve peak MB |
| --- | --- | --- | --- | --- | --- |
| 1 | 59.9 | 23,076 | 0.0026 [0.0026, 0.0029] | 82.7 | 139.8 |
| 2 | 65.3 | 15,242 | 0.0043 [0.0038, 0.0046] | 82.9 | 204.1 |
| 4 | 60.7 | 10,559 | 0.0058 [0.0056, 0.0063] | 82.7 | 333.4 |
| 8 | 60.5 | 8,689 | 0.0070 [0.0066, 0.0075] | 82.7 | 589.2 |
| 16 | 60.5 | 8,282 | 0.0075 [0.0071, 0.0082] | 82.9 | 1,099.2 |

Per-repeat spread of our cost: 56-70 us at every worker count.

## Targets

| metric | target | measured | status |
| --- | --- | --- | --- |
| our overhead per evaluation, 1-16 workers (N >= 8,000) | <= 100 us | 59.9-65.3 us | met |
| ours / OpenEvolve, 95% CI upper bound, every worker count | <= 0.05 | <= 0.0082 | met |
| our peak working set, 16 workers | <= 64 MB | 82.9 MB at `5699058`; 68.9 MB with the fix below; 64.1 MB with #197 as well | not yet met |

## Peak memory

Peak working set grew with run length: 43 MB at 1,000 evaluations, 83 MB at 16,000 and 204 MB at 64,000.

- **Cause:** a heap dump found one 32 MB byte array owned by the shared array pool. The end-of-run state hash covers
  every seen identity and cached result. It was built as one string and encoded into a pooled buffer three times its
  length, and the pool kept the buffer.
- **Fix, in this PR:** the hash is now streamed through fixed buffers, with an identical digest (tested). Cached results
  share empty collections.
- **Effect:** live heap at 64,000 evaluations fell from 178 MB to 44 MB, and peak working set from 204 MB to 103 MB.
- **What remains:** the rest of the peak at 16 workers is GC headroom driven by allocation rate. #197 (V1-71) halves
  allocation per evaluation. With both changes, the 16,000-evaluation run peaks at 64.1 MB. The final figure is taken
  once #197 has merged.

## Hangs

OpenEvolve deadlocked in 3 of about 180 runs: `n=600` at 4, 8 and 16 workers. These runs normally take 6 s. The
harness kills a run that exceeds 180 s, records it under `hangs` in the JSON, and re-measures it. OpenEvolve also
left pool workers alive after a run. These inherited the harness's output pipe and hung the first rerun for over an
hour, so child output now goes to files and every descendant is killed after each run.
