# Competitive roadmap implementation

Updated September 10, 2026. The [26-story roadmap](COMPETITIVE_ANALYSIS_AND_ROADMAP.md) remains the target.
Implementation is continuing across the core and companion PRs. **The roadmap is not complete and no competitive win is claimed.**

## Implemented in the initial core PR

- `IOutcomeAwareVariationOperator<TGenome>` receives terminal committed outcomes, including proposal failures,
  duplicate rejections, cache hits and retried evaluations. Seeds and migration are not operator proposals.
- `AdaptiveVariationPortfolio<TGenome>` attributes outcomes to child operators, maintains bounded archive-success
  rewards divided by evaluator cost, explores with epsilon-greedy selection, exposes statistics and checkpoints
  both its learning and stateful children. This reward is not marginal scalar gain or total LLM cost.
- The opt-in [operator-credit extension](OPERATOR_CREDIT.md) adds proposal-time parent improvement, explicit gain/cost
  scales, proposal-plus-evaluator receipts, backend checkpoints and typed terminal child/configuration attribution.
  Uninstrumented consumer stages are still not automatically included in credit or accounting.
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
| US-02 fair baselines | Partial | Matched-population SciPy differential evolution now uses the shared C# evaluator and independent counters; native-default/tuned controls, OpenEvolve and consistent model access remain. |
| US-03 correctness gates | Partial, companion | Trusted sandbox/reference integrations and held-out validation; wrapper alone is not proof of correctness. |
| US-04 statistical evidence | Partial | Paired task/run analysis, failure-inclusive effects/intervals and trace checks implemented; prospective sample-size/power design and representative confirmation remain. |
| US-05 resource ledger | Partial | Generic ledger, stage helper and evaluator/cascade adapter implemented; consumer model/compiler/setup integrations and deterministic concurrent admission remain. |
| US-06 noisy evaluation | Partial | Fresh bounded replicate runner, per-sample costs, finite-look uncertainty and separate confirmation identities implemented; archive resampling policy, cascade-rejection audit and representative noisy comparisons remain. |
| US-07 ablations | Partial | Same-operator uniform/adaptive allocation is available; representative island, migration, novelty and dispatch ablations remain. |
| US-08 adaptive operators | Partial | Parent-improvement/archive-success policies, proposal-plus-evaluator credit and typed attribution implemented; full consumer-stage integration and representative held-out comparisons before default promotion remain. |
| US-09 Pareto pipeline | Not implemented | Feasibility, objective definitions, archive/snapshot/selection/stopping/migration semantics together. |
| US-10 engine performance | Partial | BenchmarkDotNet engine/archive/checkpoint suite and fixture validation implemented; controlled repeated baselines, peak memory, dimension/island scaling and regression thresholds remain. |
| US-11 application examples | Not implemented | End-to-end program, AutoML, kernel and external-session examples. |
| US-12 quality release gates | Partial | Representative measured quality thresholds, confidence and release artifacts. |
| US-13 typed search spaces | Implemented | Mixed/conditional domains, owned canonical genomes, operators/refinement, checkpointed diagonal CMA and pinned simpler-baseline comparison; no universal-quality claim. |
| US-14 surrogate assistance | Partial | Cost-metered acquisition, exploration/fallback contracts and a numeric KNN example implemented; production calibration/backends, representative expensive/noisy evidence and durable observation integration remain. |
| US-15 multi-fidelity | Partial | Bounded successive-halving bracket, exploration, fidelity/replicate identities, incremental state handoff and fresh full confirmation implemented; real learning-workload integration, durable bracket resume and representative comparisons remain. |
| US-16 adaptive islands | Not implemented | Resource allocation, heterogeneous policies and checkpointed restarts. |
| US-17 compiler-guided edits | Partial, companion | Exact source identity and preserved feedback implemented; syntax-aware C# edits, bounded repair, isolation, dependency fingerprints and multi-file evolution remain. |
| US-18 reusable experience | Not implemented | Provenance-backed retrieval, lessons and calibrated semantic novelty. |
| US-19 model/prompt routing | Not implemented | Consumer routing policy, end-to-end costs, replay and fixed-routing comparisons. |
| US-20 proposal concurrency | Not implemented | Immutable proposal contexts, bounded scheduling and deterministic policy. |
| US-21 durable external work | Pending API integration | Run/evaluation/attempt identity, leases, heartbeats, stale results and pending-work persistence. |
| US-22 centroid archive | Partial | Fixed-K routing, immutable geometry, transactional offline projection and engine/checkpoint coverage implemented; matched common-reference runner available; controlled memory/latency and representative quality confirmation remain. |
| US-23 warm starts | Not implemented | Applicability-keyed repertoire/evaluation reuse, noisy-sample freshness and fair warm/cold reporting. |
| US-24 CLI/dashboard | Partial | Numeric CLI only; general configuration, preflight, run lifecycle and local inspection remain. |
| US-25 promotion/retuning | Partial, companion | Persistent quarantine, retuning/drift policy and program/AutoML deployment registry. |
| US-26 policy meta-evolution | Not implemented | Opt-in declarative policy search, held-out outer loop and complete inner/outer cost accounting. |

