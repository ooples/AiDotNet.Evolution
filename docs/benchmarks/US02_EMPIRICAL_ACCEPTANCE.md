# US-02 empirical acceptance: pilot versus proof

This is a continuation of issue #20 / PR #46, not a new story or a claim that the
roadmap is finished. All new implementation is in AiDotNet.Evolution. The live
pilot uses the Evolution-owned host at `d5d2aa2`; it does not silently import newer
story branches or prove their adaptive/Pareto/scheduling policies effective.

## What the real-program pilot can establish

- **Given** unchanged pinned upstream starting modules, **when** all six tracks
  propose Python candidates, **then** the same isolated evaluator checks actual
  outputs against an outside oracle and measures daemon start-to-finish time.
- **Given** a selected candidate, **when** fresh diagnostic inputs and a separate
  post-selection adversarial audit run, **then** correctness failures remain
  visible and require the original-program fallback, not a speedup claim.
- **Given** controlled and native-bounded tracks, **when** results are reported,
  **then** every track's actual tokens, evaluation work, settings, failed attempts
  and selected-source identity are retained. Equal declared caps are not described
  as equal actual cost; zero tuning is not described as tuned-native performance.

## Adversarial findings that constrain interpretation

1. **Wrong endpoint risk.** Fresh-process batch latency includes startup, unused
   imports, input parsing and serialization. Removing framework imports can help
   this endpoint without making the algorithm kernel faster. A solve-throughput
   study needs a separately defined, sufficiently work-dominated workload and an
   outside clock; candidate-reported nanoseconds are not a substitute.
2. **No population attribution yet.** Two proposals and one independent search
   run per task cannot establish whether a population/archive helps. One-shot and
   single-parent controls may match or beat population search. Report that result.
3. **No held-out-family claim.** All three tasks are development families, with
   custom disclosed input generation. Fresh instances and the adversarial audit
   are not a sealed final family partition or an official AlgoTune leaderboard.
4. **Unknown provider snapshot.** Same requested model and transport do not prove
   an identical resolved provider snapshot. Retain `unreported`, use balanced
   time blocks in further experiments, and do not certify an exact-version claim.
5. **Unmatched actual cost.** Prompt lengths, cached tokens, invalid programs and
   repeated source identities affect incurred work. Model tokens, elapsed wall
   time and evaluator work are distinct endpoints, not interchangeable money.
6. **Selection bias and timing noise.** Three repeated timings are one search-run
   observation, not three independent optimized solutions. Do not manufacture a
   confidence interval or post-hoc minimum detectable effect from this pilot.
7. **Feature attribution.** This host uses core MAP-Elites with fixed settings.
   Claims about US-08 adaptive operators, US-09 Pareto search, US-10 scheduling or
   US-17 optimization need explicit same-task feature-on/feature-off ablations;
   these are not established by a faster candidate from the baseline host.

## Remaining work, in order

- **Given** the pilot's raw outcomes and runtime profile, **when** an endpoint is
  chosen for the next campaign, **then** fix its workload, correctness distribution,
  practical effect threshold, cost endpoint and supported hardware before final
  data are observed. Keep cold-start latency separate from kernel throughput.
- **Given** all fixed methods and disclosed native tuning spaces, **when** a
  development-only repeated-search calibration runs, **then** use independent
  search runs in randomized/balanced blocks, charge tuning and failed attempts,
  and derive the final sample size from search-level variation, not timing repeats.
  Publish an explicit maximum run/call budget before dispatch; do not expand it
  until a favorable result appears or quietly consume API-key access.
- **Given** a prospective protocol and predeclared stopping rules, **when** it is
  frozen for final execution, **then** bind task/method/source/image hashes,
  independent seeds, family partition, paired analysis, failure handling and
  multiplicity/regression gates. Review the protocol before opening final data.
- **Given** the frozen methods, **when** the full final task/seed grid executes,
  **then** retain every attempt, apply independent post-selection correctness,
  report uncertainty and cost-quality frontiers, and fail closed on unknown work
  or missing controls. An inconclusive or negative result completes an honest
  experiment; it does not justify a competitive-superiority claim.
- **Given** a claim about a newer story's feature, **when** its feature-on/off
  comparison is performed, **then** bind both implementations and identical
  inputs/budgets, with the same failure-inclusive analysis. Do not infer the
  feature's benefit from unrelated program rewrites or synthetic harness tests.

These are outstanding US-02 empirical acceptance items, not completed checkboxes.
The user retains PR review and merge authority. No release, protection change,
paid API fallback or automatic issue closure is part of this work.
