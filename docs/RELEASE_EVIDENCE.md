# US-12 release evidence and regression gates

The checked-in `release/claim.json` currently declares **no competitive claim**. This permits an engineering release only after the existing engineering/security checks pass. It does not say the search beats OpenEvolve. Qualifying US-11 consumer evidence and its repository migration are still outstanding.

## Execution tiers

- Every PR keeps the existing deterministic quality/replay, budget, test, coverage and format checks. `Release evidence` adds adversarial claim-contract tests and validates the declared release claim.
- Weekly and manually dispatched CPU comparisons have an explicit default cap:8 seeds,4 numeric tasks,6 core methods plus SciPy differential evolution,128 evaluations per method/task/seed =28,672 evaluations. Pinned external dependencies, raw observations and failures are uploaded. These repeated public development seeds are not held-out release evidence. No model calls or new paid services run in CI.
- The automated release pipeline evaluates the exact tagged checkout's claim before packing (`pack` still requires `validate` and `legacy`). Complete declared bundles and the decision are retained as artifacts. This does not modify branch protections, approve reviews or publish a package during implementation.

## Competitive release protocol

Before running confirmation, calibrate the provisional useful-effect and support assumptions on separate pilot data. Commit the resulting `evolution-release-protocol-v1` JSON under `release/registrations/` in a separately reviewed commit. Freeze one primary endpoint, exact task IDs/families/definition hashes, independent paired seed set, exact implementation revisions, requested model and reported snapshot (use `unreported` when unavailable), hardware, cost metric/cap, oracle identity and regression limits. The release claim must name that ancestor commit/path and the exact protocol SHA256. A same-commit or modified protocol is refused.

Supported endpoints:

1. Runtime: resulting programs require at least1.10x geometric-mean speedup, a95% family-wise interval above1.0, and all task regression bounds. Every pair has equal **reconciled total search cost**, not merely equal iteration caps. Frozen runtime summaries are medians of a fixed number of fresh confirmation timings.
2. Target cost: at least20% lower candidate cost means competitor/candidate cost >=1.25, not1.20. Both sides must reach the same frozen target. Missing/censored targets block the claim rather than receiving invented zero cost.

For both, each task's tolerated regression is fixed in advance and cannot exceed10%. Each resulting/fallback program must have an independently produced correctness receipt. A failed improvement search retains the exact original valid program. Infrastructure failures remain in the complete task/seed grid and block claims.

## Statistics

Ratios are analyzed on the log scale under **predeclared bounded support**. Out-of-support values are refused, never clipped. A two-sided Hoeffding interval is computed across independent paired seeds. Tasks are equally weighted within each seed, preserving within-seed task correlation. The alpha budget0.05 is split across the aggregate, every task and every predeclared family. This is deliberately conservative; inadequate sample size yields inconclusive evidence, not a relaxed threshold. Results generalize only to the declared task set and seed-generating process, not an unseen task population.

All family results and task intervals remain in the assessment. An aggregate claim fails if a task violates its regression gate. Supported family diagnostics do not silently turn into an unrestricted release claim; the current publisher accepts only the complete declared-task-set endpoint or no claim.

## Complete bundle

`manifest.json` uses schema `evolution-release-bundle-v1` and an `artifacts` map from confined relative paths to SHA256. It must include exact `protocol.json`, `pilot.json`, `observations.json`, original and resulting source files, per-run work receipts and independent correctness receipts. Duplicate JSON keys, path escapes, altered files, duplicate pairs, missing runs, reused work receipts and mismatched run/program identities are refused. Inspection is bounded to32MiB per artifact and256MiB total.

Every work receipt records task/seed/system, source hash, all-stage cost components and total. Runtime receipts also contain raw confirmation timings and phase; target-cost receipts contain the frozen target definition/attainment. Oracle receipts identify source hash, correctness verdict, oracle and confirmation phase. Never omit repairs, failed proposals, compilation or evaluation work from the declared cost conversion. The gate verifies reconciliation; independent collectors and reviewers must verify that this declared conversion actually covers all work.

The schemas and complete synthetic examples are executable in `benchmarks/analysis/test_release_gate.py`. Those fixtures are **not scientific evidence**. Hashes, prior Git ancestry, a pilot-calibration declaration and receipt fields prove consistency, not honest measurement, chronological secrecy, oracle independence or adequate pilot design. Reviewers must verify protocol custody, those scientific assumptions and provenance before accepting a claim. Git commit order alone cannot prove that a researcher never saw the holdout.

```powershell
python -m unittest discover -s benchmarks/analysis -p test_release_gate.py -v
python benchmarks/analysis/release_gate.py --release release --output new-assessment.json
./eng/Run-BudgetedComparison.ps1 -Python C:/path/to/isolated/python -Seeds 8 -Budget 128
```

Output paths must be new. Invalid evidence produces an explicit failing assessment. No defaults, statistical thresholds or release-approval protections are weakened to obtain a green result.