## Validation evidence

- Explicit operator credit: 478 core tests pass on each of net10.0, net8.0 and net471, including 21 new gain/cost cases.
  Fresh modern coverage is 91.35% line / 76.77% branch; the unchanged ratchet passes. Tests cover pending parent/cost
  restoration, whole-engine boundary resume, unknown/missing receipts, cancellation, concurrent-use rejection, fixed-scale
  numeric extremes and lower-cost preference with retained exploration. The original constructor's binary signature and
  default checkpoint representation are preserved. The example passes 24 paired runs / six methods with exact replay,
  static controls, measured-only winners and all-stage synthetic cost reconciliation. No realistic-price or quality win is claimed.
  The [pinned credit pilot](benchmarks/OPERATOR_CREDIT_PILOT.md) retains 120 runs / 11,597 true evaluations / 15,300.20
  synthetic units with zero failures and exact replay. Static small-step has the lowest smooth-fixture median; adding
  proposal costs worsens both adaptive policies' rippled-fixture medians. These are useful controls, not a default-promotion result.
- Evaluator receipt hardening: six new cases bring the core suite to 457 passing tests on each target framework.
  Four regressions first failed: a positive cost rounding to zero, lost engine-visible unrepresentable cost, a nested
  budget exception mislabeled as preflight denial, and a declared maximum overrun remaining promotable. The adapter
  now fails closed, keeps actual/conservative attempt totals and preserves unknown-cost diagnostics through retries.
  Simulated fatal errors still propagate after settlement. Fresh modern coverage is 91.20% line / 76.77% branch.
  The adapter semantic version changes intentionally; old checkpoints are incompatible. See [resource accounting](RESOURCE_ACCOUNTING.md).
- Multi-fidelity scheduling: 451 core tests pass on each of net10.0, net8.0 and net471, including 13 promotion/state/cost
  contract cases. Fresh modern-framework coverage is 91.18% line / 76.74% branch; the unchanged ratchet passes.
  The eight-run example replays exactly and checks 32 measurements, 12 resumed calls, four fresh full-fidelity confirmation
  calls and 92.08 synthetic work units per run. Tests cover late improvers, incompatible tokens, incomplete/unknown work,
  confirmation reversing the search ranking and retention of never-dispatched starting candidates. This is one bounded
  synchronous bracket, not full Hyperband, persistent workers or a real learning-workload speedup claim.
  See [multi-fidelity scheduling](MULTI_FIDELITY.md) for statistical, state ownership and isolation boundaries.
- CodeQL follow-up: eight arithmetic/complexity findings are addressed in `3cb89477`; 438 tests still pass on all
  three frameworks and all 240 numeric run records remain unchanged. Fresh net10.0 coverage after the guard refactor
  is 90.86% line / 76.38% branch; the unchanged ratchet passes. Hosted rescan confirmation remains pending.
- Surrogate selection: 438 core tests pass on each of net10.0, net8.0 and net471, including 20 surrogate contract cases.
  Fresh net10.0 coverage is 90.89% line / 76.42% branch, above the unchanged ratchet minimum. Model failures retain
  actual/unknown charges; nested backend budget denials are not mislabeled as pre-dispatch denials. The numeric
  example validates interpolation, detached training data, fitted identities and weak-model/domain fallback.
  Its 12-run replay smoke verifies all-stage synthetic charges, measured-only winners and 155 actual acquisitions.
  The [pinned surrogate example pilot](benchmarks/SURROGATE_EXAMPLE_PILOT.md) retains 60 runs / 7,380 true evaluations /
  7,651.135264 synthetic work units, with zero failures and exact replay. Learned acquisition improves smooth-fixture
  median loss but worsens rippled-fixture median loss; it remains optional and is not a calibrated production backend.
  See [surrogate selection](SURROGATE_SELECTION.md) for explicit-input/checkpoint and heuristic-uncertainty boundaries.
- External baseline: eight integration tests cover all four shared objectives, exact initialization/replay, real SciPy
  calls, valid early convergence, errors and hard caps. The smoke completes eight external runs / 256 calls twice,
  with byte-identical merged 56-run evidence. The analyzer now passes 17 tests and preserves valid under-budget
  convergence without invented measurements or costs. This is a controlled eight-member population, not SciPy native
  defaults or a competitive win. See [external baseline protocol](../benchmarks/external/README.md).
  The [pinned external pilot](benchmarks/EXTERNAL_BASELINE_PILOT.md) completes 280 runs / 71,528 calls with no failures.
  SciPy's matched-population DE has lower median loss than diagonal CMA on all four fixtures; the paired utility
  difference interval crosses zero. All 240 core records reproduce the prior pilot exactly and the external campaign
  replays byte-identically. This exposes an optimization gap without establishing representative superiority.
