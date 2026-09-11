# Optional, measured-only surrogate selection

`EvolutionSurrogateSelector<TGenome>` generates and charges a bounded proposal pool, optionally fits/scores it,
and returns a candidate for **true evaluation**. Predictions use a separate type and never authorize archive
insertion, correctness acceptance or deployment. This opt-in contract has no model-library dependency.

## Contract

- Supply at most 256 measured search observations with matching candidate/evaluation identity, task/evaluator
  versions and direction. Failures, infeasible records, cache hits, producer-declared reuse, zero-attempt records and duplicate measured
  identities are rejected. Declared original sample IDs must also be disjoint across records, even when records
  carry different evaluation IDs or scope keys: sample IDs are globally stable observation identities. Changing
  the label to Measured cannot manufacture independence. Original measurement metadata participates in the
  training/operation fingerprint. Selector v3 rejects the older semantic identity rather than preserving inflated
  sample credit. Unreported sample identities cannot be cross-checked; producers must report provenance truthfully.
  Record validation cannot prove evaluator correctness or enforce hidden-data isolation.
- Supply a proposal factory returning 1–64 unique owned canonical genomes and actual cost for **all** generated
  candidates, including rejected/unselected work. Maxima are reserved before proposal generation, fitting and inference.
- Implement `IEvolutionSurrogateTrainer<TGenome>` and a detached `IEvolutionSurrogateModel<TGenome>`. Return a
  same-unit receipt on success or known failure. Unknown exceptions charge the reserved maximum. A backend must
  enforce its time/resource bounds externally; a ledger is not process isolation or physical resource enforcement.
- Models report predicted mean, a nonnegative quality-scale uncertainty estimate, fitted version and domain support
  for every pool member. The backend must define and validate its reliability/domain policy; the interface alone
  does not establish calibration. Uncertainty is not automatically a confidence interval.
- Acquisition maximizes `mean + optimism × uncertainty` for maximization, or `−mean + optimism × uncertainty` for
  minimization. Exact ties select the first pool member. Nonfinite acquisition values invalidate the ranking.
- Explicit exploration probability is at least 5% (default 20%). Exploration, insufficient observations, unreliable
  models, any unfamiliar candidate, failed/incomplete predictions or denied optional stages use uniform pool selection.
  This is a probabilistic allocation, not a guaranteed finite-run quota. Exploration skips model work entirely.
- Proposal failures propagate because no valid fallback pool exists. Cancellation and declared maximum violations
  propagate. Ordinary model failures return a fallback **after** retaining actual or unknown charges. A backend's
  nested budget exception after dispatch is not mislabeled as a free preflight denial.

`EvolutionSurrogateSelection<TGenome>` records the selection reason, complete valid prediction set, fitted-model
identity, ordered training-data identity, exploration allocation and ledger operation identity. All generated pool
members are charged even when no predictions are requested. The caller must meter the selected candidate's actual
evaluation too, and must not double-charge a model call through both this selector and a nested receipt owner.
Backends may implement `IEvolutionSurrogateDiagnosticModel`; its detached, finite, bounded validation report is
retained on both acquisition and unreliable-model fallback. It does not turn empirical diagnostics into confirmation.

## State and integration

Fitting receives only the explicit observations supplied for that decision. Backends must construct fresh,
deterministic detached models without hidden cross-call learning. Serialize calls sharing a trainer unless its
backend explicitly supports concurrent fitting. Caller-owned observation windows, proposal generator state,
randomness and the independent resource ledger require their own compatible checkpoint/persistence arrangement.
There is no automatic checkpointed training-data collector in this API.

Do not train a search-affecting model from a diagnostic engine observer: observer state is outside the deterministic
checkpoint contract. Use explicit inputs or a checkpointable outcome-aware operator. Repeating an already charged
operation with identical semantic inputs is rejected before new proposal work; changing inputs requests new work,
not permission to reuse the old identity as a cache or retry receipt. Keep sealed confirmation data outside training.

## Optional numeric package

