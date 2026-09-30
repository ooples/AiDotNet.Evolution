# V1-72 (#177): per-evaluation cost against archive size

Measured at `47899db` on one workstation: AMD Ryzen 9 3950X (16 cores), 64 GB RAM, Windows 11, .NET 10 Release, and
OpenEvolve 0.3.2 (`411fb59`) on Python 3.13. Our side ran a published Release binary. Raw rows, one per repeat, are in
`scaling.json`, which was rewritten after every repeat. This supersedes the `1ccb67b` run: in that run the OpenEvolve
baseline started measuring before its population was full (98 of 100, 4,993 of 5,000).

**The machine was not quiet.** Other builds and test runs shared the CPU for most of this run, so every number
below is noisier than on an idle machine. The spread columns show how much. The conclusions below hold at the
extremes of each spread as well as at the medians.

## Method

Both systems use a null variation/LLM and a null evaluator in-process, one worker, 5 repeats, fresh processes.

- **Ours** (`benchmarks/EvolutionScaling`): a one-descriptor MAP-Elites archive with exactly `size` bins. The operator
  sweeps the bins, so the first `size` evaluations fill the archive completely; the run checks it holds exactly `size`
  elites. It then times the next 20,000 evaluations from inside the run, using the evaluation events' timestamps, so
  startup and the fill are excluded.
- **OpenEvolve** (`benchmarks/external/openevolve_scaling_run.py`): its population cannot be pre-filled. Each repeat
  first runs until the population reports exactly `size` programs (110, 1,010 and 5,012-5,018 iterations), then runs
  again with 200 more iterations, and reports (T(large) - T(baseline)) / 200. Every baseline and every large run
  held a full population. A run that hangs is killed and re-measured; none did.

## Results (microseconds per evaluation)

| archive / population | ours, median | ours, range | OpenEvolve, median | OpenEvolve, range |
| --- | --- | --- | --- | --- |
| 100 | 34.0 | 32.4-58.2 | 15,847 | 15,072-25,016 |
| 1,000 | 35.9 | 34.0-56.5 | 31,303 | 9,171-62,565 |
| 5,000 | - | - | 211,486 | 31,305-849,542 |
| 10,000 | 53.5 | 38.5-59.6 | - | - |
| 50,000 | 33.9 | 26.3-60.5 | - | - |

| metric | target | measured |
| --- | --- | --- |
| our cost at 50,000 elites / at 100 elites | <= 1.25x | **1.00x** (met) |
| OpenEvolve growth, population 100 to 5,000 | reported | **13.3x** (median) |

Our cost does not grow with the archive. Our slowest 50,000-elite repeat (60.5 us) is close to our slowest
100-elite repeat (58.2 us), and the medians are equal. The spread follows the machine's load, not the archive size.

OpenEvolve's figure is the difference of two runs of about 400-600 s each at 5,000, so load during either run moves
it a lot. Repeat 4 differenced to 6 s, repeat 3 to 170 s. Even so, the fastest 5,000 repeat (31 ms) is about twice
the fastest 100 repeat (15 ms), and the median grows 13x. On the quiet `1ccb67b` run, whose baseline was slightly
short of full, it grew 7.4x. Its growth is certain; its exact size needs a quiet machine.
## The defect this found

Before this change, cost grew with the archive: 60 us at 100 elites, 103 us at 1,000, 199 us at 10,000 and 5,856 us at
50,000 (97x). Filling a 50,000-elite archive took 92 s; it now takes 2 s. A stack sample showed uniform selection
copying every archive entry for each proposal. Both selection and parent sampling also read `Entries`, which rebuilt
a full copy whenever the archive's version changed, and in steady state that happens about every other evaluation.

`MapElitesArchive` now keeps its ordered entries in step with its cells: a replacement is one slot write, a new cell is
one binary insert, and only bulk changes rebuild the view. Uniform selection draws inspirations with a sparse
Fisher-Yates over that view in O(k). A test checks the chosen entries and the random-stream position against the
original algorithm, draw for draw, using an oracle built without the view, so every run's state hash is unchanged.

## Reproduce

```text
dotnet publish benchmarks/EvolutionScaling -c Release -o <dir>
python benchmarks/external/run_scaling.py --upstream <openevolve checkout> --ours-dll <dir>/EvolutionScaling.dll --output scaling.json
```