# Deploying evolved programs and models

`AiDotNet.Evolution.Deployment` promotes a search result into production only after a fresh
comparison with the incumbent. It then watches the promoted version and rolls it back when it
regresses. Nothing here starts a background worker or calls a paid provider on its own: the
application decides when to promote, retune and observe.

## Artifacts and where they apply

An `EvolutionDeployableArtifact` holds immutable deployable bytes: an evolved program
(`FromProgram`) or a trained model (`FromModel`). Its `EvolutionDeploymentArtifactKind` is `Program`
or `TrainedModel`. Every artifact carries an `EvolutionDeploymentEnvelope`, which records exactly
where it applies: the runtime, device, compiler, dataset, workload and validation-protocol hashes.
An artifact is never used outside its envelope. Creating one does not activate it.

`EvolutionDeploymentArtifactRegistry` stores artifacts and evidence in a private directory:

- `Stage` writes an artifact's bytes once, without promoting it.
- `Load` reads it back for an exact envelope, without running or activating it.
- `RetainEvidence` and `ReadEvidence` keep the raw validation evidence, verified by hash.
- `IsQuarantined` reports whether an artifact was rolled back.

The registry keeps a single deployment slot, updated by compare-and-swap.

## Deciding to promote

`EvolutionDeploymentPolicy` is the application's explicit authorisation, frozen when it is created:
the objective direction, how many paired validation samples to take, the minimum mean gain, the
largest acceptable p-value (a one-sided paired sign test), the largest acceptable P95 latency ratio,
and how many retunes are allowed over the lifetime. `AllowBestEffortPersistence` must be set: it
accepts flushed-file and atomic-rename storage, which does not guarantee survival of a power loss.

Validation is done by the application. `EvolutionDeploymentEvaluators.Program` and `.Model` bridge an
artifact to a correctness check and a measurement. Each observation is an
`EvolutionDeploymentMeasurement`: whether correctness passed, the quality, the elapsed time, the cost
in your own units, and whether it is a fresh measurement rather than reused search evidence.

## The lifecycle

`EvolutionDeploymentLifecycle` ties this together.

- `PromoteAsync(candidate)` runs the fixed, fresh paired comparison and switches the slot only when
  the policy's thresholds are met. It returns an `EvolutionDeploymentDecision`: the outcome, whether
  it activated, and the artifact and evidence ids.
- `Select(observedEnvelope)` is called at your dispatch boundary. It returns an
  `EvolutionDeploymentSelection`: the artifact to run and a revision token. It uses the
  known-valid fallback you supplied for that envelope when nothing promoted applies or storage is
  faulted, and `IsFallback` says so.
- `ObserveAsync(selection, ...)` attributes a window of production measurements to the exact revision
  that served them, and quarantines the artifact after the configured number of consecutive
  regressions.
- `RetunePendingAsync` runs one requested retune through a bounded search driver. The driver receives
  an `EvolutionDeploymentRetuneRequest` carrying the envelope and hard limits on evaluations,
  proposals and time. `EvolutionDeploymentRetuners.Program` and `.AutoML` are drivers that enforce
  those limits.

Operations never overlap. A second call while one is running returns a `Busy` decision instead of
waiting.

## MAP-Elites AutoML

`MapElitesAutoML<T, TInput, TOutput>` runs AutoML model search as a quality-diversity search.
`MapElitesAutoMLOptions` sets the seed, initial population, complexity bins, archive capacity,
mutation and exploration probabilities, inspirations, and islands. Each elite it keeps is a
`MapElitesAutoMLArchiveEntry`: the model type, its parameters, its score, its descriptors and its
archive cell.
