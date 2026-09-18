# Judge relocation verification

2026-09-17, Windows, .NET8 and .NET10. No live provider/model calls.

- Release solution build: zero warnings/errors.
- Solution formatting verification: exit0 (`--verify-no-changes`).
- Programs/compiler tests: **770 passed on each framework**, zero skipped.
- Deployment tests: **41 passed on each framework**, zero skipped.
- PR workflow contracts: **4 passed** on net10.
- Total: **1,626 local test executions passed**.
- Freshly packed, uniquely versioned package-only consumer: passed, including a
  scripted judge response and accounting assertion. No project references.
- Four archived original source/test blobs match their Git object hashes at
  `9cd7d5d6c366a483874024650d02901f69a1829c`.

`verification.zip` contains final build/test/package logs and five TRX reports.
The earlier 766-test intermediate run is not the final verification bundle.

Adversarial coverage includes scalar/objective minimization direction, unsupported
score units, normalized name collisions, measured-descriptor collisions, duplicate
JSON keys, nonnumeric/nonfinite scores, response size/depth, per-request retry cost,
weighted/decorated/failed panels, floating-point weight overflow, pre/post provider
identity drift, partial panel cancellation, fatal errors, and critique disclosure.

The `.65` package fixture score checks arithmetic and packaging only. These results
do not establish search efficacy, model quality, GPU speedups, or competitor superiority.
See [migration boundaries](../../migration/JUDGE_RUNTIME_MIGRATION.md).
