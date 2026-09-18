# US-08 verification, experiment and adversarial review

Core [#51](https://github.com/ooples/AiDotNet.Evolution/pull/51), based on US-07 #50; consumer companion [AiDotNet #2212](https://github.com/ooples/AiDotNet/pull/2212), based on consumer US-06 #2210. No Tensors source change is needed.

[verification.zip](verification.zip): 9,172,657 bytes; SHA-256 `50609943dbb87add0978b0f90c319b703adfb045d21d7071909b3f77ea1f454f`.

## Final validation and provenance

- Notification/study implementation: `f4d88f2`. The registered study pins the actual binary hashes at this implementation, before the final legacy-default reward guard below.
- Final production core: `8d28a72d72bf48cb75ceadc6a70a77844e657ac7`. Build 0 warnings/errors; **792 net10 / 719 net8 / 719 net471** tests pass, no skips. Formatting passed. Modern coverage **90.84% / 76.07%** and **92.12% / 78.11%** line/branch, above unchanged gates. net471 coverage collector unavailable; tests passed.
- Python analysis: **50 tests** passed, including corrupted costs/rows, changed criteria, static comparator selection and one-use claims.
- Consumer: **83 compiler/portfolio + 954 integration tests per net10/net8**, no failures/skips. Its [evidence](https://github.com/ooples/AiDotNet/blob/feat/evolution-us-08-portfolio/docs/evidence/us08/README.md) preserves build warnings and exact local/pinned-CI provenance.
- The final guard changes only the legacy no-explicit-policy reward path and compatibility/diagnostic identities. The study uses explicit reward policies. Its observations were **not rerun** or relabeled as measured under the later binary; the archive keeps both test batches and original registration.

## Fixed-budget comparison

| Partition | Runs | Proposal dispatches | Actual objective invocations | Charged service units | Failed runs |
| --- | ---: | ---: | ---: | ---: | ---: |
| Development | 576 | 29,931 | 43,727 | 73,658 | 0 |
| Different-task confirmation | 576 | 30,113 | 43,563 | 73,676 | 0 |

Four static operators and two adaptive reward policies, three task families, 32 paired seeds per partition. Refiner inner evaluations are actual objective invocations and remain charged. The comparator is the **best static method selected on development**, not a weaker convenient baseline. All six confirmation comparisons retain the fixed 0.02 useful-gain threshold and six-hypothesis correction.

| Family | Selected static | Adaptive parent mean gain | Adaptive archive mean gain | Promotion |
| --- | --- | ---: | ---: | --- |
| Numeric | Mutation | -0.024755 | -0.053209 | Neither eligible |
| Bounded expressions | Mutation | 0.003105 | 0.013752 | Neither eligible |
| CPU kernels | Restart | -0.000375 | -0.001454 | Neither eligible |

Every simultaneous lower bound was negative. **Adaptation remains opt-in**. Negative means and inconclusive bounds are retained; no equivalence, superiority or changed default is claimed.

The study uses different objectives from US-07 and separates development/confirmation seeds. These are local numeric, bounded interpreted-expression and trusted CPU-kernel tasks, not unrestricted model-generated programs. The driver directly uses real archive/metering/feedback contracts; full engine replay and consumer integration are separate tests. Kernel timings were collected while a consumer build was also active on the machine: they are not quiescent performance evidence or fresh-incumbent speedup estimates. A service-unit budget is not equal CPU time, dollars or FLOPs. See [protocol and scope](../../../benchmarks/analysis/PORTFOLIOS.md).

## Adversarial findings and fixes

1. `LastCredit` alone was only a mutable diagnostic view. Added per-commit detached notifications, evaluation/genome/attempt identifiers and pending cost access. Duplicate outcomes still fail; notifications are not replayed after restore.
2. A notification handler could otherwise reenter state mutation or make later handlers see another outcome. Mutation/checkpoint reentry is now rejected; handler failures are counted and isolated, and each callback receives the captured committed record. This is in-process notification, not transactional exactly-once external delivery.
3. Equal cost labels on separate consumer ledgers could multiply spending. New ledger-provider binding requires all arms and the evaluator to use the same live ledger; regressions and real facade tests pass.
4. Compiler repairs and failed proposals remain attributed and charged. The two-arm test initially exceeded a one-arm metadata allowance; increasing only the fixture's declared allowance fixed it without bypassing admission.
5. The older default reward constructor could credit infeasible, unmeasured or unknown-cost insertions, unlike explicit policies. The final guard rejects those cases; five new regressions pass on all three core targets. Compatibility identity was bumped to reject potentially invalid prior learned state.
6. Best-static selection is frozen before confirmation; failed runs stay in denominators; missing refinement work, duplicate notifications, altered criteria and consumed-registration retries are rejected.

User retains independent approving reviews and dependency-aware merging/publication. No protection changes, paid calls, merges or package publication were performed.
