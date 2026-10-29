# E-1: cold resume from installed packages, ReadyToRun against IL

`benchmarks/EvolutionCheckpoint` (10,000 elites, 4 KB programs, `DirectoryEvolutionCheckpointStore` on local NVMe),
AMD 3990X, Windows 11, net10.0 (win-x64). The library code is a local merge of #352 (segmented journal and resume work,
`916626f`) with this branch (`a7a106e`); the merge itself, `555b8dd`, was not pushed.

Both arms reference **packages**, not projects: `AiDotNet.Evolution` and `AiDotNet.Evolution.Programs` packed from the
same source, once with `-p:EvoReadyToRun=true` (`0.7.0-r2r`) and once without (`0.7.0-il`), each restored into its
own empty package cache. The R2R arm's output holds `runtimes/<rid>/lib/net10.0/` images; the IL arm's does not.

One checkpoint directory was written once, then nine pairs were run, each arm on a fresh copy, interleaved with the
order alternating per pair (odd pairs R2R first). Each process resumes three times; **repeat 0 is the cold resume**.
The machine was shared with other work, which the pairing is there to cancel.

| arm | cold resume, median of 9 | range |
| --- | ---: | --- |
| ReadyToRun packages | **683.2 ms** | 655.7-772.1 |
| IL packages | 880.0 ms | 855.9-939.9 |

Per pair, ReadyToRun minus IL (ms): -83.8, -257.8, -197.9, -184.4, -176.9, -231.9, -204.4, -217.7, -193.9. Median
**-197.9 ms**; ReadyToRun was faster in all nine pairs. The resumed state hash is `71b4407d...` on all 18 launches.
Warm resumes (repeats 1 and 2) are left in the files unanalysed.

`resume-<arm>-<pair>.jsonl` is the harness output, unedited.
