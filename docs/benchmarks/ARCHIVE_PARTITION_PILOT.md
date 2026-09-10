# Sparse-grid versus fixed-centroid development pilot

Source: `16fdc5baa4c2dd9aa724a686cc0bdb9caad7854b`. Protocol `archive-partition-development-v1`.
[Every run and geometry definition](archive-pilot-16fdc5b.json) is retained, including the SHA-256 of the full trace.

Forty runs completed: two methods × two 12-dimensional tasks × ten paired seeds × 256 evaluator calls = 10,240
calls, with zero incomplete/failed runs. Both methods used the same eight initial genomes, mutation parameters,
uniform parent selection, evaluation/proposal caps and a maximum of 32 retained elites. The grid has 4,096 possible
cells and remains sparse; the centroid archive has 32 frozen uniform sites. These are equal elite-slot budgets,
**not equal measured RAM** or fitted-CVT comparisons. Setup/partition preparation is not included in call budgets.

Both retained repertoires are projected onto an independently frozen 32-site reference partition. Reference utility
is the sum of `1/(1+loss)` for retained reference-cell winners, divided by 32; empty cells and failed runs contribute
zero. Search-archive occupancy percentages are not compared across incompatible partitions.

| Task | Method | Median final loss ↓ | Median reference utility ↑ | Mean occupied reference cells |
| --- | --- | ---: | ---: | ---: |
| Quadratic12 | SparseGrid | 0.491085 | 0.126741 | 8.2 |
| Quadratic12 | FixedCentroid | 0.435748 | 0.132884 | 8.1 |
| Rippled12 | SparseGrid | 1.040251 | 0.091531 | 7.9 |
| Rippled12 | FixedCentroid | 0.856528 | 0.098594 | 7.9 |

The centroid archive has better median loss and reference utility on these two fixtures, but does not improve mean
reference occupancy. This is a descriptive development result, not statistical confirmation, representative task
coverage, measured memory efficiency, a runtime speedup or competitor superiority. Keep the archive opt-in.

Given two archive layouts, when the matched runner compares their retained solutions, then both use the same
reference geometry and failed runs remain in the denominator. Given the same development smoke configuration,
when repeated locally, then all eight smoke-run records are byte-identical and independent costs reconcile.

The raw trace is generated under `TestResults/quality/`; it is not committed because compact evidence retains every
scheduled run and the full trace hash. Reproduce from the pinned source with the command in
[CENTROID_ARCHIVES.md](../CENTROID_ARCHIVES.md), then export with `eng/Export-NumericEvidence.ps1`. Outputs must be new
files. Freeze a representative suite, endpoint and sample-size plan before any confirmatory campaign; this pilot
does not choose a production default or close US-22's controlled memory/performance work.
