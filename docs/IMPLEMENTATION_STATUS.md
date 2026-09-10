# Competitive roadmap implementation

Updated September 10, 2026. The [26-story roadmap](COMPETITIVE_ANALYSIS_AND_ROADMAP.md) remains the target.
Implementation is continuing across the core and companion PRs. **The roadmap is not complete and no competitive win is claimed.**

## Implemented in the initial core PR

- `IOutcomeAwareVariationOperator<TGenome>` receives terminal committed outcomes, including proposal failures,
  duplicate rejections, cache hits and retried evaluations. Seeds and migration are not operator proposals.
- `AdaptiveVariationPortfolio<TGenome>` attributes outcomes to child operators, maintains bounded archive-success
  rewards divided by evaluator cost, explores with epsilon-greedy selection, exposes statistics and checkpoints
  both its learning and stateful children. This reward is not marginal scalar gain or total LLM cost.
- The numeric development harness compares six methods on four objectives with identical initial populations,
  matched evaluator-call caps, complete failure accounting and machine-readable best-so-far traces.
- CI checks harness accounting and exact same-platform replay. It does not require the new method to win these
  development fixtures, which would encourage tuning the fixtures into a misleading performance gate.
- The [resource extension](RESOURCE_ACCOUNTING.md) adds multi-resource reservations, exactly-once actual receipts,
  conservative unknown-cost handling, bounded history and explicit restore. An evaluator adapter meters every retry
  and cascade stage independently of refunded evaluation-attempt counters. The numeric runner also meters proposal calls.
- [Typed search spaces](TYPED_SEARCH_SPACES.md) provide real, integer, logarithmic, categorical and conditional domains,
  canonical owned genomes, feature encoding, mutation/crossover/restart and budgeted local refinement. An opt-in diagonal
  CMA-style emitter checkpoints covariance, step-size and pending cohorts; stale cohorts cannot overwrite newer learning.
  A runnable mixed-parameter example checks validity, incumbent preservation, accounting and exact replay in CI.

Given a failed or repeated proposal, when its outcome commits, then the selected operator receives exactly one
terminal notification and a cache hit cannot earn free success reward.

Given a completed checkpoint boundary, when an adaptive run resumes with the same semantic configuration,
then its learned state and trajectory match the uninterrupted run.

Given the same task and seed, when benchmark methods run, then they start from identical genomes and account
for every evaluator call, including initialization; failed or incomplete runs remain visible and fail the harness.

## Companion work and integration boundaries

