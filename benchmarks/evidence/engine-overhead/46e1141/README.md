# V1-30: engine overhead, AiDotNet.Evolution vs pinned OpenEvolve (null evaluators)

**Not a headline claim (R12):** a C# controller against a Python one is expected to win on overhead.

Method: each system runs a null LLM/variation and a null evaluator **in-process** (OpenEvolve gets no broker or HTTP hop),
in fresh processes. Steady-state cost per evaluation is (T(2N) − T(N)) / N, which cancels startup: N = 2,000 for ours and
100 for OpenEvolve. There are 9 repeats per cell. The 95% interval is a bootstrap over repeat-paired medians. Memory is
the peak resident set of the **whole process tree** (OpenEvolve runs a process pool), sampled every 50 ms.
Measured at main 46e1141, on this machine (Windows 11, GTX 1660 Ti host, shared load).

| workers | ours µs/eval | OpenEvolve µs/eval | ours / theirs [95% CI] | ours peak MB | OpenEvolve peak MB |
| --- | --- | --- | --- | --- | --- |
| 1 | 186 | 17,954 | 0.010 [-0.068, 0.064] | 49 | 150 |
| 2 | 196 | 9,583 | 0.020 [0.016, 0.032] | 49 | 222 |
| 4 | 185 | 5,165 | 0.036 [-0.069, 0.091] | 48 | 367 |
| 8 | 174 | 5,508 | 0.032 [0.018, 0.085] | 48 | 655 |
| 16 | 164 | 4,615 | 0.036 [0.014, 0.087] | 48 | 1,184 |

- **Scaling:** our per-evaluation overhead is flat to slightly falling from 2 to 16 workers, and memory is flat (about 48 MB). OpenEvolve's memory grows with its pool (222 MB → 1.18 GB).
- **The 4-worker interval crosses zero** because OpenEvolve's 100-iteration difference is noisy: some bootstrap draws have T(2N) < T(N). It is reported as measured.
- **Correction:** an earlier single-shot reading suggested our cost *grows* with run length. Repeated launches show the opposite: 164 → 89 → 61 µs at N = 2,000 / 8,000 / 32,000, as JIT tiering amortises.

## Allocation (the real engine hot path found here)
A null evaluation allocated **about 91 KB** (GC.GetTotalAllocatedBytes, median of 3 launches). By type, the largest share was
`String`/`Char[]`/`StringBuilder`/`Byte[]` plus a `HashProviderCng`: identity hashing. With #138's hashing fix
(one-shot SHA-256 and a single hex buffer) it is **59.5 KB per evaluation (−35%)**. The remainder is genome-identity string
building (`Char[]` 14 KB, `String` 13 KB, `String[]` 5 KB, UTF-8 `Byte[]` 5 KB); reducing it needs allocation call
stacks and is follow-up work, not guessed at here.