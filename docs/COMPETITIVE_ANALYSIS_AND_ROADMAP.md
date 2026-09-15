# AiDotNet.Evolution: competitive assessment and improvement roadmap

Assessment date: September 10, 2026. Revision 2 incorporates adversarial review and an expanded functionality backlog. “Open evolved” is interpreted as OpenEvolve.

Implementation is now tracked separately in [IMPLEMENTATION_STATUS.md](IMPLEMENTATION_STATUS.md). This assessment describes the pinned baseline; it is not a claim that all roadmap stories have been implemented.

## High-level assessment

AiDotNet.Evolution has a substantial engineering foundation and already measures whether evolution improves search results. The largest gap is the evidence needed to show that those improvements generalize to real algorithms and exceed competitors under comparable budgets.

Develop the measurement foundation and new functionality in parallel. Use benchmark results to decide which features become defaults and which competitive claims can be published; a completed benchmark program should not be a prerequisite for building useful capabilities.

The highest-value feature candidates are adaptive mutation/model selection, typed search spaces with ready-to-use operators, surrogate-assisted and multi-fidelity search, a compiler-guided program improvement loop, concurrent proposal generation, and durable external evaluation. Pareto retention, reusable experience, warm starts, and deployment retuning turn individual search runs into a more useful product. These are recommendations to implement and validate, not claims of measured gains.

The strongest positioning opportunity is reproducible, efficient optimization for .NET applications, with demonstrated gains in program optimization, AutoML, and kernel tuning. This is a proposed direction; superiority over OpenEvolve is currently unproven.

### Scope and verification