`AiDotNet.Evolution.Surrogates` supplies `ValidatedNearestNeighborTrainer` for typed numeric genomes, without adding
a dependency from the generic core to any model package. It targets .NET 10, .NET 8 and .NET Framework 4.7.1.
See the [package README](../src/AiDotNet.Evolution.Surrogates/README.md) and executable example for integration.

The trainer accepts 2–256 fresh observations and at most 128 encoded features. Repeated measurements of the same
genome stay in one group and contribute an equally weighted record-quality mean. With at least 18 distinct genomes,
a deterministic genome-hash split creates disjoint interpolation, residual-calibration and validation groups
(minimum eight/five/five). Validation labels never fit the residual radius. Excess relative validation MAE,
insufficient empirical radius coverage or too few groups mark the model unreliable. Candidate support uses nearest
normalized RMS distance. All model settings, prices, original-sample provenance and observations affect identity.

These are **empirical search-data diagnostics**, not a conformal coverage guarantee or sealed deployment holdout.
Replicate-group means are the validation targets, not coverage for an independent noisy draw. Adaptive collection,
small holdouts, nonstationarity and mismatched feature geometry can invalidate generalization. Keep the backend
opt-in, report fallback reasons, collect true measurements through explicit exploration, and confirm deployment
winners separately. There is no automatic observer-based learning, persistent collector, or model checkpoint blob.

Fitting charges a declared fixed bookkeeping tariff plus each encoded feature and each distance coordinate used
in both residual calibration and validation. Inference charges encoding and all training distances. Use
`MaximumTrainingCost(windowBound)` and `MaximumInferenceCost(poolBound, windowBound)` for conservative reservations.
These user-supplied work prices cover declared operations, not measured wall time or dollar expenses; representative
hardware/workload pricing is required before an economic claim. Models rejected as unreliable still incur fit cost.

The package job validates both archives and runs a **PackageReference-only** consumer with a new local feed/cache,
verifying restored package hashes before fitting and prediction. It does not use a published same-version core as
a substitute. The compatible core and adapter require coordinated versioning/release; this PR publishes neither.

## Runnable comparison

The [example adapter](../examples/SurrogateSearch/NearestNeighborSurrogate.cs) implements three-neighbor interpolation
over typed features, leave-one-out error screening and a distance support guard. Its residual-plus-distance
uncertainty is explicitly heuristic, not calibrated probabilistic coverage. Fitted arrays detach from caller data;
the model identity includes the observed genomes/qualities. It is intentionally in the example, not the generic core.

```powershell
powershell -ExecutionPolicy Bypass -File eng/Test-SurrogateSearch.ps1
dotnet run --project examples/SurrogateSearch -c Release -- --cost-ratios 10 64 TestResults/surrogate-new.json
powershell -ExecutionPolicy Bypass -File eng/Export-SurrogateEvidence.ps1 -InputPath TestResults/surrogate-new.json -OutputPath TestResults/surrogate-summary-new.json -RawGzipPath TestResults/surrogate-raw-new.json.gz
```

The example compares ordinary single proposals, uniform four-proposal pools, the historical heuristic backend and
the reusable validation-guarded backend's four-proposal acquisition
on two synthetic objectives with paired initialization and identical total **synthetic work-unit caps**. It charges
setup, all attempted proposals, model training, inference and true evaluations separately. These prices are declared
demonstration assumptions, not measured runtime or realistic universal model/evaluator cost ratios. `--cost-ratios`
uses evaluator tariffs 0.1, 1 and 10 with fixed proposal/model tariffs. Within each scenario every method has the
same total cap (`base cap × evaluator tariff`); comparisons across tariffs are sensitivity checks, not paired equal
absolute caps. The archive
receives only true measurements, and each run retains decisions, predictions, raw measurements and resource receipts.
The smoke checks replay/accounting/backend behavior, not a requirement that the surrogate win development fixtures.

Representative expensive/noisy tasks, externally validated coverage, matched tuning and release/default-promotion
evidence remain necessary before broader claims. The first optional backend and synthetic cost-ratio pilot do not
demonstrate superiority over OpenEvolve or establish that surrogate selection is universally beneficial.
