# US-06 local verification

## Final fail-closed hardening

Archive size: 4,978,737 bytes; SHA-256:
`c017ffc4901e11d7d40519db67726f0001834a6d91a0abc40e79b5afeb05f2b4`.

`screening-hardening-verification.zip` preserves the final 68-test warm batch,
the passing 17-test screening correction, the previous-head red control, a repeated
real Docker ablation, the 126-container controller fixture and the core example.
The initial batch had one error: stopping search also blocked reserved audits.
The corrected lifecycle separates stopped search from consumed audits; all 17
affected screening tests pass. The other 67 warm tests passed in the initial batch.
There are no remaining local failures or skipped tests. Unchanged C# build,
format and non-warm suites retain their previous passing evidence below.

The four new regression methods reproduce 12 failed subtests and two errors
against `4680857`; all pass after the fixes. These cover individual invalid
timings hidden by a median, invalid full baselines, retry-until-lucky cheap
initialization and equality-coerced frozen policy types.

The repeated reject-heavy panel uses 51→46 containers and 24.299→15.668 CPU
seconds, with host elapsed 113.569→119.507 seconds. All three panels retain the
full-run best, with zero descriptive useful rejections. This repeats the cost
tradeoff, not a wall-time or competitor-win claim. No provider calls were made.

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