- Reviewed AiDotNet.Evolution `main` at `f0f282cf2e9027ab5278a1953493c805d6ab52ad`, including orchestration, archives, evaluation contracts, trace reporting, tests, and CI.
- Reviewed OpenEvolve at `411fb59c886c18704caaffb611e17cf9e7d824d2` and relevant primary benchmark documentation.
- Locally ran `dotnet test tests/AiDotNet.Evolution.Tests/AiDotNet.Evolution.Tests.csproj -c Release -f net10.0 --verbosity quiet` using SDK `10.0.401`: **280 passed, 0 failed, 0 skipped**.
- This assessment did not run OpenEvolve, paid model experiments, hardware benchmarks, or fresh coverage collection. Consumer implementations in AiDotNet and AiDotNet.Tensors were outside this audit.
- [PR #14](https://github.com/ooples/AiDotNet.Evolution/pull/14) proposes ask/tell sessions and was open during review. It is relevant upcoming infrastructure, not functionality counted as merged.
- The adversarial pass rechecked GitHub: `main` remains at the same commit and PR #14 remains open at `cf631f38206fa9a6048bff1e59d45d7467066452`. It inspected proposal feedback, dispatch, artifact delivery, and the proposed external-evaluation API. The test result above is from the initial review; this documentation revision did not rerun unchanged code or execute the PR.
- Expanded competitor coverage uses primary sources for ShinkaEvolve, SkyDiscover/AdaEvolve/EvoX, and LoongFlow. Their reported performance was not independently reproduced.

## Adversarial review: findings and corrections

This pass challenged the original recommendations, checked whether proposed capabilities already exist, and looked for ways an apparent performance win could be misleading. “Verified” below describes source behavior; it does not imply a measured production incident.

| Finding | Evidence and consequence | Correction |
| --- | --- | --- |
| High: the original plan overemphasized measurement and postponed useful functionality. | Most early stories concerned reports, tests, or comparisons. It did not provide a sufficiently concrete product-development program. | Add US-13–US-26, strengthen US-08/US-09, and build feature prototypes alongside evaluation infrastructure. |
| High: existing diversity evidence is narrower than the original wording suggested. | The proof uses population count as its descriptor; for OneMax this is also the quality. Evolution starts at the all-zero genome, while uniform random does not share that seed. [Proof tests][quality-tests]. | Treat this as a controlled smoke test for search pressure. Equalize starting populations and test descriptors independent of fitness, unseen instances, and stronger optimizers before claiming a diversity advantage. |
| High, verified: checkpointable variation is not a complete adaptive-operator API. | Operators expose proposal generation and optional state persistence; the engine sends direct outcome feedback to selection policies. Existing artifact delivery is bounded, replaces a parent's pending artifact array, and consumes it on one proposal. [Variation contract][variation], [commit path][evaluation-loop], [artifact delivery][cascade-engine]. | Add explicit operator attribution/outcome feedback in US-08; build structured, reusable experience in US-18. Reuse current feedback facilities rather than describing them as absent. |
| High, verified: evaluator concurrency does not supply concurrent proposal generation. | Both dispatch paths await `PrepareVariationAsync`, which awaits the variation operator. A slow LLM proposer can therefore limit throughput despite extra evaluator workers. [Batch dispatch][engine], [continuous dispatch][continuous], [proposal path][evaluation-loop]. | Add independent bounded proposal/evaluation pipelines in US-20; measure the bottleneck before choosing limits. |
| High, static PR risk: external evaluation needs stronger identity and provenance. | At the reviewed PR head, canonicalization uses `genome.ToString()`, task/evaluator versions are fixed strings, and `Tell` takes an evaluation ID without an attempt token. Distinct genomes can share display text; retries require stale-result protection. [Proposed session][session-pr]. | US-21 requires caller-supplied canonicalization/version fingerprints and run/attempt identity. Collision and delayed-reply scenarios are acceptance tests; these are unmerged-code concerns, not reproduced failures on `main`. |
| High, verified: Pareto support needs more than another archive class. | Result snapshots recompute `Best` through scalar-quality ordering, even when built from an archive interface. [Snapshot implementation][snapshot], [ordering][ordering]. | US-09 must define front-aware results and selection/stop semantics across the pipeline; a scalar representative must be an explicit policy. |
| Medium: repeatability claims need precise boundaries. | Extending a budget after a truncated logical batch can change the proposal trajectory; continuous dispatch depends on the resolved in-flight window. Live models and timing are also external sources of variation. [Options][options]. | Separate replay, equivalent-boundary resume, and a deliberately forked continuation. Preserve pending logical work if exact mid-batch continuation becomes a requirement. |
| Medium: statistical and benchmark choices could favor the desired answer. | A percentage gain on an arbitrarily shifted quality scale is ambiguous; repeatedly adding seeds until significance appears is invalid without a sequential design. Public tasks can also be familiar to models. | Define task-level effects, stopping rules, failure handling, disjoint development/validation/final-test partitions, and a concrete runtime/cost endpoint in US-01/US-04/US-12. |

The review supports new functionality, but it does not support a blanket claim that AiDotNet.Evolution exceeds every competitor. The expanded backlog distinguishes existing mechanisms, new core capabilities, and consumer features whose implementation status requires a separate consumer inventory.

## What is already measured, and what remains missing

| Area | Evidence in the repository | Interpretation |
| --- | --- | --- |
| Engineering reliability | Deterministic orchestration, checkpoint compatibility, immutable genomes, migration, retries, continuous dispatch, and corresponding regression tests. [Core engine][engine], [test suite][tests]. | A useful foundation for dependable experiments; passing tests alone does not establish competitive search quality. |
| Search effectiveness | Three 48-bit problems: OneMax, WeightedOneMax, LeadingOnes. Five seeds and up to 4,096 evaluations; comparisons cover best quality, occupied cells, target hits, and evaluations to target. [Search-quality tests][quality-tests]. | There is already a real quality test, not just code coverage. Its comparator is uniform random search. |
| Representativeness | Those quality tests use a custom best-elite selector, one island, batch size one, no migration, and no inspirations. [Search-quality tests][quality-tests]. | They do not establish gains from the default selector, the other built-in policies, islands, or LLM-driven program evolution. |
| Observability | Evaluation identities, lineage, metrics, costs, cache status, version hashes, and trace summaries with improvement statistics. [Evaluation record][evaluation], [trace summary][trace]. | Extend this infrastructure into experiment reports. A child improving on its parent is different from beating the starting implementation or a competitor. |
| CI and coverage | Multi-framework checks and a coverage ratchet; checked-in baseline is 89.8% line and 74.51% branch coverage, dated September 4. [Build workflow][ci], [baseline][coverage]. | These are engineering checks. The baseline is not a fresh measurement from this review. No dedicated competitor benchmark runner or statistical comparison workflow was found in the reviewed tree. |
| Budget comparability | Rejected cascade screens can refund the evaluation-attempt charge by default. [Cascade options][cascade], [evaluation loop][evaluation-loop]. | Equal evaluation limits can still purchase unequal amounts of work. Screen, retry, model, and hardware costs need explicit accounting. |
| Optimization semantics | The supplied archive ranks by scalar quality. Objective and constraint vectors are retained; insertion does not automatically enforce feasibility or Pareto dominance. `Remeasure` updates descriptors. [Archive][archive], [ordering][ordering]. | Correctness gates, multi-objective retention, and repeated fitness measurement require explicit policies. Descriptor remeasurement is not noise-resistant fitness reevaluation. |

## Competitive context

| Reference | Relevant strength | Recommended comparison |
| --- | --- | --- |
| OpenEvolve | An application-level program evolution system with LLM ensembles, prompts, evaluation, population search, and examples. [Repository][openevolve], [configuration][oe-config]. | Compare the complete AiDotNet program-evolution integration against OpenEvolve. Comparing only this core library would omit essential parts of the workflow. |
| OpenEvolve examples | AlgoTune and symbolic-regression integrations offer concrete starting workloads. Its AlgoTune report summarizes eight successful tasks and discusses prompt/configuration tuning. [AlgoTune example][oe-algotune], [symbolic regression][oe-symbolic]. | Reproduce a declared task set, including failures. Treat reported example speedups as upstream claims, not a matched comparison against AiDotNet.Evolution. |
| pyribs and QDax | Established quality-diversity implementations; pyribs includes CMA-ME/CMA-MAE, while QDax provides additional algorithms and benchmark tasks. [pyribs][pyribs], [QDax][qdax]. | Use appropriate shared numeric tasks to test the core search machinery. These complement the OpenEvolve application comparison. |
| ShinkaEvolve | Adaptive parent sampling, novelty rejection, and bandit-based model selection; current tooling also supports asynchronous workflows and result inspection. [Paper][shinka-paper], [repository][shinka]. | Compare sample efficiency and model cost. A fixed model ensemble and basic novelty threshold alone would not establish a feature advantage. |
| SkyDiscover / AdaEvolve / EvoX | Adaptive island search, search-strategy evolution, several baseline backends, and explicit Pareto configuration are documented. [Repository][skydiscover]. | Include a current adaptive-search baseline and treat meta-evolution as a separate experimental capability. Do not present Pareto support as unique to us. |
| LoongFlow | Plans experiments, executes them, summarizes outcomes, and retrieves structured experience. [Repository][loongflow]. | Evaluate whether compiler/test-guided repair and reusable lessons improve our program evolution beyond immediate mutation feedback. |

Preserve the documented architecture: generic search contracts belong here; LLM clients, executable-code isolation, model training, and device-specific evaluators belong in consumer integrations. [Integration boundaries][readme].

## New functionality map

“New” means absent from the audited core's supplied implementations, not necessarily absent from every consumer repository. Consumer-owned stories require checking and reusing existing integration code before adding another implementation. Proposed extension/package names below describe responsibilities, not APIs that already ship.

| Functionality | Existing foundation | Concrete addition | Story / priority |
| --- | --- | --- | --- |
| Adaptive search operators | Variation, refinement, selection feedback, state checkpoints. | Outcome-aware operator portfolios with per-operator credit and cost. | US-08 / P1 |
| Multi-objective solutions | Stored objective/constraint vectors; scalar archive. | Feasibility policy and bounded Pareto retention with explicit objective directions/tolerances. | US-09 / P1 |
| Ready-to-use numeric search | Generic typed genomes and user-supplied operators. | Conditional search spaces, seed generation, numeric/categorical mutation, crossover, local search, and optional CMA-style emitters. | US-13 / P1 |
| Fewer expensive evaluations | Caching, fixed cascade gates. | Surrogate ranking with uncertainty and resource-adaptive multi-fidelity promotion. | US-14, US-15 / P1 |
| Escaping stagnation | Fixed islands, selection policies, migration. | Budget reallocation, bounded restarts, and heterogeneous island policies. | US-16 / P1 |
| Program improvement workflow | Consumer task/variation interfaces and artifacts. | Planned code edits, syntax-aware multi-file changes, compile/test repair, and reusable lessons. | US-17, US-18 / P1 |
| Model and prompt adaptation | Consumers may supply LLM operators. | Cost-aware model routing and versioned prompt selection from observed outcomes. | US-19 / P1 |
| Faster generation and external execution | Evaluator parallelism; pending ask/tell PR. | Concurrent proposal pipeline, durable worker leases, stale-result rejection, and resource-aware dispatch. | US-20, US-21 / P1 |
| Diversity at larger descriptor counts | Sparse grid archive, growth, calibration, distance hook. | Fixed-capacity centroid archive and behavior-aware novelty strategies. | US-22 / P2; US-18 / P1 |
| Reuse across runs | Seeds, run checkpoints, run-local memoization. | Versioned elite import and compatible persistent evaluation records. | US-23 / P1 |
| Usable product and deployable results | Library API and trace files. | CLI/inspection tools, exportable validated winners, fallback, and controlled retuning. | US-24, US-25 / P1 |
| Self-improving search policies | Pluggable strategies and recorded state. | Offline search over bounded, declarative search policies with independent evaluation. | US-26 / P2 research |

## Measurement scorecard

Keep engineering quality, search effectiveness, and the quality of the resulting algorithm visible separately.

| Question | Measure | Required interpretation |
| --- | --- | --- |
| Is the candidate correct? | Fraction passing independent correctness checks; worst numerical error; constraint violations. | Correctness is a promotion gate. Reduced precision or changed semantics must satisfy the declared task contract. |
| Is the resulting algorithm better? | Runtime speedup `reference runtime / candidate runtime`; memory use; task-specific accuracy or objective value. | Measure on held-out inputs and the same execution environment. Report compilation/startup separately from steady-state performance. |
| Does search find improvements efficiently? | Best valid quality versus evaluations, elapsed time, and total cost; area under each progress curve. | Fix direction and task normalization before running; keep the original valid implementation as the incumbent fallback. |
| How reliably does it succeed? | Target-hit rate and budget needed to reach a declared target. | Include unsuccessful runs. Treat their time-to-target as censored at the budget; also show success rates. |
| Does diversity add value? | Archive coverage and normalized QD score: the sum of per-cell normalized elite quality, divided by the fixed reporting-grid size. | Use identical descriptor definitions and normalization. Project adaptive archives onto a common reporting grid; also report best quality. |
| What does improvement cost? | Model tokens/calls, evaluation and screening work, CPU/GPU time, money, retries, duplicates, and cache hits. | Count proposal generation and failures. Summed evaluator duration is not total run wall time when evaluations overlap. |
| Is an apparent win repeatable? | Per-task/per-seed results, effect sizes, 95% confidence intervals, win/tie/loss counts. | Pair shared task instances/seeds where meaningful; account for runs nested within tasks. Predeclare aggregation, multiplicity handling, and fixed-sample or valid sequential stopping rules. |
| Can users reproduce it? | Run manifest, artifact hashes, replay outcomes, and compatible checkpoint/resume checks. | Deterministic orchestration does not make live LLM responses or hardware timing deterministic. |

## Prioritized user stories

P0 establishes trustworthy evaluation and essential contracts; P1 delivers useful functionality; P2 covers later extensions/research. Dependencies identify integration and promotion prerequisites, not a requirement to postpone independent design or prototyping. Numeric thresholds below are proposed acceptance targets, not observed results.

### US-01 — Establish a representative benchmark suite

**Priority:** P0. **Owner:** Benchmark maintainer.

As a maintainer, I want a versioned benchmark suite so that I can measure progress beyond simple synthetic landscapes.

- **Given** the existing three proof problems, **when** the suite is expanded, **then** it includes deceptive, multimodal, constrained, noisy, and expensive-evaluation cases, with untouched evaluation instances.
- **Given** a comparison of search policies, **when** runs begin, **then** methods receive equivalent starting populations and information, and diversity tasks include descriptors that are not merely another encoding of fitness.
- **Given** public benchmark exposure, **when** final evaluation is prepared, **then** independently generated instances and disjoint task families complement public tasks; development, model/configuration selection, and final reporting use separate partitions.
- **Given** program optimization is a target use case, **when** the first application benchmark is defined, **then** it includes a predeclared 10–20-task AlgoTune subset across several algorithm families, plus a valid starting implementation for every task. [AlgoTune][algotune].
- **Given** a published run manifest, **when** another developer executes it, **then** task versions, seeds, budgets, dependencies, hardware information, and evaluator hashes are recorded in the result bundle.

Deliverable: a separate benchmark project/runner and task manifests; retain the existing tests as fast regression checks.

### US-02 — Make fair comparisons against competitors

**Priority:** P0. **Owner:** Benchmark/integration maintainer. **Depends on:** US-01.

As a product owner, I want budget-matched comparisons so that an advantage reflects our search system rather than extra resources.

- **Given** shared numeric tasks, **when** core search is compared, **then** baselines include random search, hill climbing, and applicable MAP-Elites/CMA-ME implementations, with equal starting information and evaluator access.
- **Given** a program-optimization task, **when** AiDotNet's integration and OpenEvolve run, **then** they use the same candidate language, evaluator, model/version, hardware, allowed libraries, and initial program, under separately declared evaluation, time, and cost limits.
- **Given** system-specific prompts or tuning, **when** results are published, **then** controlled comparisons and equally budgeted native configurations are reported separately, with all tuning effort disclosed.

Deliverable: competitor adapters pinned to commits, plus a one-shot LLM and iterative single-parent baseline to test whether population search adds value.

### US-03 — Prevent invalid improvements from winning

**Priority:** P0. **Owner:** Consumer evaluator maintainer. **Depends on:** US-01.

As a developer using evolved code, I want independent correctness checks so that optimization preserves required behavior.

- **Given** a fast candidate with wrong outputs or violated constraints, **when** it is evaluated, **then** it cannot become a deployable winner regardless of its scalar performance score.
- **Given** search-visible training cases, **when** finalists are selected, **then** an independent evaluator checks held-out, boundary, adversarial, and randomized cases with predefined numerical tolerances; hidden-test results never feed back into that search.
- **Given** generated executable code, **when** the consumer runs it, **then** it executes with enforceable process/resource isolation and cannot alter the evaluator or access hidden test data.

Deliverable: reusable evaluator contracts and consumer-specific correctness oracles. Explicitly document the supplied archive's constraint semantics. [Archive][archive].

### US-04 — Turn traces into trustworthy experiment reports

**Priority:** P0. **Owner:** Benchmark maintainer. **Depends on:** US-01–US-03.

As a researcher, I want statistical reports so that I can distinguish repeatable gains from favorable seeds.

- **Given** a benchmark campaign, **when** results are aggregated, **then** every task and failed run appears alongside progress curves, effect sizes, confidence intervals, and the scorecard above.
- **Given** pilot variance estimates, **when** a release comparison is planned, **then** sample size is fixed before final testing or governed by a valid sequential design; 10 pilot seeds and approximately 30 confirmatory runs are planning examples, not guarantees of sufficient power or a rule to keep sampling until a win appears.
- **Given** multiple tasks, **when** confidence intervals are computed, **then** paired comparisons respect task/run nesting, failures remain included, and timing repetitions are not treated as independent search runs.
- **Given** bounded traces that dropped records, **when** a report is generated, **then** it flags incomplete trajectories and uses independently maintained complete counters instead of inferring missing work.

Deliverable: machine-readable results and Markdown/HTML reports built on existing trace records. [Trace summary][trace]. Use the official scoring rules when naming an AlgoTune score; label subsets explicitly. [AlgoTune scoring][algotune-site].

### US-05 — Account for and enforce the real search budget

**Priority:** P0. **Owner:** Core and integration maintainers. **Depends on:** US-01.

As an operator, I want complete resource accounting so that optimization stays within budget and comparisons remain fair.

- **Given** model proposals, refinement, surrogate training/inference, novelty checks, retries, rejected cascade screens, and full evaluations, **when** any consumes resources, **then** its cost is recorded, including unsuccessful work and all comparative setup/tuning costs.
- **Given** a configured spending limit, **when** new work is dispatched, **then** estimated in-flight cost is reserved, actual cost reconciled, and any unavoidable overrun is bounded and reported.
- **Given** two cascade configurations, **when** they are compared, **then** screening calls and stage costs remain visible even when `ChargeRejectedStagesToBudget` is false.

Deliverable: a generic resource ledger/budget extension, with token and currency interpretation in consumers. Reuse `CostUnits`; do not treat it as automatically equivalent to dollars. [Cascade accounting][evaluation-loop].

### US-06 — Handle noisy measurements and expensive evaluation

**Priority:** P1. **Owner:** Evaluator maintainer. **Depends on:** US-03–US-05.

As a user optimizing runtime or model quality, I want robust evaluation so that a lucky timing or training run cannot create a false winner.

- **Given** a promising candidate, **when** it challenges an incumbent, **then** repeated measurements and an independent confirmation run estimate the improvement and its uncertainty.
- **Given** staged evaluation, **when** cheap screens reject candidates, **then** a predefined audit sample receives full evaluation to estimate false rejection of useful candidates.
- **Given** a request to remeasure fitness, **when** it runs, **then** cache policy permits fresh samples and all repetitions consume budget; descriptor-only `Remeasure` is not used as a substitute.

Deliverable: noise-aware consumer evaluators, controlled warmup/timing protocols, and validated cascade presets. [Remeasure implementation][archive].

### US-07 — Identify which existing features improve search

**Priority:** P1. **Owner:** Search maintainer. **Depends on:** US-04–US-06.

As a maintainer, I want feature-removal experiments so that defaults reflect measured benefits.

- **Given** the same benchmark and resource budget, **when** Uniform, Ratio, Curiosity, and Double selection are compared, **then** results show quality, diversity, success, and cost for each task family. [Selection policies][selection].
- **Given** islands, migration, novelty filtering, calibration, archive growth, and continuous dispatch, **when** each is enabled or removed, **then** its contribution and important interactions are measured against the same baseline.
- **Given** a better configuration on development tasks, **when** it is proposed as a default, **then** its benefit is confirmed on untouched tasks without changing the evaluation criteria.

Deliverable: evidence-backed presets for inexpensive numeric search, costly program search, and kernel tuning.

### US-08 — Adapt proposal strategies using measured outcomes

**Priority:** P1. **Owner:** Search and consumer maintainers. **Build foundation:** US-05. **Validate default promotion:** US-07.

As a user, I want search to favor productive proposal strategies so that it spends less effort on ineffective mutations.

- **Given** a portfolio of mutation, crossover, restart, local-refinement, and consumer-provided LLM operators, **when** outcomes arrive, **then** operator selection can use valid improvement or archive gain per unit cost while retaining exploration.
- **Given** an operator produced a candidate, **when** evaluation commits, **then** an explicit outcome contract identifies the responsible operator/configuration, archive outcome, and consumed resources exactly once; checkpoint support alone does not satisfy this requirement.
- **Given** adaptive operator state, **when** a compatible run resumes, **then** credit history and random-stream state are restored consistently.
- **Given** the best static preset, **when** adaptation is evaluated, **then** it must show a statistically supported useful gain on held-out tasks before becoming a default.

Deliverable: an optional portfolio with outcome feedback, typed attribution metadata, and checkpointing; reuse current variation/refinement contracts. Credit is based on measured valid improvements, with bounded rewards and a minimum exploration allocation. US-13 adds concrete operators; US-19 applies this foundation to models and prompts.

### US-09 — Preserve useful tradeoffs between objectives

**Priority:** P1 for speed/memory/accuracy use cases. **Owner:** Archive maintainer. **Depends on:** US-03, US-04.

As an application developer, I want several valid speed/memory/accuracy tradeoffs so that I can choose an implementation matching deployment requirements.

- **Given** conflicting objectives and hard constraints, **when** an optional Pareto archive is used, **then** it retains feasible nondominated candidates under a bounded capacity policy.
- **Given** objectives with different directions, scales, and tolerances, **when** candidates are compared, **then** explicit objective definitions determine dominance, hard constraints control promotion, and optional infeasible exploration is retained separately from deployable winners.
- **Given** a Pareto-aware run, **when** results are snapshotted, selected, migrated, or used for stopping, **then** objective/front semantics remain explicit; any scalar `Best` representative is a documented choice rather than silently replacing the front.
- **Given** a fixed objective normalization and reference point, **when** it is benchmarked, **then** hypervolume and individual objective outcomes are compared against scalar-quality search.
- **Given** existing scalar consumers, **when** the new archive is available, **then** they retain their established ordering and checkpoint semantics unless they opt in.

Deliverable: an optional Pareto archive plus front-aware result/query and policy contracts. Stored objective vectors and a new archive alone do not provide this behavior. [Ordering][ordering], [snapshot behavior][snapshot].

### US-10 — Measure engine overhead and scaling

**Priority:** P1. **Owner:** Performance maintainer. **Depends on:** US-01, US-04.

As a .NET developer, I want low orchestration overhead so that the engine remains useful when candidate evaluation is cheap.

- **Given** pinned hardware and runtime settings, **when** engine operations are benchmarked, **then** throughput, allocations, peak memory, and checkpoint overhead are reported across archive sizes, island counts, and worker counts.
- **Given** mixed evaluation durations, **when** batch and continuous dispatch are compared, **then** worker utilization and quality versus elapsed time are both reported.
- **Given** deterministic evaluators and fixed semantic settings, **when** worker count changes, **then** determinism checks pass; continuous-mode comparisons explicitly fix `MaxInFlight`.

Deliverable: a [BenchmarkDotNet](https://benchmarkdotnet.org/articles/overview.html) project outside the shipping package. Label engine throughput and resulting-algorithm speedup as different measurements.

### US-11 — Demonstrate value in consumer workloads

**Priority:** P1. **Owner:** AiDotNet and AiDotNet.Tensors maintainers. **Depends on:** US-02–US-06.

As an adopter, I want reproducible examples so that I can assess the value for my application.

- **Given** the generic engine, **when** the first integration milestone is delivered, **then** one complete program-optimization example reports OpenEvolve comparisons and preserves the original implementation as a fallback.
- **Given** subsequent AutoML and kernel-tuning examples, **when** evaluated, **then** they use independent data splits or numerical oracles, strong domain baselines, and representative input sizes/devices.
- **Given** external scheduling needs, **when** the ask/tell work lands, **then** integrations can use it after validating identity, cancellation, duplicate-result, and replay behavior.

Deliverable: consumer-owned examples and benchmark adapters. Additional language bindings are an adoption option after search value is demonstrated. [Boundaries][readme], [ask/tell proposal](https://github.com/ooples/AiDotNet.Evolution/pull/14).

### US-12 — Gate releases on demonstrated improvement

**Priority:** P0 infrastructure; P1 competitive claim. **Owner:** Release maintainer. **Depends on:** US-04, US-05, US-11.

As a product owner, I want release evidence so that performance claims remain credible over time.

- **Given** a pull request, **when** CI runs, **then** fast deterministic quality checks accompany existing engineering gates; larger competitor campaigns run on scheduled or explicitly budgeted jobs.
- **Given** a proposed claim of exceeding OpenEvolve, **when** the release report is reviewed, **then** it names the benchmark, task set, model, budget, and hardware, and publishes the complete result bundle.
- **Given** a predeclared program-runtime comparison, **when** superiority is evaluated, **then** a proposed gate requires at least 1.10x geometric-mean speedup over the competitor's resulting programs at equal total search cost, with a 95% interval above 1.0 and predefined per-task regression limits; alternatively, predeclare a 20% cost reduction to reach the same target. All promoted winners must pass independent correctness checks.
- **Given** mixed task outcomes or insufficient statistical power, **when** the report is published, **then** claims are limited to supported task families and inconclusive results remain visible.

Deliverable: a benchmark regression workflow and release evidence checklist. Choose one primary endpoint before the campaign and calibrate these provisional thresholds using pilot data before freezing the protocol. Generic quality objectives need task-specific effect definitions; percentages of arbitrarily shifted scores are not interchangeable with runtime speedups. Failed improvement searches retain the valid seed, while infrastructure failures are explicitly reported under a predefined scoring rule.

### US-13 — Supply typed search spaces and useful operators

**Priority:** P1. **Owner:** Core/algorithm maintainer. **Depends on:** US-03, US-05; adaptive emitters use US-08.

As a .NET developer, I want to describe my parameters and constraints so that I can run useful optimization without implementing every genome and mutation detail.

- **Given** numeric, integer, enum, logarithmic, and conditional parameters, **when** a search space is built, **then** it validates domains, samples initial candidates, and canonicalizes inactive parameters consistently.
- **Given** a supported genome, **when** a preset is selected, **then** supplied mutation, crossover, restart, and local-refinement operators produce valid independently owned candidates with stable random streams.
- **Given** continuous numeric tasks, **when** optional CMA-style emitters are enabled, **then** covariance/step-size state survives checkpoints and their quality-per-budget is compared against simpler operators. [CMA-ME implementations][pyribs].

Deliverable: a small search-space builder and operator catalog, with optional numeric adapters where heavier dependencies are needed. Existing custom genomes remain supported.

### US-14 — Rank proposals using a surrogate model

**Priority:** P1, experimental default off. **Owner:** Search maintainer; model implementations in AiDotNet/adapter package. **Depends on:** US-05, US-08, US-13.

As a user with expensive evaluations, I want a learned approximation to identify promising candidates so that fewer full evaluations are wasted.

- **Given** measured candidate outcomes, **when** a surrogate ranks a proposal pool, **then** it records predicted quality, uncertainty, model version, and an explicit exploration allocation; predictions never masquerade as validated archive fitness.
- **Given** weak calibration, unfamiliar candidates, or insufficient training data, **when** ranking becomes unreliable, **then** selection falls back to an established non-surrogate policy and continues collecting representative measurements.
- **Given** the same total cost cap, **when** surrogate and ordinary search are compared, **then** training, inference, and rejected-proposal costs are included and every promoted winner receives true evaluation.

Deliverable: an optional prediction/acquisition contract and a first numeric implementation. Support multiple prediction backends without making the generic engine depend on a model library.

### US-15 — Allocate evaluation resources dynamically

**Priority:** P1. **Owner:** Scheduler/evaluator maintainers. **Depends on:** US-05, US-06.

As a user training models or benchmarking many workloads, I want promising candidates to receive larger budgets so that full evaluation is concentrated where it helps.

- **Given** increasing resource levels such as training epochs, dataset sizes, or benchmark repetitions, **when** a successive-halving/Hyperband-style policy runs, **then** it promotes selected candidates and resumes reusable evaluator state where valid. [Hyperband][hyperband].
- **Given** one genome evaluated at several fidelities or repetitions, **when** outcomes are stored, **then** sample identity includes fidelity and replicate information; low-fidelity cache entries cannot satisfy full-fidelity requests.
- **Given** noisy or poorly correlated early scores, **when** promotion decisions are audited, **then** an exploration allowance protects late improvers and only independently confirmed full-fidelity outcomes qualify for deployment.

Deliverable: a resource-aware promotion scheduler and fidelity-specific sample records. This extends the existing fixed cascade rather than renaming it.

### US-16 — Adapt islands and restart stalled searches

**Priority:** P1. **Owner:** Search maintainer. **Depends on:** US-05, US-07, US-08.

As a user, I want search to escape unproductive regions so that a long run continues exploring alternatives.

- **Given** islands with different recent valid gains and diversity, **when** resource allocation updates, **then** productive islands receive more proposals while a minimum budget preserves exploration.
- **Given** stagnation over a declared window, **when** a restart triggers, **then** the policy preserves verified elites and seeds a bounded new exploration phase without silently discarding the best result.
- **Given** heterogeneous island operators or membership changes, **when** a checkpoint is restored, **then** policy state, island identities, migration history, and allocation decisions are restored or the incompatible checkpoint is rejected.

Deliverable: adaptive allocation and restart policies, initially using a fixed island pool. Dynamic island creation is a later option with explicit checkpoint semantics. [Adaptive-search reference][skydiscover].

### US-17 — Build a compiler-guided program improvement loop

**Priority:** P1; first consumer feature slice. **Owner:** AiDotNet program-evolution maintainer. **Depends on:** US-03, US-05; US-08 adds attribution.

As a developer optimizing an algorithm, I want planned edits and bounded repair so that more proposals become correct, useful programs.

- **Given** an initial program and measured bottleneck, **when** an improvement is proposed, **then** the consumer records a testable hypothesis and applies syntax-aware edits to an isolated candidate snapshot; begin with C# and extend to coordinated multi-file changes.
- **Given** a compilation failure or failing search-visible test, **when** repair is allowed, **then** diagnostics guide a bounded repair loop, all model/build costs are charged, and hidden evaluation data remains unavailable.
- **Given** a valid patch, **when** it is promoted, **then** the build, public API contract, correctness tests, and held-out performance result correspond to the exact same source/dependency fingerprint.

Deliverable: plan/edit/build/test/repair stages and patch artifacts in the consumer, with interfaces for alternative compilers. Reuse existing consumer implementations where available; the core remains language-agnostic. [Reasoning-loop reference][loongflow].

### US-18 — Reuse experience and detect meaningful novelty

**Priority:** P1. **Owner:** Consumer maintainer; generic storage/selection contracts as needed. **Depends on:** US-03, US-05, US-08.

As a user, I want relevant successes and failures to inform later proposals so that the search does not repeatedly rediscover the same mistakes.

- **Given** an evaluated proposal, **when** an experience record is retained, **then** its hypothesis, source hash, outcome, diagnostics, applicable task/version, and evidence are stored with bounded retention and retrieval rules.
- **Given** a new proposal, **when** relevant experience is retrieved, **then** selected lessons and diverse examples fit a fixed context budget; cross-run reuse is opt-in and excludes final-test information and incompatible task assumptions.
- **Given** near-identical syntax or behavioral fingerprints, **when** novelty is scored, **then** similarity is treated as a heuristic: equivalent outputs can still have different performance, and useful small edits are not automatically discarded. Only proven identity under the evaluation contract permits cache reuse.

Deliverable: a structured experience store, budgeted context builder, and optional syntax/behavior/embedding novelty adapters. Extend the existing structural distance and artifact facilities. [Distance contract][distance], [artifact delivery][cascade-engine], [experience reference][loongflow].

### US-19 — Route models and prompts by measured return

**Priority:** P1. **Owner:** Consumer LLM maintainer. **Depends on:** US-05, US-08, US-17.

As an operator paying for model calls, I want the system to choose an effective model and prompt so that better results do not require using the most expensive model for every proposal.

- **Given** an approved model/prompt portfolio, **when** a proposal is assigned, **then** routing considers valid gain per cost and exploration, with versioned configuration and explicit spending limits.
- **Given** cheap-model failures or a difficult repair, **when** escalation criteria are met, **then** the consumer may use a stronger configured model, recording the escalation and its incremental cost.
- **Given** changing model behavior or provider failures, **when** routing state updates, **then** failures remain part of the accounting, stale reward estimates are handled explicitly, and fixed-model baselines remain available.

Deliverable: a bandit-style model/prompt router with response provenance and replay support. Prompt selection or evolution uses development data; hidden-test scores do not train the router. [Adaptive-model reference][shinka-paper].

### US-20 — Overlap proposal generation and evaluation

**Priority:** P1. **Owner:** Core scheduler maintainer. **Depends on:** US-05, US-08, US-10.

As a user running slow model-backed operators, I want independent proposal and evaluator capacity so that available workers are not starved by serial generation.

- **Given** slow proposal generation and available evaluator slots, **when** pipeline mode runs, **then** separate bounded generation/evaluation queues apply backpressure, rate limits, cancellation, and resource reservations.
- **Given** concurrent generation, **when** a proposal is dispatched, **then** it receives an immutable parent/context snapshot and stable identity; stateful operators remain serialized unless they explicitly support deterministic concurrent use.
- **Given** a recorded deterministic schedule and external responses, **when** a run is replayed, **then** commit order and operator updates reproduce that schedule; an opportunistic mode explicitly records its different semantics.

Deliverable: an opt-in pipeline with utilization and queue metrics. Continuous evaluator dispatch already exists; the new capability is concurrent preparation/proposal work. [Current dispatch][continuous].

### US-21 — Make external and distributed evaluation durable

**Priority:** P1; identity contracts before broad ask/tell adoption. **Owner:** Core session maintainer and worker-adapter maintainer. **Depends on:** US-03, US-05; builds on PR #14.

As a user evaluating on remote CPUs, GPUs, or another runtime, I want recoverable work delivery so that crashes and retries cannot corrupt a run.

- **Given** distinct genomes with identical display text, **when** work is canonicalized, **then** an injected canonicalizer/codec preserves their distinct identities and caller-supplied task/evaluator fingerprints guard compatibility.
- **Given** a worker lease expires and an attempt is retried, **when** an old or duplicate result arrives, **then** run, evaluation, and attempt/lease identity prevent stale results from updating the replacement attempt or charging it twice.
- **Given** a worker or coordinator restarts, **when** work resumes, **then** durable pending work, leases, reservations, and committed results reconcile; compatible workers are selected using declared hardware/resource requirements.
- **Given** a request for exact continuation across a partial logical batch, **when** that mode is supported, **then** pending proposals, operator state, and external-response provenance survive; otherwise continuation is explicitly labeled as a fork with different trajectory guarantees.

Deliverable: first harden the local session contract, then add an optional durable coordinator/worker protocol with heartbeat, cancellation, and idempotent commit. NativeAOT/C ABI and TypeScript/Python bindings build on this protocol; an in-memory ask/tell queue alone does not provide a distributed service. [Reviewed PR implementation][session-pr].

### US-22 — Bound diversity search with many descriptors

**Priority:** P2. **Owner:** Archive maintainer. **Depends on:** US-04, US-10.

As a user searching across many behavior dimensions, I want a fixed-size repertoire so that useful diversity remains affordable as descriptors increase.

- **Given** many descriptor dimensions and a capacity of K cells, **when** a centroid/Voronoi archive is selected, **then** it retains at most K elites with deterministic nearest-centroid assignment and tie handling. [CVT archive reference][cvt].
- **Given** normalization or centroid definitions change, **when** existing entries are remapped, **then** the change is versioned, transactional, and visible in checkpoints and reports.
- **Given** the same memory and evaluation budgets, **when** it is compared with the current sparse grid, **then** quality and diversity are assessed on a common reporting basis.

Deliverable: an optional centroid archive. The current grid is already sparse and capacity-bounded; this proposal addresses partitioning and representation, not a presumed dense-allocation bug.

### US-23 — Warm-start compatible searches and reuse evaluations

**Priority:** P1. **Owner:** Core integration/consumer maintainer. **Depends on:** US-03, US-05, US-06.

As a repeat user, I want compatible previous discoveries to accelerate new searches so that I do not pay to rediscover or reevaluate the same work.

- **Given** an exported repertoire, **when** a new run imports seeds, **then** genome schema, constraints, task semantics, and provenance are validated; incompatible fitness is discarded and candidates are reevaluated.
- **Given** a persistent evaluation cache, **when** reuse is requested, **then** its key covers canonical code/genome, task/evaluator, data, fidelity, compiler/runtime, hardware, and correctness policy as applicable.
- **Given** stochastic or timing-based outcomes, **when** records are reused, **then** sample counts, uncertainty, and freshness are preserved and new measurement remains possible; cache hits are not independent samples.
- **Given** a benchmark using warm starts, **when** systems are compared, **then** both receive equivalent admissible prior information and reports distinguish cold-start cost from the cost of building and reusing prior knowledge.

Deliverable: repertoire import/export plus pluggable persistent evaluation storage, separate from the engine's run-local memoization and consumer deployment caches. [Current cache boundary][readme].

### US-24 — Provide a practical run and inspection experience

**Priority:** P1. **Owner:** Tooling maintainer. **Depends on:** US-04, US-05, US-11; external control builds on US-21.

As an adopter, I want a CLI and inspection interface so that I can configure, understand, and control an optimization run without writing a custom application.

- **Given** a task adapter and configuration, **when** a run is launched, **then** preflight validates dependencies, evaluator availability, budgets, output locations, and a seed's correctness before expensive search begins.
- **Given** a running experiment, **when** it is inspected, **then** best valid progress, archive diversity, lineage, model/operator choices, queues, errors, and cost are visible, with pause/resume/cancel at documented boundaries.
- **Given** a completed run, **when** a winner or comparison is exported, **then** code/genome, configuration, environment fingerprints, and validation evidence accompany it without exporting credentials.

Deliverable: a CLI first, then an optional local dashboard. Keep these outside the core NuGet dependency graph. Monitoring is supporting functionality, not evidence of better optimization. [Existing tooling reference][shinka].

### US-25 — Promote validated results and retune when conditions change

**Priority:** P1 for consumer adoption. **Owner:** Consumer/deployment maintainer. **Depends on:** US-03, US-06, US-09 where multiple objectives apply, US-23.

As an application owner, I want optimized results to enter use under explicit policies so that discoveries create sustained value.

- **Given** a validated candidate, **when** deployment is requested, **then** the consumer packages the exact artifact with its applicability envelope and compares it with the current implementation under a configured promotion policy.
- **Given** a material runtime, device, compiler, dataset, or workload change, **when** applicability is checked, **then** incompatible results fall back to a known valid implementation and trigger bounded revalidation or retuning.
- **Given** a promoted candidate regresses on monitored workloads, **when** the rollback condition is met, **then** the consumer can restore the prior artifact and retain the regression evidence for future search.

Deliverable: artifact registry integration, applicability checks, and promotion/fallback/retuning hooks in consumers. Automated production promotion requires an explicit application policy; benchmark success alone is not authorization.

### US-26 — Experiment with evolution of search policies themselves

**Priority:** P2 research, opt-in. **Owner:** Search research maintainer. **Depends on:** US-04, US-05, US-07, US-08.

As a researcher, I want to optimize search policies across tasks so that the system can discover better combinations than manually tuned presets.

- **Given** a bounded declarative policy space, **when** meta-search runs, **then** it can vary operator mixtures, selection schedules, restart rules, and context policies within fixed contracts and budgets.
- **Given** policies selected on development tasks, **when** a candidate policy is assessed, **then** independent task families and strong manually tuned baselines determine whether it generalizes; all inner and outer search costs are reported.
- **Given** a policy is invalid, expensive, or unstable, **when** it is tested, **then** execution limits contain it and a stable preset remains available; evolving arbitrary core executable code is outside the initial design.

Deliverable: an offline policy optimizer. This is a longer-term experiment, not a prerequisite for useful mutation/model adaptation. [Meta-evolution reference][skydiscover].

## Suggested delivery sequence

The expanded backlog is a set of options, not a promise to deliver all 26 stories in 90 days. Prioritize complete user workflows over implementing every algorithm at once.

| Phase | Measurement workstream | Functionality workstream | Exit evidence |
| --- | --- | --- | --- |
| First 30 days | A small versioned suite, strong simple baselines, correctness checks, and cost ledger from US-01–US-05. | US-08 feedback foundation plus one complete initial workflow: US-17 C# program improvement for the stated code-optimization objective; US-13 is the alternative first slice for numeric tuning. Include a minimal US-24 CLI. | A user can run a real optimization, inspect a valid improvement or explicit no-improvement result, and reproduce its evaluation. |
| Days 31–60 | US-06/US-07 robustness and feature comparisons; US-10 throughput measurements. | Add US-19 model routing and US-18 focused experience reuse to the program workflow. Address US-20 if proposal latency is a measured limit; US-14/US-15 suit expensive numeric/model evaluation. | A selected feature improves quality, success, or cost against the same baseline with full failure accounting. |
| Days 61–90 | An independent competitor campaign and scoped US-12 release report. | Choose the next practical constraint: US-21 remote execution, US-09 tradeoffs, or US-23/US-25 reuse and deployment. Harden selected capabilities rather than starting every remaining story. | A reproducible consumer release with explicit applicability, fallback, and supported competitive claims. |
| Later research | Broader cross-task validation. | US-22 high-dimensional archives and US-26 meta-search; expand adaptive islands and other operators when evidence supports them. | Demonstrated incremental benefit over the best simpler system. |

These are sequencing estimates; staffing, existing consumer code, evaluator preparation, model access, and hardware determine the actual schedule. Numeric optimization and program evolution can share core contracts while shipping through different integrations.

Every implemented core feature must preserve the documented target frameworks and dependency boundary, add relevant compatibility/versioning and deterministic regression checks, and have an opt-in migration path when it changes search semantics. Consumer features must first inventory existing implementations. Feature graduation requires a demonstrated user benefit, not merely another configuration flag.

[readme]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/README.md
[engine]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionEngine.cs
[tests]: https://github.com/ooples/AiDotNet.Evolution/tree/f0f282cf2e9027ab5278a1953493c805d6ab52ad/tests/AiDotNet.Evolution.Tests/UnitTests
[quality-tests]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/tests/AiDotNet.Evolution.Tests/UnitTests/EvolutionSearchQualityProofTests.cs
[evaluation]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionEvaluation.cs
[trace]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionTraceSummary.cs
[ci]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/.github/workflows/build.yml
[coverage]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/coverage-baseline.json
[cascade]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Configuration/EvolutionCascadeOptions.cs
[evaluation-loop]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionEngine.Evaluation.cs
[archive]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/MapElitesArchive.cs
[ordering]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionEntryOrdering.cs
[selection]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Enums/EvolutionSelectionPolicyKind.cs
[openevolve]: https://github.com/algorithmicsuperintelligence/openevolve/tree/411fb59c886c18704caaffb611e17cf9e7d824d2
[oe-config]: https://github.com/algorithmicsuperintelligence/openevolve/blob/411fb59c886c18704caaffb611e17cf9e7d824d2/openevolve/config.py
[oe-algotune]: https://github.com/algorithmicsuperintelligence/openevolve/blob/411fb59c886c18704caaffb611e17cf9e7d824d2/examples/algotune/README.md
[oe-symbolic]: https://github.com/algorithmicsuperintelligence/openevolve/blob/411fb59c886c18704caaffb611e17cf9e7d824d2/examples/symbolic_regression/README.md
[pyribs]: https://github.com/icaros-usc/pyribs
[qdax]: https://github.com/adaptive-intelligent-robotics/QDax
[algotune]: https://github.com/oripress/AlgoTune
[algotune-site]: https://algotune.io/
[variation]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Interfaces/IVariationOperator.cs
[cascade-engine]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionEngine.Cascade.cs
[continuous]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionEngine.Continuous.cs
[options]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Configuration/EvolutionEngineOptions.cs
[distance]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Interfaces/IGenomeDistance.cs
[session-pr]: https://github.com/ooples/AiDotNet.Evolution/blob/cf631f38206fa9a6048bff1e59d45d7467066452/src/AiDotNet.Evolution/Core/EvolutionSession.cs
[shinka-paper]: https://arxiv.org/abs/2509.19349
[shinka]: https://github.com/SakanaAI/ShinkaEvolve/tree/9912af12d423504b8d580f4179fd15f5f88b8c50
[skydiscover]: https://github.com/skydiscover-ai/skydiscover/blob/700711ec4c37d0be677456f66d38461310c0bcf8/README.md
[loongflow]: https://github.com/baidu-baige/LoongFlow/blob/945c78bc1554f8281aac40320b3599bd68d528d7/README.md
[cvt]: https://docs.pyribs.org/en/stable/api/ribs.archives.CVTArchive.html
[hyperband]: https://arxiv.org/abs/1603.06560
[snapshot]: https://github.com/ooples/AiDotNet.Evolution/blob/f0f282cf2e9027ab5278a1953493c805d6ab52ad/src/AiDotNet.Evolution/Core/EvolutionArchiveSnapshot.cs
