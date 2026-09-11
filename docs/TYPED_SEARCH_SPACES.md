# Typed parameter search

US-13 supplies real, integer, enumerated/categorical, logarithmic and conditional domains, canonical genomes,
sampling, a checkpoint codec, mutation, crossover, restart and budgeted local refinement. Custom genomes remain supported.

Run the complete API example from the repository root:

```powershell
dotnet run --project examples/TypedParameterSearch -c Release -- 64
```

It searches a synthetic mixed-parameter proxy and prints the original and best scores, valid genome, version hashes,
operator statistics and evaluator spending. It is not an AutoML, kernel-performance or competitor benchmark. A valid
no-improvement result remains a legitimate outcome.

```csharp
var space = new EvolutionSearchSpaceBuilder()
    .Add(EvolutionParameter.Categorical("optimizer", new[] { "adam", "sgd" }))
    .Add(EvolutionParameter.Logarithmic("learning_rate", 1e-5, 0.1))
    .Add(EvolutionParameter.Real("momentum", 0, 0.99)
        .When("optimizer", EvolutionParameterValue.Categorical("sgd")))
    .Add(EvolutionParameter.Integer("batch_size", 8, 256))
    .Build();
var seed = space.Sample(StableRandom.CreateStream(42, 0));
var mutation = new SearchSpaceMutation(space);
var crossover = new SearchSpaceCrossover(space); // configure inspirations on the engine
var restart = new SearchSpaceRestart(space);
```

For a ready-made operator choice, use `EvolutionSearchPresets.Create(space)`. The default is mutation only;
`UniformMixed` and `AdaptiveMixed` explicitly select the same mutation/crossover/restart catalog, while
`DiagonalCma` is an opt-in continuous-domain emitter. The uniform preset tries each operator once before uniform
allocation; the adaptive preset uses archive-success credit and 0.1 exploration. Configure `InspirationCount`
on the engine for crossover. Each factory call owns fresh state, and checkpoints validate the resulting operator
versions. Unsupported CMA domains fail immediately. `Create` returns `IVariationOperator<EvolutionSearchGenome>`;
`CreateMutation`, `CreateUniformMixed`, `CreateAdaptiveMixed` and `CreateDiagonalCma` return `SearchSpaceMutation`,
`AdaptiveVariationPortfolio<EvolutionSearchGenome>` (both mixed presets) and `DiagonalCmaEmitter`, so concrete members
such as portfolio statistics need no downcast. Only `DiagonalCma` uses a scalar `direction`; supplying one to any other
preset is rejected rather than silently ignored. Use direct constructors for custom settings and
`SearchSpaceLocalRefiner` for explicit budgeted refinement; the catalog never invents an objective or an unmetered
refinement budget. Mutation remains the default: the recorded numeric pilot compared operator policies directly rather
than through this preset catalog, so it is not evidence about the presets themselves.

`EvolutionSearchTask` accepts an objective delegate plus explicit task/evaluator versions. Pass `space` as the engine's
genome codec for checkpoints. Domain legality is separate from application correctness and hard constraints; retain the
independent evaluation gates required by your application.

## Given / When / Then

- Given mixed or conditional definitions, when the builder freezes them, then invalid domains, duplicate names, forward
  dependencies, invalid parent values and oversized configurations are refused. Conditional parents precede children;
  multiple conditions mean AND, with OR among each condition's accepted values.
- Given inactive values, when a genome is canonicalized, then those values disappear from identity. Unknown parameter
  names still fail validation. Signed numeric zero is normalized; identity uses IEEE-754 bit patterns, not locale-dependent
  display text. A changed domain/condition/encoding changes the schema fingerprint.
- Given standard operators, when candidates are generated, then conditional activation is recomputed in dependency order,
  newly active values are initialized from the supplied stream, and parents remain unchanged. Numeric mutation uses
  normalized coordinates; logarithmic parameters mutate in log space. Integer trials remain integral.
- Given local refinement, when trials are scored, then the initial measurement and every trial consume the shared resource
  budget; infeasible or unsuccessful trials cannot replace the best measured point. The final outer evaluation is still required.
- Given a model feature vector, when values are encoded, then explicit activity indicators distinguish inactive values from
  numeric minima, and categories are one-hot rather than assigned an artificial numeric order.
- Given a compatible checkpoint, when the engine restores, then typed genomes and adaptive operator state preserve the
  continuation. Malformed or differently versioned payloads are rejected.

Logarithmic coordinates use the ratio `maximum / minimum`, whose rounding is IEEE-deterministic, rather than a
difference of two logarithms that cancels when the bounds are close. Both endpoints are pinned, so a normalized
coordinate at or beyond 0 or 1 decodes to exactly the declared bound and a parent on a bound keeps its identity when
mutation pushes it outward. Intervals narrower than one part in 1e9 interpolate linearly, which differs from the
logarithmic map by under 1.3e-10 of the normalized span and keeps every representable value distinct; when the ratio
overflows to infinity the difference-of-logarithms form is used instead. Logarithmic domain fingerprints include
`log-domain-v3-ratio-pinned`; genomes and checkpoints from either earlier logarithmic schema are rejected, not
silently reinterpreted. Non-logarithmic schemas retain their prior fingerprints. Domain identity does not depend on
how a particular runtime rounds `Math.Log`.

Known limitation: there is no integer logarithmic kind. `Integer` is always sampled and mutated in linear
coordinates, so a wide integer range is not log-uniform. Declare a `Logarithmic` parameter and round it inside the
evaluator when a log-scaled integer is needed.

## Optional diagonal CMA-style learning

`DiagonalCmaEmitter` implements positive-weight ranked recombination, diagonal covariance adaptation and cumulative
step-size control, based on [Hansen's CMA tutorial](https://arxiv.org/abs/1604.00772). It supports one to 32 nonconstant,
unconditional real/logarithmic parameters. It does not model cross-coordinate covariance and is not full CMA-ME.

The bounded-domain variant clips proposals and learns from their evaluated coordinates. Variances and step size have
finite safety bounds. Only fresh, completed, feasible measurements with the configured direction train it. Full learning
populations require enough valid parents; failed, cached, producer-declared reused or infeasible outcomes never
become successful parents. Measurement-origin-aware CMA versions reject older learned checkpoints; see
[measurement origin](MEASUREMENT_ORIGIN.md#learning-from-measurements).

Proposal generation can span evaluation batches. Each population retains its sampling distribution. A stale population's
results can still enter the archive, but cannot overwrite a newer distribution; `StalePopulations` exposes this tradeoff.
Batch size one avoids stale populations. Pending samples, distribution state and counters survive checkpoints.

The numeric development harness includes `DiagonalCma` alongside random search, hill climbing and fixed/adaptive
MAP-Elites controls, with matched initial populations and evaluator-call budgets. These are development comparisons,
not a claim that this emitter wins across workloads. It remains opt-in.
