# Optional numeric surrogate adapters

This package depends on AiDotNet.Evolution; the generic engine does not depend on this package or a model library.
The numeric adapter is experimental and opt-in. Its reliability checks use disjoint genome groups within supplied
search measurements, not sealed deployment confirmation. Reported empirical residual coverage is not a universal
probabilistic calibration guarantee, especially for adaptively collected or shifted data. Work-unit tariffs are
explicit user assumptions, not measurements of elapsed CPU or monetary expense. The compatible core and adapter
must be built/released together; this source PR does not publish a package.

## Integrating the backend

Construct a `ValidatedNearestNeighborTrainer(space, new NumericSurrogateOptions(qualityMinimum, qualityMaximum))`
for the declared typed space and finite quality support. Pass it to `EvolutionSurrogateSelector<EvolutionSearchGenome>`
with an independent `EvolutionResourceLedger`, proposal-pool maximum, `trainer.MaximumTrainingCost(windowBound)`
and `trainer.MaximumInferenceCost(poolBound, windowBound)`. Supply fresh `EvolutionSurrogateObservation` records,
a stable random stream and a charged proposal factory to each `SelectAsync` call. Then meter and truly evaluate
the selected candidate before archive insertion. The repository's `examples/SurrogateSearch` is a complete runnable
integration; `eng/SurrogatePackageSmoke` verifies the packaged APIs without project references.

The trainer is stateless and returns detached models. Callers own observation persistence, random/proposal state,
task/evaluator versioning, failure handling and independent confirmation. Do not train from diagnostic observers
whose state is outside engine checkpoints. Direct `FitAsync`/`PredictAsync` calls report actual declared work but do
not reserve a budget themselves; use the selector or an equivalent receipt owner. Do not double-charge nested work.

## Reliability and limits

- 2–256 fresh records, at most 128 normalized features, 1–64 unique prediction candidates, and 1–16 neighbors.
- Repeated measurements of one genome stay together; original sample IDs must not overlap between records.
- At least 18 distinct genomes are required for reliable ranking. A deterministic genome-hash split holds out
  separate residual-calibration and validation groups; validation cannot tune the residual radius.
- Reliability requires bounded relative validation MAE and adequate empirical residual coverage. Support also
  requires nearest-neighbor distance within the configured normalized RMS threshold.
- `ValidationReport` and partition IDs expose the evidence. A zero radius on a constant fixture is not a general
  zero-error guarantee. Coverage concerns replicate-group means, not independent single noisy measurements.
- The selector records predictions separately from fitness and falls back to uniform pool selection on unreliable,
  insufficient or unfamiliar data. Explicit random exploration remains active even when ranking is accepted.

The default work tariff is 0.02 per fit, 0.005 per inference batch and 0.000001 per encoded feature/distance
coordinate. Fitting includes interpolation distances for both calibration and validation. The fixed tariffs cover
bookkeeping by assumption; sorting, allocation, elapsed CPU and energy are not separately measured. Change prices
for your workload and retain them in evidence. All options and provenance contribute to fitted-model identity.

## Package boundary

Targets: `net10.0`, `net8.0`, `net471`. Its only runtime package dependency is the compatible `AiDotNet.Evolution`
core. The core does not depend on this optional adapter. The CI consumer uses fresh isolated packages and verifies
exact hashes because published preview packages can have the same version but lack these unreleased APIs.
Release automation for the core does not implicitly publish this adapter; coordinate compatible versions before
shipping. No package publication, automatic deployment or default enablement is performed by these changes.
