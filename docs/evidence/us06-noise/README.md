# US-06 local verification

## Product screening follow-up

`screening-verification.zip` (10,203,250 bytes), SHA-256:
`5a6a24960163accdae0eb48e0dee4e5af37cf438c3ae8097615b2ede9927d96e`.

- `us06-screening-final`: passing build/core/format/non-warm checks, failed v1
  calibration and the intentionally interrupted superseded integration fixture.
  Its warm log is incomplete, not a passing final result. `screen-v1-source.zip`
  preserves the superseded screening implementation for inspection.
- `us06-screening-v2`: passing 63-test warm suite, actual 126-container controller
  proof and registered public sorting comparison; `retention-regression.log`
  adds one passing targeted test. `retention-reconciliation.log` confirms the
  corrected retention metric matches every saved panel. `noise.log` reconciles
  the executable core example's 537 charged callbacks.

Total current passing coverage: 2,235 test executions across the declared frameworks
and Python suites. The first five-second permission-fixture timeout is retained;
the same assertions pass using the production 15-second limit. The dedicated
deadline test still uses five seconds. No production sandbox bound was changed.

See [measured trade-offs](../../SCREENING.md), including the failed calibration
and screening-on overhead controls. All data are public/local; no provider calls.

## Previous independent-confirmation verification

See [acceptance and limitations](../../evolution-stories/US-06.md).

`verification.zip` SHA-256:
`5049490a330639d86c1b62fe5932187032833d13899612837b20af4c98790296`.

Includes the final Release build/format logs, full test batch, initial failing
workflow TRXs and passing targeted correction, real sandbox receipts and complete
fake-provider warm-controller report/journal. `noise.log` is the executable core
policy example's JSON. No live model usage or competitive efficacy data.

Reproduction uses Release solution build, tests on all three supported frameworks,
Python unittest discovery in `benchmarks/analysis`, and external patterns
`test_program*.py`, `test_codex_transport.py`, `test_openevolve_adapter.py`,
`test_docker_sandbox.py`, `test_warm*.py`. Required pins/environment follow the
existing shared-program CI job. Run the core example with:

```text
dotnet run --project examples/ReplicatedEvaluation -c Release --no-build -- --noise-policies
```

The fixture uses three fixed confirmation pairs and fake provider responses to
limit verification cost. Production registration uses sixteen pairs. Do not use
the fixture's noisy wall times to claim superiority over OpenEvolve.
