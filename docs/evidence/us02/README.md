# US-02 verification evidence

Core C# implementation/test build: `4f35ebd2e0dba803d13359a9b1d6a821b5b691bd`.
Final companion host: `9343d45907342eabd1f24cffa4724e7817855bf4`.
Python final-gate repairs are in the enclosing commit; no core C# changed after
the tested build. Raw host reports identify all three actual DLL hashes.

## Local final gates

- Full Release core solution: zero warnings/errors; format verification passed.
- net10.0: 648 passed; net8.0 and net471: 608 each; zero failures/skips.
- Coverage: 90.57% line / 76.07% branch, above 88.80% / 73.51% minima.
- Numeric: 15 Python tests passed; final provenance repair reran six suite tests.
  All nine families reconcile shared C# measurements; actual pyribs CMA-ME runs.
  Comparison fixture: 15 core / 9 external rows. Legacy replay: eight runs,
  256 evaluations, byte-identical replay.
- Program controls/broker: 11 tests; transport: seven; actual pinned OpenEvolve:
  one integration test. Final metered transport regression: seven passed.
- Actual six-track program fixture: all completed, 11 model calls / 17 evaluations;
  exact first controlled prompts, independent call/token/evaluation receipts.
- Companion host final rebuild: zero warnings/errors, 3.22 seconds. Its dependency
  build produced 2,843 existing library/generator warnings; these are not hidden.
- Representative suite and 17 analysis tests passed. The suite contains 54 numeric
  smoke runs, 11 original program validations, and registered lifecycle contract
  checks (18 selection / nine final rows plus repeated-final refusal).

## Failures retained, not reclassified as success

The first companion build suppressed project references before generator outputs
existed and failed CS0006. The full dependency build then exposed a wrong option
name in the new host; `EvaluationGracePeriod` repaired it without another library
rebuild. The first campaign exposed chunked HTTP framing rejected by the bounded
broker. Explicit bounded `ByteArrayContent` repaired transport. The second campaign
exposed repeated fixture proposals correctly rejected as duplicates. Deterministic
parent-dependent fixture proposals passed the unchanged evaluation-count gate.

Two tiny live ChatGPT-authenticated Codex generations requested `gpt-6-astra`.
Each reported 8,707 input tokens (6,784 cached, already included) and 19 output
tokens. First generation succeeded but wrapper cleanup failed with Windows
`WinError 32`; retaining the owned workspace repaired the wrapper and the second
gate passed. No API-key authentication or paid API fallback was used. Resolved
model snapshot and monetary cost remain unknown, not invented as exact or zero.

## Interpretation

Program campaign scores are constant, synthetic, and nonexecuting: they establish
adapter interoperability and accounting, not correctness, speedup or superiority.
Real experiments require a trusted isolated evaluator with hardware/library
identity. The model token cap detects and retains post-receipt overruns; it is not
an exact preemptive token cutoff. Registered competitor holdout integration,
equally budgeted tuning/selection studies and efficacy evidence remain outstanding.
No merges, publication, deployment, or review approval are implied by local passes.

`verification.zip` retains successful and failed logs, raw campaign receipts,
numeric replay, representative-suite records, coverage and TRX results. Subscription
receipts contain prompts/events and usage, never authentication files or secrets.

Archive: 1,741,259 bytes; SHA256
`648caee6650d0f57c8cf5926594832c5d949f92529e5a27abf15c2ff3cb39ab9`.
All 172 ZIP entries passed CRC verification, with no duplicate paths.
