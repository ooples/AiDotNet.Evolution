# Competitive roadmap implementation

Updated September 11, 2026. The [26-story roadmap](COMPETITIVE_ANALYSIS_AND_ROADMAP.md) remains the target.
Implementation is continuing across the core and companion PRs. **The roadmap is not complete and no competitive win is claimed.**

Track the full plan through the [26 user-story issues and PR delivery index](USER_STORY_DELIVERY.md).

## Dependent story delivery

The user chose to retain #15, AiDotNet #2148 and Tensors #1024 as shared foundations.
All 26 stories now have individual open issues and dependent PRs, with reciprocal links and original
Given/When/Then checklists. Initial story commits were documentation-only, not completed implementation.

[US-08 / PR #51](https://github.com/ooples/AiDotNet.Evolution/pull/51) adds the first subsequent story-owned
implementation slice: producer-declared reused measurements cannot earn fresh portfolio credit, enter
surrogate training as new evidence or train CMA as fresh population members. Credit retains origin separately
from current cost; semantic versions reject older learned checkpoints. This fix is on the dependent branch,
not yet in this shared foundation or consumer source/package pins. US-13, US-14 and US-23 require integration.
Full story acceptance and independent current-head review remain open.

## US-10 story-owned performance evidence

[PR #53](https://github.com/ooples/AiDotNet.Evolution/pull/53) adds fresh-process profiling and expanded BenchmarkDotNet factors.
At `4a7b50e`, all 132 attempts and eight worker-determinism groups pass; 47 profiling contracts and all 661 core tests
on each supported target pass locally. [Results](../benchmarks/evidence/performance/README.md) include memory,
checkpoint overhead, utilization and quality-over-time, plus the original failed campaign. This is engine overhead,
not an optimized-algorithm speedup. Host load/frequency/power are not controlled; release thresholds remain separate.

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
The companion now includes exact-source protected-edit enforcement across proposal formats and complete configured
proposal-option hashing (variation v5). The existing [package migration PR #2092](https://github.com/ooples/AiDotNet/pull/2092)
at `104a2a41` was integrated into the companion feature branch in `f317ed04`; neither that PR nor `master` was merged
or modified. This removes the copied engine and enables local validation against the standalone core. The published
`0.1.0-preview.1` NuGet artifact exists but identifies baseline `f0f282cf`, not the new roadmap APIs; a newer artifact
is required before claiming those APIs work through the normal package path.

Companion `fcb29b770` adds the opt-in `AiDotNet.Evolution.CSharp` package: bounded syntax-addressed edits,
real deterministic Roslyn emit, bounded compiler-feedback repair, owned/reference/assembly fingerprints,
write-once exact-source evidence, and shared model/setup/compiler/audit/evaluation resource accounting.
The facade also accepts caller-owned proposal loops. Adversarial regressions fixed canceled I/O undercounting,
fatal runner suppression, missing receipts being treated as known costs, lazy provenance opt-in and lost terminal
cost-bookkeeping cleanup. These are tested contracts, not live-model or runtime optimization results; compiler
cancellation is cooperative, not OS isolation, and sealed correctness/performance promotion remains external.

Companion `d0c4039ac` adds a real C# execution worker without a scripting-tool dependency and tests the full
facade/compiler/correctness/fitness/shared-ledger path using authored sources and scripted proposals. Compile-only
does not load the emitted assembly. Adversarial tests also fixed truncated-prefix false passes, asynchronous canceled
call undercounting and fatal suppression, mutable exported cases invalidating task identity, raw payloads in redacted
diagnostics, and unused legacy provenance construction for custom loops. The worker is not OS isolation. Its internal
compilation is included in whole evaluation-call units, not separately instrumented compiler/CPU/memory totals.

Companion `ddad4e3e7` adds caller-owned custom fitness through the facade with version-pinned identities, explicit
conflict validation and minimization configuration. Its existing CLI now offers an
[authored C# runtime pilot](https://github.com/ooples/AiDotNet/blob/ddad4e3e71e214ddc5c0fbeb072c9854c57fea64/docs/evolution-runtime-benchmark.md):
independent public correctness checks, search timing, frozen-winner confirmation, shared receipts and write-once
failure-inclusive evidence. The retained four-run export contains all 96 worker calls/units, zero unknown receipts
and unchanged listed binary hashes. This fixed authored catalog uses no model calls; timings include startup,
compilation, execution and cleanup. It is not an isolated algorithm benchmark, sealed suite or competitive win.

Companion `367fce237` adds opt-in [evidence-verified persistent program fitness](https://github.com/ooples/AiDotNet/blob/367fce237ac000873f4749e5fcfa6ada4002e9cb/docs/evolution-persistent-fitness.md)
below fresh correctness checks. Producer origin survives descriptors/gating; unsupported LLM score blending fails
before model calls. Exact source/scope, raw-evidence verification, age/uncertainty rules, force-fresh and metered
logical store calls are integrated through custom fitness. Acceptance-time age is rechecked after verification.
The facade refuses bypassing outer memoization and uncoordinated resume. Independent engines use distinct run
ledgers with shared evidence stores; campaign-wide allocation and production raw-evidence providers remain explicit.
Its checked-in focused suite runs in the source-pinned workflow. These are scripted control-flow contracts,
not a representative runtime campaign or a new competitive result.

Hosted source validation at `d0c4039ac` failed NU1008 because the nested core checkout inherited consumer Central
Package Management; the companion workflow now uses sibling checkouts. The normal package-path wiki build also
failed on missing `EvolutionResourceLedger`, confirming the published-preview gap. Neither failure is hidden by
the local source-path tests; current-head hosted validation remains pending.

[AiDotNet.Tensors companion PR #1024](https://github.com/ooples/AiDotNet.Tensors/pull/1024), `665cb3c8`: exact-observation
deactivation plus opt-in persistent configuration quarantine and explicit rollback to a retained validated prior
snapshot. Publication admission shares the journal gate with quarantine, so a previously loaded winner cannot
race past deactivation. Bounded regression receipts, corrupt-record rejection, independent persistence outcomes
and conservative process-local blocking on write failure are implemented without adding disk work to dispatch.
This extends the existing validated promotion machinery, not a replacement promotion system. Cross-process active
revocation, automatic drift detection, bounded retuning and program/AutoML registries remain open.

The existing [ask/tell PR #14](https://github.com/ooples/AiDotNet.Evolution/pull/14) is not merged or duplicated here.
Durable evaluation identities and leases must be developed against its eventual agreed API.

## Story-by-story remaining work

“Partial” means at least one useful slice exists; it does not close the story's acceptance criteria.

| Story | State | Work still required |
| --- | --- | --- |
| US-01 representative suites | Partial | Authored C# end-to-end pilot now runs; representative program/kernel/AutoML tasks, AlgoTune subset and sealed task partitions remain. |
| US-02 fair baselines | Partial | Matched-population SciPy differential evolution now uses the shared C# evaluator and independent counters; native-default/tuned controls, OpenEvolve and consistent model access remain. |
| US-03 correctness gates | Partial, companion | Real C# worker/facade checks and fail-closed truncated-output/owned-case contracts exist; trusted OS isolation, reference integrations and held-out validation remain. |
| US-04 statistical evidence | Partial | Paired task/run analysis, failure-inclusive effects/intervals and trace checks implemented; prospective sample-size/power design and representative confirmation remain. |
| US-05 resource ledger | Partial | Generic ledger and adapters plus bounded C# consumer model/compiler/setup/audit/evaluation integration implemented; other consumer stages, coordinated persistence and deterministic concurrent admission remain. |
| US-06 noisy evaluation | Partial | Fresh bounded replicate runner, per-sample costs, finite-look uncertainty and separate confirmation identities implemented; archive resampling policy, cascade-rejection audit and representative noisy comparisons remain. |
| US-07 ablations | Partial | Same-operator uniform/adaptive allocation is available; representative island, migration, novelty and dispatch ablations remain. |
| US-08 adaptive operators | Partial | Parent-improvement/archive-success policies, proposal-plus-evaluator credit and typed attribution implemented; full consumer-stage integration and representative held-out comparisons before default promotion remain. |
| US-09 Pareto pipeline | Implemented on #52 | Feasible fronts, separate infeasible exploration, metadata/checkpoints/query/selection/migration/stopping and retained 180-run comparison; review and dependencies remain. |
| US-10 engine performance | Local acceptance verified on #53 | 44 cases × three isolated repetitions, peak memory/allocation/checkpoint scaling, quality-over-time/utilization and eight deterministic worker groups; current-head CI/review and representative release thresholds remain separate. |
| US-11 application examples | Partial, companion | Executable authored-C# facade/worker/timing/confirmation example exists; representative program, AutoML, kernel and external-session examples remain. |
| US-12 quality release gates | Partial | Representative measured quality thresholds, confidence and release artifacts. |
| US-13 typed search spaces | Implemented | Mixed/conditional domains, owned canonical genomes, operators/refinement, checkpointed diagonal CMA and pinned simpler-baseline comparison; no universal-quality claim. |
| US-14 surrogate assistance | Partial | Cost-metered acquisition, exploration/fallback contracts and a numeric KNN example implemented; production calibration/backends, representative expensive/noisy evidence and durable observation integration remain. |
| US-15 multi-fidelity | Partial | Bounded successive-halving bracket, exploration, fidelity/replicate identities, incremental state handoff and fresh full confirmation implemented; real learning-workload integration, durable bracket resume and representative comparisons remain. |
| US-16 adaptive islands | Not implemented | Resource allocation, heterogeneous policies and checkpointed restarts. |
| US-17 compiler-guided edits | Partial, companion | Exact identity/boundaries plus bounded C# syntax edits, real emit, compiler repair, reference/assembly fingerprints, attempt evidence and a real console worker/facade integration implemented; correctness-driven repair, OS isolation, public-API/target validation, multi-file evolution and representative performance confirmation remain. |
| US-18 reusable experience | Not implemented | Provenance-backed retrieval, lessons and calibrated semantic novelty. |
| US-19 model/prompt routing | Not implemented | Consumer routing policy, end-to-end costs, replay and fixed-routing comparisons. |
| US-20 proposal concurrency | Implemented; local acceptance verified, review/CI/dependencies pending | Opt-in bounded feedback-wave pipeline, immutable semantic snapshots, explicit concurrency capability, rate limits/cancellation, deterministic resource phases/retries and bounded queue/utilization/commit/abort diagnostics. 691 tests per TFM; 576 authored live cases with exact offline response replay; 132 matched default-profile pairs plus retained hardware-mismatch campaign. [Contract](PROPOSAL_PIPELINE.md), [raw evidence and limitations](../benchmarks/evidence/pipeline/9c3441d/README.md). Pipeline remains off by default; representative model/competitor validation is not established. |
| US-21 durable external work | In progress; local fencing and durable coordinator implemented, integration/verification pending | Strict session/host/TypeScript tickets and compatibility; optional bounded atomic work+ledger coordinator with leases, heartbeat/cancellation, stale receipt reconciliation, compatible-worker capacity and explicit delivery-only/fork semantics. Local host/native tests and authored real-process recovery pass; final cross-target checks and durable engine/binding integration remain. [Session contract](EXTERNAL_WORK_IDENTITY.md), [durable contract and limits](DURABLE_EXTERNAL_WORK.md). |
| US-22 centroid archive | Partial | Fixed-K routing, immutable geometry, transactional offline projection and engine/checkpoint coverage implemented; matched common-reference runner available; controlled memory/latency and representative quality confirmation remain. |
| US-23 warm starts | Partial | Bounded [seed repertoires](WARM_START_REPERTOIRES.md), twelve-facet revalidation, [sample provenance](MEASUREMENT_ORIGIN.md), [persistent evaluation storage](PERSISTENT_EVALUATION_REUSE.md), age/uncertainty/force-fresh decisions and store accounting implemented. Real-engine deterministic cold/warm/force-fresh example and AiDotNet fitness-facade integration pass; production raw-evidence providers, other consumer integrations and representative fair warm/cold/freshness campaigns remain. |
| US-24 CLI/dashboard | Partial | Core numeric CLI plus consumer authored-C# pilot and existing YAML commands; broader lifecycle/provider/dashboard integration remains. |
| US-25 promotion/retuning | Partial, companion | Tensors persistent quarantine, guarded publication and explicit validated rollback implemented; automatic drift/bounded retuning, coordinated cross-process revocation and program/AutoML registry remain. |
| US-26 policy meta-evolution | Not implemented | Opt-in declarative policy search, held-out outer loop and complete inner/outer cost accounting. |

## Validation evidence

- AiDotNet companion `367fce237`: 941 focused consumer tests and 79 optional compiler/worker tests pass separately
  on net8.0/net10.0. The focused suite is now checked in and wired to pinned-source CI. Changed production C#:
  111/112 executable lines covered; new reuse evaluator 89/90; selected evolution namespace 5,672/6,095.
  Normal net8/net10/net471 builds pass with existing warnings, matching production/test-copy hashes; no net471
  test claim. Old hosted ddad CLI tests passed 42/42 but the gate counted duplicate TRX attachment paths; both
  downloaded reports have identical SHA256 and the fixed gate passes their 268/270 benchmark lines. Ten gate
  regressions pass. Current-head hosted review/package integration remains outstanding.
- Persistent evaluation slice: 594 tests pass on each of net10.0/net8.0/net471 (28 new cases). Final net10 coverage:
  8,172/8,884 lines (91.99%), 4,866/6,242 branches (77.96%); new source files 255/257 executable lines.
  Final real-engine cold/warm/force-fresh example checks evaluator calls 8/0/8, store calls 16/8/8, validation
  calls 8/8/8 and unchanged original IDs only on reuse. Prior acquisition remains accounted, not free information.
  Production/test-copy hashes match; whitespace passes. This is deterministic integration evidence, not
  representative noisy-task freshness, cross-process coordination or competitor superiority.

- Measurement-origin slice: 566 tests pass on each of net10.0/net8.0/net471 (35 new cases). Net10 coverage:
  7,916/8,627 lines (91.76%), 4,708/6,074 branches (77.51%); new origin class has 100% executable-line coverage.
  Tests cover zero-cost cache copies after restart, migration, checkpoint pre-decode rejection, both compressed
  trace formats, provenance-preserving invalid receipts and replication refusal of reused/aggregate samples.
  Normal all-target library build: zero warnings/errors. This is provenance infrastructure, not persistent reuse
  or a completed US-23 campaign; legacy trace readers may silently discard new provenance.

- Warm-start repertoire slice: all 531 core tests pass separately on net10.0/net8.0/net471, including 53 new
  repertoire cases. The final suite covers changed constraints/evaluator behavior, fresh engine evaluation,
  source/current import decisions, schema and identity drift, malformed/oversized JSON, cancellation and provenance
  limits. Net10 coverage is 7,741/8,453 lines (91.58%) and 4,563/5,916 branches (77.13%); the two new source files
  cover 227/228 executable lines. All production/test-copy DLL hashes match; whitespace verification passes.
  These are deterministic contract tests, not a warm/cold superiority campaign or evidence for reusing old fitness.

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
- AiDotNet companion, earlier v4 evidence: 1,042 tests pass separately on net10.0 and net8.0 (1,020 UnitTests.Evolution tests and 22 facade integration tests),
  including 15 new gate/metadata tests, three facade checks, 12 source-identity/proposal/output tests and two malformed-source
  retry tests. Six identity regressions first failed against the old library. Earlier scoped coverage passed the 1,040-test selection: the correctness
  decorator, genome codec and new Unicode validation method have 100% line/branch coverage. The `ProgramGenome` class
  has 98.24% line / 91.66% branch coverage. These are not whole-repository coverage figures. Final local net10/net8
  builds disabled diagnostic analyzers and used unchanged dependency assemblies; they do not establish full analyzer
  or net471 compatibility. The earlier compiler exit -1 has no established root cause. Interface/checkpoint migration
  notes and bounded malformed-source retry handling addressed that review; CodeRabbit approved the then-current head
  `75aa6b1d` on September 10. This is not Copilot review or hosted build completion.
- AiDotNet variation v5 at `ddd80160b`: 1,082 authored Evolution/facade tests pass separately on net10.0 and net8.0,
  including 40 new cases. Ten regressions first failed against `75aa6b1d`: nine compatibility hashes and one protected
  full rewrite. The new boundary helper has 100% line/branch coverage; fenced extraction has 99.1% line/92.72% branch
  coverage. A focused local harness compiles the existing test sources against the actual built library, with matching
  production/test DLL hashes. These pre-migration counts include duplicate core tests subsequently removed by #2092;
  they must not be compared directly to post-migration consumer-only counts. Diagnostic analyzers were disabled;
  whole-repository/hosted/current-head review validation remains separate.
- AiDotNet package integration at `f317ed04` with the ownership-assertion follow-up: 857 selected consumer tests pass
  separately on net10.0 and net8.0 against core `6d9aeb2`, including real MAP-Elites AutoML, facade and YAML tests.
  One regression assertion was corrected to require the engine-owned parent snapshot, not caller reference identity.
  This is local project-path validation; the published preview still points to `f0f282cf`. The detailed migration
  evidence and DLL hashes are in the companion's `docs/evolution-package-integration.md`.
- AiDotNet companion `fcb29b770`: all 876 focused consumer/facade/AutoML/YAML tests and all 62 optional compiler
  package tests pass separately on net10.0 and net8.0. Compiler-package net10.0 coverage is 499/505 lines (98.81%)
  and 331/360 branches (91.94%). Main DLL hashes matched the test copies before execution; compiler tests invoke
  real Roslyn emit with scripted model and execution doubles. The optional package locally packs both TFMs,
  XML documentation, license and README; that is not proof of compatible published AiDotNet/Evolution dependencies.
  Normal NuGet resolution, net471 verification, whole-repository/current-head CI and review remain separate.
- AiDotNet companion `d0c4039ac`: 888 focused consumer tests and 79 compiler/worker tests pass on each of net8.0
  and net10.0. Twelve evaluator/custom-provenance regressions were first red. Combined optional/worker coverage on
  each target is 544/550 lines and 365/404 branches; separately, compiler coverage is 98.81%/91.94% and worker
  coverage 100%/77.27%. Targeted consumer coverage is 4,994/5,408 lines (92.34%) and 2,434/2,989 branches (81.43%),
  excluding the builder and unrelated assembly types. Production/test-copy hashes matched before execution. Worker
  fixtures execute real C# but are neither live-model optimization benchmarks nor hostile-code containment tests;
  Linux behavior, normal NuGet resolution and current-head hosted validation remain unverified.
- AiDotNet companion `ddad4e3e7`: 901 focused consumer tests plus 79 optional compiler/worker tests pass separately
  on net8.0 and net10.0. Forty-two CLI tests pass on net10.0; the benchmark file has 268/270 executable lines covered,
  with a 90% file gate and explicit coverage-module verification. The full CLI is 389/463 lines, not 99%.
  The custom-fitness identity wrapper has 100% line/branch coverage; selected consumer program/options paths have
  5,026/5,440 lines and 2,456/3,011 branches covered (builder and unrelated types excluded). Library rebuilds succeed
  on net8.0/net10.0/net471 with diagnostic analyzers disabled and existing warnings retained; no net471 tests are
  claimed. The four-run raw pilot export retains every sample and receipt, including the deliberately wrong
  candidate's failures. Current-head hosted/package-path validation and independent review remain outstanding.
- [Paired analysis](benchmarks/analysis-d62d5cb/report.md): 14 Python contract tests pass. Retrospective reporting retains every scheduled run, marks unknown
  work and incomplete curves, and resamples paired seeds within tasks. The pinned CMA-versus-hill-climbing adjusted
  utility-difference interval crosses zero; the pilot is not evidence for promoting CMA as a universal default.
- [Development pilot](benchmarks/NUMERIC_PILOT.md): 200 runs / 51,200 calls, no failures. Adaptation beats the
  same-operator uniform control on median loss here, but hill climbing beats both on every task's median loss.
  Adaptation remains opt-in; this is not representative or statistically confirmed superiority.
- [Diagonal CMA comparison](benchmarks/DIAGONAL_CMA_PILOT.md): 240 runs / 61,440 calls, no failures. The emitter's
  median final loss beats hill climbing on three development fixtures and loses on the rippled fixture. All six
  methods share the complete initial population and evaluator cap; proposal/evaluator ledger totals are retained.
- Tensors companion `665cb3c8`: 191 main-project autotuning tests pass separately on net10.0 and net471; 64 focused
  linked-source tests pass on net8.0 (the main test project has no net8.0 target). Normal library builds across all
  three targets succeed with zero warnings/errors. Test-project rebuilds retain existing warnings and disable
  diagnostic analyzers; no entire-suite/GPU execution claim is made. Coverage: quarantine file 174/175 executable
  lines; changed tuner lines 28/30; changed cache write line covered. The broader selected files are 615/733 lines
  and 291/359 branches, not 99%. An unsuppressed net471 cache probe first failed because a temporary suffix made a
  224-character final path 263 characters long; short sibling temporary filenames fixed the original and new
  persistence tests without shortening fixtures. Final production/test-copy DLL hashes were checked.
- Hosted validation remains incomplete for Evolution/AiDotNet (build/security checks queued). Tensors historical head
  `77d16869` had successful build, AVX-512 verification and returned GPU-parity checks; new head `665cb3c8` requires
  fresh hosted results. Earlier duplicate title
  runs were cancelled. AiDotNet's CodeRabbit approval applies only to historical head `75aa6b1d`; its later review
  was skipped because 175 files exceeded the 100-file limit. No current-head CodeRabbit or Copilot approval is claimed. This historical validation does not describe current draft/readiness states;
  consult the delivery index and individual PRs. No full-roadmap or merge-readiness claim.

## Experiment access policy

Use local compute and the user's existing subscription-authenticated Codex access where appropriate. Do not silently
switch to API-key billing, purchase credits or rent compute. Subscription access still has usage limits. A fair model-driven
comparison requires the same model-access mechanism, settings, context and tool permissions for both frameworks; an
agentic Codex run must not be described as equivalent to a raw model-completion call without controlling those differences.
