# US-09 current-stack evidence

Runtime revision: `1c2237275449c98df7524ff01dd260a3f8953dfb`.
Raw uncompressed SHA-256: `948436d9d93a7e22facdbafa5e8badc10e511ced55abe702ffd161b99e9f9edd`.

`report.json.gz` retains all 144 primary and 144 four-worker replay runs, terminal evaluations, constraints, scalar quality, objectives, costs, membership and state hashes. `summary.json` is independently recomputed by `benchmarks/analysis/pareto_study.py`; tests check its hash and reject altered volume, budgets, pairing, replays and constraints.

## Protocol and results

24 paired seeds, eight identical initial genomes, two authored squared-distance tasks, three policies, 128 charged calls each. Shared mutation, scalarization, feasibility gate and call/proposal caps. Scalar-single capacity 1; scalar-map64 and Pareto64 capacity 64. Fixed minimizing bounds [0,2], reference (1,...,1), resolution zero. Constraint x+y<=1.4. Rejections cost one call. **36,864 calls including replays; no model calls.**

| Objectives | Method | Median HV | Median retained | Median objective minima (lower is better) |
|---|---|---:|---:|---|
| 2 | Scalar-single | 0.765323 | 1 | 0.244863, 0.255408 |
| 2 | Scalar-map64 | 0.923321 | 39 | 0.015209, 0.051326 |
| 2 | Pareto64 | 0.948164 | 47 | 0.002574, 0.013502 |
| 3 | Scalar-single | 0.462995 | 1 | 0.217742, 0.572317, 0.545156 |
| 3 | Scalar-map64 | 0.736014 | 39 | 0.018113, 0.073731, 0.061178 |
| 3 | Pareto64 | 0.751038 | 64 | 0.002180, 0.077742, 0.031585 |

Versus map64, paired median HV differences: +0.020520 (24 wins/0 losses, 2D), +0.016899 (21 wins/3 losses, 3D). Versus single-best: 24 wins on both fixtures. Difference-of-medians is not median-paired-difference.

The second 3D marginal minimum worsens, 0.073731 → 0.077742. Better volume is not improvement on every objective. Descriptive median run time is higher: map64 3.89/3.77 ms versus Pareto 7.24/17.59 ms. Timing was sequential, overlapped local tests, and was not isolated/randomized; no causal speedup claim. Full objective maxima, vectors, elapsed observations and losses remain available.

## Reproduce

Build the pinned revision in Release, then:

```powershell
dotnet benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll --pareto-study 1c2237275449c98df7524ff01dd260a3f8953dfb new-report.json
python benchmarks/analysis/pareto_study.py new-report.json new-summary.json
python -m unittest discover -s benchmarks/analysis -p test_pareto_study.py -v
```

The harness checks both compiled revisions and refuses overwrites. Repeated timing differs; deterministic sample/state matches are the reproducibility criterion. The artifact test exactly regenerates the retained summary.

`failed-09a3a4d.json.gz` preserves 144 primary + 144 replay failures from the initial genome-ownership bug (zero calls). These are excluded from successful metrics. The narrower intermediate implementation was rejected as final delivery after discovering #52's broader functionality; its evidence remains local at `TestResults/us09-narrow-prototype-evidence`.

Scope: authored development evidence, not real latency/memory, held-out efficacy, exploration superiority, or OpenEvolve comparison. The [historical campaign](https://github.com/ooples/AiDotNet.Evolution/blob/2de0419e8aab7dc656d59af9358134b8483a7544/benchmarks/evidence/pareto/README.md) includes a disconnected task without demonstrated Pareto advantage; it remains part of the evidence history.
