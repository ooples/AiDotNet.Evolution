# US-06 local verification

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
