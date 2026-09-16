# US-12 verification

Implementation: e5c656a; explicit no-claim manifest1efe3c6; final ignore-security regression3838ddc. All changes are in AiDotNet.Evolution. No consumer feature PRs, live models, package publication, merges or approval/protection changes were performed.

`verification.zip` is956,359 bytes, SHA256`d5768bf8253c576dc1fea6ed2e1e8a1e7257dc88048ee7eb99f4f515d90eb4b8`. It contains raw local test/build/format logs, TRX files, release assessment and complete CPU smoke comparison records.

- Release solution build at1efe3c6:0 warnings/errors.
- Core tests:792net10,719net8,719net471;0 failed/skipped. No runtime C# implementation changed after that batch.
- Final Python analysis suite:66 passed, including16 US-12 adversarial/contract tests. Synthetic passing gate fixtures are not measured superiority evidence.
- Full solution format verification passed.
- CPU smoke:56 runs,1,792 evaluator calls, all completed. Includes8 real SciPy differential-evolution runs through the shared C# evaluator. Two public seeds and32-call budgets validate the scheduled command; the production schedule's8-seed/128-call setting is bounded but was not executed locally. This is development evidence, not an OpenEvolve or runtime-speedup claim.
- The actual release manifest produces an engineering-only no-claim assessment. Qualifying independent US-11 evidence is absent, not silently substituted by the contract tests or CPU smoke.

## Adversarial review

The implementation refuses altered/missing/duplicated rows, unfair/unreconciled cost, unsafe bundle paths, changed source hashes, reused work receipts, missing correctness, reused search timings, bad fallback identity, post-hoc endpoints and same-commit registration. Out-of-support ratios fail instead of being clipped. Infrastructure failures remain explicit blockers; negative families and inconclusive intervals remain visible.

Prior Git registration, hashes and attested calibration cannot prove scientific honesty or unseen holdouts. Custody, pilot calibration, independent oracles and complete cost conversion remain required review responsibilities. The gate documents that boundary rather than promoting a self-authored evidence declaration into an authenticity guarantee.

The inherited generic Release-directory ignore rule initially omitted the new manifest. A root-directory exception fixes tracking without overriding private-key exclusions; a regression test verifies that `release/private_key.pem` remains ignored. Original security/engineering release jobs and publish dependencies remain unchanged.

See [protocol and release checklist](../../RELEASE_EVIDENCE.md). Hosted current-head checks and independent review are separate from these local results. A green no-claim assessment is not permission to advertise superiority.
