# Surrogate example pilot

Pinned source `def8bc8bce7786c88cd923182f8dea265b84f068`, September 10, 2026. Sixty runs compare ordinary proposals,
uniform four-candidate pools and surrogate-ranked four-candidate pools on two four-dimensional development fixtures,
with ten paired seeds and a 128-unit cap per run. **7,380 true evaluations, 7,651.135264 total synthetic work units,
zero failed runs.** The remaining capacity is not invented consumption: admission reserves stage maxima and may
stop with insufficient capacity for another evaluation.

[Compact per-run evidence](surrogate-def8bc8.json) retains setup/proposal/training/inference/evaluation charges,
selection reasons, observed counts and provenance. Raw decisions/predictions/measurements are identified by SHA-256
`c8af38d24c0c38f24945180974612528d2e39fe8316b036c75985e64adb6edc2`;
the complete 19.2 MB campaign replays byte-identically. The [example and backend](../../examples/SurrogateSearch/Program.cs)
are separate from the generic engine and from production model libraries.

| Development fixture | Ordinary median loss | Uniform-pool median loss | Surrogate-pool median loss |
| --- | ---: | ---: | ---: |
| Shifted quadratic | 0.00172095 | 0.00197579 | 0.00112678 |
| Rippled quadratic | 0.156896 | 0.163459 | 0.209698 |

Lower is better. The surrogate improves the smooth fixture's median but worsens the rippled fixture's median.
It made 1,710 learned acquisitions across 20 runs; all archived winners come from true measurements. It did not
simply run extra free proposals: ordinary/uniform/surrogate methods respectively used 126/123/120 evaluations per
run under the same cap, with their different proposal and model charges retained.

This is descriptive **synthetic-priced development evidence**, not measured CPU costs, a matched wall-time result,
independent calibration, a confidence interval or superiority over SciPy/OpenEvolve. The KNN backend's leave-one-out
error/distance heuristics do not establish reliable acquisition on the rippled task. Keep the feature optional;
production backends, calibration checks, new expensive/noisy task families, cost-ratio sensitivity and independently
confirmed comparisons remain necessary before choosing defaults. Do not reuse the eight-dimensional external-pilot
analyzer on these different tasks/dimensions without an explicit new protocol and analysis specification.
