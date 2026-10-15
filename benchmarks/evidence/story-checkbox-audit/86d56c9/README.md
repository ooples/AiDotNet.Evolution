# V1-01 (#108): what the open boxes actually are

Measured by [`eng/Measure-StoryCheckboxAudit.ps1`](../../../../eng/Measure-StoryCheckboxAudit.ps1) at main
`86d56c9`. `audit.json` is the script's output, unedited. **75 unchecked, 80 checked, 0 issues closed with
unticked boxes.**

The count alone invites the wrong conclusion - that 75 pieces of work are missing. This file records what
each unchecked box **is**, checked against current `main` rather than inferred from the issue text.

## Metrics now measured, from merged work

| issue | metric | measured | by |
|---|---|---|---|
| #21 | wrong-output fast candidates promoted | **0 of 10** (10 of 10 before the fix) | `MutantPromotionSuiteTests`, #259 |
| #21 | finalists independently verified | **4 of 4** finalist paths retain evidence | `FinalistVerificationCoverage`, #264 |
| #22 | report regeneration diff | **0 bytes** for the published performance tables | `eng/Test-EvidenceTables.ps1`, CI `build.yml:177` |
| #24 | false gains from re-measuring unchanged code | **0** at the shipped default; **8 of 10** with a bounded capacity | `RemeasurementGainSuiteTests`, #263 |
| #29 | oracle false-accept on seeded mutants | **0 of 36** | `kernel_family.oracle_mutant_suite`, #262 |
| #38 | pipeline vs sequential wall-clock | **2.9473x** on authored delays - **target names real models, so unmet** | `benchmarks/evidence/pipeline/9c3441d` |
| #38 | ZeroLatency profile | **0.4682x**, met by "or auto-disabled" - the pipeline is off by default | same |
| #43 | promoted winners regressing the incumbent | **0 of 7** | `PromotionRegressionSuite`, #261 |
| #170 | end-to-end LLM examples | **21 of 21 documented**, met by the exclusions half | `OpenEvolveExampleLedgerTests` |
| #170 | examples with committed real-run evidence | **4 of 4 - met** | `benchmarks/evidence/openevolve-examples/` |

## Criteria that turned out already satisfied and CI-enforced

Checked this cycle, each previously reading as open work:

- **#119** - the Python binding passes the shared conformance scenarios: **15 tests, 29 subtests** against
  `conformance/durable-v1/scenarios.json`, and `typescript-binding.yml` already runs them **twice**, from
  source and from a built wheel.
- **#121** - `benchmarks/analysis/design.py` already implements the power analysis (`plan_design`) and the
  frozen pre-registration (`freeze`), with 14 tests including a declared-power bound check and monotonicity
  in effect, power and multiplicity. It is blocked on the pilot's variance input, not on the instrument.
- **#41** - `EvolutionRepertoireTests` already covers the warm-start criterion: fitness is always
  discarded, a changed evaluator or constraint revalidates, an incompatible task or schema requires
  explicit migration, and every applicability facet participates in identity.

## What is genuinely left, and why

| blocked on | issues |
|---|---|
| a paid paired pilot with real models | #35, #36, #37, #41 |
| the pilot campaign itself (#116) | #116, and #121's input |
| the decisive gate campaign (#122) | #29 (first metric), #30, #122, #123 |
| a merge-and-review that has not happened | the Definition-of-done box on most stories |
| **CodeRabbit review, counted rather than assumed** | #163, #166, #167, #169, #38 each have at least one carrying PR that merged with **zero** reviews - its free OSS tier reports a passing check while posting *"Review limit reached"*, so a green check is not a review |

## What this evidence is not

It does not tick a box. The script reports and never edits an issue, and ticking is a judgement about
evidence that stays with the person doing the audit. Nor is 75 a measure of remaining effort: the table
above shows ten metrics now carrying measured values that the count still reports as unchecked, because the
boxes they belong to also require a merge and a review.
