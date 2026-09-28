# V1-72 (#177): per-evaluation cost against archive size

Measured at `1ccb67b` on one workstation: AMD Ryzen 9 3950X (16 cores), 64 GB RAM, Windows 11, .NET 10 Release, and
OpenEvolve 0.3.2 (`411fb59`) on Python 3.13. Nothing else ran. Raw rows, one per repeat: `scaling.json`.

## Method

Both systems use a null variation/LLM and a null evaluator in-process, one worker, 5 repeats, fresh processes.

- **Ours** (`benchmarks/EvolutionScaling`): a one-descriptor MAP-Elites archive with exactly `size` bins. The operator
  sweeps the bins, so the first `size` evaluations fill the archive completely; the run checks it holds exactly `size`
  elites. It then times the next 20,000 evaluations from inside the run, using the timestamps of the evaluation
  events, so startup and the fill are excluded. Every timed proposal lands in an occupied cell and replaces its holder
  about half the time.
- **OpenEvolve** (`benchmarks/external/openevolve_scaling_run.py`): its population cannot be pre-filled, so each
  repeat runs `size` and `size + 200` iterations with `population_size = size` and reports
  (T(size + 200) - T(size)) / (evaluations added). The first run leaves the population full (98 of 100, 996 of
  1,000, 4,993 of 5,000), so the 200 measured evaluations meet a full population. A run that hangs is killed and
  re-measured; none did.

## Results (median microseconds per evaluation)

| archive / population | ours | OpenEvolve |
| --- | --- | --- |
| 100 | 49.7 | 13,520 |
| 1,000 | 51.5 | 32,403 |
| 5,000 | - | 99,762 |
| 10,000 | 41.5 | - |
| 50,000 | 27.4 | - |

| metric | target | measured |
| --- | --- | --- |
| our cost at 50,000 elites / at 100 elites | <= 1.25x | **0.55x** (met) |
| OpenEvolve growth, population 100 to 5,000 | reported | **7.4x** |

Our per-repeat spread is under 3 us, except one 50,000-elite repeat at 55.5 us (the other four were 26.8-27.4 us).
OpenEvolve's spread widens with size: 62.7-209.7 ms at 5,000.

Our cost falls slightly at the largest sizes rather than staying exactly flat. The steady-state work per evaluation is
O(log n) in the archive, so larger archives do not cost more, and the figure varies with how much the GC runs during
the timed window. The target is an upper bound on growth, and it is met with a wide margin.

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
dotnet build benchmarks/EvolutionScaling -c Release
python benchmarks/external/run_scaling.py --upstream <openevolve checkout> --output scaling.json
```