- Fresh replication extension: 418 core tests pass on each of net10.0, net8.0 and net471, including 22 replication cases.
  Fresh net10.0 coverage is 90.77% line / 76.23% branch. The deterministic example verifies 64 fresh dispatches/charges,
  distinct search/confirmation identities and exact replay. Numerical regressions cover wide support, tiny variance,
  large offsets and subnormal interval bounds. See [replicated evaluation](REPLICATED_EVALUATION.md) for the required
  known bounds/IID assumptions and why batch confidence is not population-wide selection confidence.
- Centroid extension: 396 core tests pass on each of net10.0, net8.0 and net471, including 18 centroid contract cases.
  Fresh net10.0 coverage is 90.60% line / 75.97% branch, above the unchanged ratchet minimum.
  The matched archive smoke completes eight runs / 256 evaluations with identical replay; equal elite slots are not a
  claim of equal measured RAM. See [centroid archives](CENTROID_ARCHIVES.md) for geometry and projection boundaries.
  The [pinned archive pilot](benchmarks/ARCHIVE_PARTITION_PILOT.md) completes 40 runs / 10,240 calls: centroid median
  loss/reference utility improves on the two fixtures, while mean reference occupancy does not. No confirmation claim.
- Core after typed search spaces and checkpoint hardening: 378 tests pass on each of net10.0, net8.0 and net471,
  including 26 outcome/portfolio, 33 resource-accounting and 39 typed-space/emitter tests.
- Core coverage after typed search spaces: 90.40% line / 75.39% branch; existing ratchet passes (88.80% / 73.51% minimum).
  These are not improvements over the stored baseline. Do not lower the baseline to accommodate the feature.
- Harness: six-method smoke passes 48 runs / 1,536 calls per replay. Repeated JSON is byte-identical, paired starting populations
  match, costs reconcile, and best-so-far curves are monotonic. This is an accounting/replay smoke test, not a
  statistically powered quality comparison.
- Performance fixture smoke verifies 16 engine configurations, three occupied archive sizes and three real checkpoint
  sizes, including unchanged state on restore. BenchmarkDotNet Dry runs completed nine archive, 32 engine and nine checkpoint cases;
  these are execution checks under local development load, not timing baselines or speedup evidence.
- Tensors: 22 targeted autotuning tests pass on net10.0, including three new deployment deactivation tests.
- AiDotNet companion: 1,042 tests pass separately on net10.0 and net8.0 (1,020 UnitTests.Evolution tests and 22 facade integration tests),
  including 15 new gate/metadata tests, three facade checks, 12 source-identity/proposal/output tests and two malformed-source
  retry tests. Six identity regressions first failed against the old library. Earlier scoped coverage passed the 1,040-test selection: the correctness
  decorator, genome codec and new Unicode validation method have 100% line/branch coverage. The `ProgramGenome` class
  has 98.24% line / 91.66% branch coverage. These are not whole-repository coverage figures. Final local net10/net8
  builds disabled diagnostic analyzers and used unchanged dependency assemblies; they do not establish full analyzer
  or net471 compatibility. The earlier compiler exit -1 has no established root cause. Interface/checkpoint migration
  notes and bounded malformed-source retry handling address the latest review; CodeRabbit approved current head
  `75aa6b1d` on September 10. This is not Copilot review or hosted build completion.
- [Paired analysis](benchmarks/analysis-d62d5cb/report.md): 14 Python contract tests pass. Retrospective reporting retains every scheduled run, marks unknown
  work and incomplete curves, and resamples paired seeds within tasks. The pinned CMA-versus-hill-climbing adjusted
  utility-difference interval crosses zero; the pilot is not evidence for promoting CMA as a universal default.
- [Development pilot](benchmarks/NUMERIC_PILOT.md): 200 runs / 51,200 calls, no failures. Adaptation beats the
  same-operator uniform control on median loss here, but hill climbing beats both on every task's median loss.
  Adaptation remains opt-in; this is not representative or statistically confirmed superiority.
- [Diagonal CMA comparison](benchmarks/DIAGONAL_CMA_PILOT.md): 240 runs / 61,440 calls, no failures. The emitter's
  median final loss beats hill climbing on three development fixtures and loses on the rippled fixture. All six
  methods share the complete initial population and evaluator cap; proposal/evaluator ledger totals are retained.
- Hosted validation remains incomplete for Evolution/AiDotNet (build/security checks queued). Tensors current head
  `77d16869` now has successful build, AVX-512 verification and returned GPU-parity checks; earlier duplicate title
  runs were cancelled. AiDotNet has current-head CodeRabbit approval, not Copilot approval. All PRs remain drafts;
  no full-roadmap or merge-readiness claim.

## Experiment access policy

Use local compute and the user's existing subscription-authenticated Codex access where appropriate. Do not silently
switch to API-key billing, purchase credits or rent compute. Subscription access still has usage limits. A fair model-driven
comparison requires the same model-access mechanism, settings, context and tool permissions for both frameworks; an
agentic Codex run must not be described as equivalent to a raw model-completion call without controlling those differences.
