# Optional, measured-only surrogate selection

`EvolutionSurrogateSelector<TGenome>` generates and charges a bounded proposal pool, optionally fits/scores it,
and returns a candidate for **true evaluation**. Predictions use a separate type and never authorize archive
insertion, correctness acceptance or deployment. This opt-in contract has no model-library dependency.

## Contract

- Supply at most 256 measured search observations with matching candidate/evaluation identity, task/evaluator
  versions and direction. Failures, infeasible records, cache hits, producer-declared reuse, zero-attempt records and duplicate measured
  identities are rejected. Record validation cannot prove evaluator correctness or enforce hidden-data isolation.
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

## First numeric backend and runnable comparison

The [example adapter](../examples/SurrogateSearch/NearestNeighborSurrogate.cs) implements three-neighbor interpolation
over typed features, leave-one-out error screening and a distance support guard. Its residual-plus-distance
uncertainty is explicitly heuristic, not calibrated probabilistic coverage. Fitted arrays detach from caller data;
the model identity includes the observed genomes/qualities. It is intentionally in the example, not the generic core.

```powershell
powershell -ExecutionPolicy Bypass -File eng/Test-SurrogateSearch.ps1
dotnet run --project examples/SurrogateSearch -c Release -- 10 128
```

The example compares ordinary single proposals, uniform four-proposal pools and learned four-proposal acquisition
on two synthetic objectives with paired initialization and identical total **synthetic work-unit caps**. It charges
setup, all attempted proposals, model training, inference and true evaluations separately. These prices are declared
demonstration assumptions, not measured runtime or realistic universal model/evaluator cost ratios. The archive
receives only true measurements, and each run retains decisions, predictions, raw measurements and resource receipts.
The smoke checks replay/accounting/backend behavior, not a requirement that the surrogate win development fixtures.

Production-quality uncertainty backends, independently validated calibration, representative expensive/noisy tasks,
cost-ratio sensitivity, fair matched tuning, consumer facade/package integration and durable observation collection
remain open. Neither this example nor a good synthetic loss closes those acceptance requirements.
