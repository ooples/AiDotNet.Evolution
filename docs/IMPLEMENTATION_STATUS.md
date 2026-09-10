# Competitive roadmap implementation

Updated September 10, 2026. The [26-story roadmap](COMPETITIVE_ANALYSIS_AND_ROADMAP.md) remains the target.
This first PR is a reviewable foundation, **not completion of the roadmap and not a competitive-win claim**.

## Implemented in the initial core PR

- `IOutcomeAwareVariationOperator<TGenome>` receives terminal committed outcomes, including proposal failures,
  duplicate rejections, cache hits and retried evaluations. Seeds and migration are not operator proposals.
- `AdaptiveVariationPortfolio<TGenome>` attributes outcomes to child operators, maintains bounded archive-success
  rewards divided by evaluator cost, explores with epsilon-greedy selection, exposes statistics and checkpoints
  both its learning and stateful children. This reward is not marginal scalar gain or total LLM cost.
- The numeric development harness compares four methods on four objectives with identical initial populations,
  matched evaluator-call caps, complete failure accounting and machine-readable best-so-far traces.
- CI checks harness accounting and exact same-platform replay. It does not require the new method to win these
  development fixtures, which would encourage tuning the fixtures into a misleading performance gate.

Given a failed or repeated proposal, when its outcome commits, then the selected operator receives exactly one
terminal notification and a cache hit cannot earn free success reward.

Given a completed checkpoint boundary, when an adaptive run resumes with the same semantic configuration,
then its learned state and trajectory match the uninterrupted run.

Given the same task and seed, when benchmark methods run, then they start from identical genomes and account
for every evaluator call, including initialization; failed or incomplete runs remain visible and fail the harness.

## Companion work and integration boundaries

AiDotNet companion: an explicit correctness-before-fitness evaluator and preservation of metrics/artifacts during
program descriptor merging. The latter changes future repair feedback, so the program-task semantic version is bumped.
The existing [package migration PR #2092](https://github.com/ooples/AiDotNet/pull/2092) is independent and must be
reconciled before treating the new core portfolio as available through AiDotNet's facade. No copied engine changes
are introduced by this companion.

AiDotNet.Tensors companion: compare-and-deactivate of an observed kernel deployment, enabling built-in fallback
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
| US-05 resource ledger | Not implemented | End-to-end model/refiner/surrogate/retry costs, reservations and hard budget enforcement. |
| US-06 noisy evaluation | Not implemented | Independent replicates, uncertainty, resampling and cascade rejection audit. |
| US-07 ablations | Partial | Same-operator uniform versus adaptive allocation; island, migration, novelty and dispatch ablations. |
| US-08 adaptive operators | Partial | Marginal-gain reward options, end-to-end cost credit, realistic benchmark validation. |
| US-09 Pareto pipeline | Not implemented | Feasibility, objective definitions, archive/snapshot/selection/stopping/migration semantics together. |
| US-10 engine performance | Not implemented | BenchmarkDotNet throughput/allocation/scaling suite and regression thresholds. |
| US-11 application examples | Not implemented | End-to-end program, AutoML, kernel and external-session examples. |
| US-12 quality release gates | Partial | Representative measured quality thresholds, confidence and release artifacts. |
| US-13 typed search spaces | Not implemented | Mixed/conditional parameters, normalization, reusable variation and refinement. |
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

- Core: 306 tests pass on each of net10.0, net8.0 and net471, including 26 new outcome/portfolio tests.
- Core coverage: 89.13% line / 73.85% branch; existing ratchet passes (88.80% / 73.51% minimum).
  These are not improvements over the stored baseline. Do not lower the baseline to accommodate the feature.
- Harness: 32 runs, 1,024 evaluator calls per replay; repeated JSON is byte-identical, paired starting populations
  match, costs reconcile, and best-so-far curves are monotonic. This is an accounting/replay smoke test, not a
  statistically powered quality comparison.
- Tensors: 22 targeted autotuning tests pass on net10.0, including three new deployment deactivation tests.
- AiDotNet companion and hosted CI: validation still in progress; do not mark ready based on the core tests.

## Experiment access policy

Use local compute and the user's existing subscription-authenticated Codex access where appropriate. Do not silently
switch to API-key billing, purchase credits or rent compute. Subscription access still has usage limits. A fair model-driven
comparison requires the same model-access mechanism, settings, context and tool permissions for both frameworks; an
agentic Codex run must not be described as equivalent to a raw model-completion call without controlling those differences.
