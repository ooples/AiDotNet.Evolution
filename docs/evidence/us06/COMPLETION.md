# US-06 consumer completion work

This supersedes the earlier core-only handoff. US-06 is still the sole active story.

[AiDotNet companion #2210](https://github.com/ooples/AiDotNet/pull/2210) implements
`ProgramNoiseEvaluationSession`: correctness-gated fresh screens, automatic full-fidelity rejection audits,
independent incumbent confirmation, immutable options and persistent one-use batches.
It consumes this core PR's `e1a791c8d6549adc9ef0f5b40e1668ff7fb12dec` revision.

Consumer evidence: [full Given/When/Then proof and raw archive](https://github.com/ooples/AiDotNet/blob/feat/evolution-us-06-noise/docs/evidence/us06/README.md).
954 net10 + 954 net8 consumer tests passed; net471 consumer compiled. All 18 fixed real training/timing runs passed
accounting checks: 5,768 fresh RidgeRegression fits, 1,680 timing invocations and 6,608 correctness checks,
14,056 charged calls. Conservative ridge screening passed its predefined audit rule on 6/6 roots; aggressive ridge
and timing negative controls failed on 12/12. Six corrupt-report variants were rejected. No timing win was fabricated.

Core completion validation: **704/658/658 tests**, clean Release build/format, **90.84%/76.32% coverage**.
CodeQL's three integer-to-double multiplication annotations were fixed with floating-point denominators;
the current implementation's CodeQL and main CI Gate passed. These count arguments were already bounded,
but making the arithmetic domain explicit removes the analyzer ambiguity without changing the statistical policy.
[core-completion-validation.zip](core-completion-validation.zip)
SHA-256: `6df934a07638d14c7b1a1948a50549175f18b77a4dc94838e160101f76bcbc45`.

## Remaining gate

Matching-source consumer integration passed hosted CI. Ordinary package-path consumer jobs still resolve the older
published core, which lacks both inherited foundation and US-06 APIs. Those failures are retained in the consumer
archive, not bypassed or mislabeled. The implementation/acceptance experiment is delivered, but **issue #24 stays
open pending dependency/package integration and review/merge gates**. There is no permission here to merge or
publish the shared dependency chain. Do not move on to US-07 while this gate remains unresolved.