[AiDotNet companion PR #2148](https://github.com/ooples/AiDotNet/pull/2148): an explicit correctness-before-fitness gate configured through
`AiModelBuilder.ConfigureProgramCorrectness`, with the evaluator kept internal, and preservation of metrics/artifacts during
program descriptor merging. The latter changes future repair feedback, so the program-task semantic version is bumped.
The existing [package migration PR #2092](https://github.com/ooples/AiDotNet/pull/2092) is independent and must be
reconciled before treating the new core portfolio as available through AiDotNet's facade. No copied engine changes
are introduced by this companion.

[AiDotNet.Tensors companion PR #1024](https://github.com/ooples/AiDotNet.Tensors/pull/1024): compare-and-deactivate of an observed kernel deployment, enabling built-in fallback
without allowing stale runtime evidence to remove a newer snapshot. This adds to the validated promotion machinery
already present on Tensors `main` at `67ceb6ed`; it is not a new promotion system. Deactivation is in-memory only,
not a persistent quarantine, cancellation of in-flight work, or automatic performance-drift detector.

The existing [ask/tell PR #14](https://github.com/ooples/AiDotNet.Evolution/pull/14) is not merged or duplicated here.
Durable evaluation identities and leases must be developed against its eventual agreed API.

## Story-by-story remaining work

“Partial” means at least one useful slice exists; it does not close the story's acceptance criteria.

| Story | State | Work still required |
| --- | --- | --- |
| US-01 representative suites | Partial | Real program/kernel/AutoML tasks, AlgoTune subset, sealed task partitions. |
| US-02 fair baselines | Partial | Matched OpenEvolve and other external adapters; stronger numeric baselines; consistent model access. |
| US-03 correctness gates | Partial, companion | Trusted sandbox/reference integrations and held-out validation; wrapper alone is not proof of correctness. |
| US-04 statistical evidence | Partial | Nested task/run uncertainty, effect sizes, preregistered sample plans and comparison reports. |
| US-05 resource ledger | Partial | Generic ledger, stage helper and evaluator/cascade adapter implemented; consumer model/compiler/setup integrations and deterministic concurrent admission remain. |
| US-06 noisy evaluation | Not implemented | Independent replicates, uncertainty, resampling and cascade rejection audit. |
| US-07 ablations | Partial | Same-operator uniform/adaptive allocation is available; representative island, migration, novelty and dispatch ablations remain. |
| US-08 adaptive operators | Partial | Marginal-gain reward options, end-to-end cost credit, realistic benchmark validation. |
| US-09 Pareto pipeline | Not implemented | Feasibility, objective definitions, archive/snapshot/selection/stopping/migration semantics together. |
| US-10 engine performance | Not implemented | BenchmarkDotNet throughput/allocation/scaling suite and regression thresholds. |
| US-11 application examples | Not implemented | End-to-end program, AutoML, kernel and external-session examples. |
| US-12 quality release gates | Partial | Representative measured quality thresholds, confidence and release artifacts. |
| US-13 typed search spaces | Implemented | Mixed/conditional domains, owned canonical genomes, operators/refinement, checkpointed diagonal CMA and pinned simpler-baseline comparison; no universal-quality claim. |
| US-14 surrogate assistance | Not implemented | Uncertainty-aware ranking, exploration and evaluated-only archive admission. |
| US-15 multi-fidelity | Not implemented | Fidelity/replicate identities, promotions, resource accounting and resumable scheduler. |
| US-16 adaptive islands | Not implemented | Resource allocation, heterogeneous policies and checkpointed restarts. |
| US-17 compiler-guided edits | Not implemented | Syntax-aware C# edits, bounded repair, isolation and multi-file evolution. |
| US-18 reusable experience | Not implemented | Provenance-backed retrieval, lessons and calibrated semantic novelty. |
| US-19 model/prompt routing | Not implemented | Consumer routing policy, end-to-end costs, replay and fixed-routing comparisons. |
| US-20 proposal concurrency | Not implemented | Immutable proposal contexts, bounded scheduling and deterministic policy. |
| US-21 durable external work | Pending API integration | Run/evaluation/attempt identity, leases, heartbeats, stale results and pending-work persistence. |
| US-22 centroid archive | Not implemented | Fixed-K archive, centroid identity, routing and deterministic restore. |
| US-23 warm starts | Not implemented | Applicability-keyed repertoire/evaluation reuse, noisy-sample freshness and fair warm/cold reporting. |
| US-24 CLI/dashboard | Partial | Numeric CLI only; general configuration, preflight, run lifecycle and local inspection remain. |
| US-25 promotion/retuning | Partial, companion | Persistent quarantine, retuning/drift policy and program/AutoML deployment registry. |
| US-26 policy meta-evolution | Not implemented | Opt-in declarative policy search, held-out outer loop and complete inner/outer cost accounting. |

## Validation evidence

- Core after typed search spaces and checkpoint hardening: 378 tests pass on each of net10.0, net8.0 and net471,
  including 26 outcome/portfolio, 33 resource-accounting and 39 typed-space/emitter tests.
- Core coverage after typed search spaces: 90.40% line / 75.39% branch; existing ratchet passes (88.80% / 73.51% minimum).
  These are not improvements over the stored baseline. Do not lower the baseline to accommodate the feature.
- Harness: six-method smoke passes 48 runs / 1,536 calls per replay. Repeated JSON is byte-identical, paired starting populations
  match, costs reconcile, and best-so-far curves are monotonic. This is an accounting/replay smoke test, not a
  statistically powered quality comparison.
- Tensors: 22 targeted autotuning tests pass on net10.0, including three new deployment deactivation tests.
- AiDotNet companion: 1,028 tests pass on net10.0 (1,006 UnitTests.Evolution tests and 22 facade integration tests),
  including 15 new gate/metadata tests and three new facade checks.
  The same selection passes under coverage: the correctness decorator and new configuration method have 100% line/branch
  coverage; `ProgramEvolutionTask` has 97.56% line / 100% branch coverage. These scoped figures are not whole-repository coverage.
- [Development pilot](benchmarks/NUMERIC_PILOT.md): 200 runs / 51,200 calls, no failures. Adaptation beats the
  same-operator uniform control on median loss here, but hill climbing beats both on every task's median loss.
  Adaptation remains opt-in; this is not representative or statistically confirmed superiority.
- [Diagonal CMA comparison](benchmarks/DIAGONAL_CMA_PILOT.md): 240 runs / 61,440 calls, no failures. The emitter's
  median final loss beats hill climbing on three development fixtures and loses on the rippled fixture. All six
  methods share the complete initial population and evaluator cap; proposal/evaluator ledger totals are retained.
- Hosted checks remain unvalidated: Evolution/AiDotNet checks are queued, and the Tensors runs were cancelled.
  AiDotNet's automated review requested facade integration; the follow-up hides the
  implementation and adds builder-level tests. Current-head approval is still pending. All PRs remain drafts;
  no merge-readiness claim.

## Experiment access policy

Use local compute and the user's existing subscription-authenticated Codex access where appropriate. Do not silently
switch to API-key billing, purchase credits or rent compute. Subscription access still has usage limits. A fair model-driven
comparison requires the same model-access mechanism, settings, context and tool permissions for both frameworks; an
agentic Codex run must not be described as equivalent to a raw model-completion call without controlling those differences.
