# US-17 verification evidence

`verification.zip` retains local build/test/format/pack output and raw authored-fixture execution evidence.
Production source: `c4419bf`; later evidence/documentation commits do not change implementation.

Contents include the first failed build (`CS0411`, corrected by explicit `Work<T>`), first formatting failure,
successful full build, core test TRX, new-project tests and coverage, final affected-project rerun/pack/format logs,
and both initial/final framework examples. Nothing claims the first failed checks passed.

Core sources were unchanged by US-17: 792/719/719 tests pass for net10.0/net8.0/net471. Thirty new tests pass on each
modern framework, rerun at the production commit after formatting. Coverage (before whitespace-only formatting)
is 94.26% line/74.56% branch in CSharp and 96.04%/73.45% in Programs. Existing-core coverage collected incidentally
by the new-project suite is **not** used as the repository coverage denominator or as a coverage-ratchet waiver.

Every example retains source and patch plans, failed public correctness, exact emitted images, artifact-bound public
and final receipts, raw timing samples and final ledger. Each uses two proposals and 27 synthetic work units.
The supervisor verifies an independent enumeration oracle; no paid model/API call was made. Fixture-only timing
improvements are **not representative speedup estimates, a preregistered release claim or a competitor win**.

The `.dll` files inside the example evidence are authored fixture images only, never automatically loaded by this
report. The example itself is not a hostile-code security sandbox. See [trust boundaries](../../COMPILER_GUIDED_PROGRAMS.md).

`verification-followup.zip` adds the final **32-test** suite on each modern target (two automated process-example
regressions) and OpenCover/Cobertura output. It corrects the workflow omission that otherwise collected only core
coverage. Final rates: CSharp 94.40% line/74.56% branch, Programs 96.04%/74.69%, example 99.05%/68.91%.
These newer results supersede the 30-test counts above; both are retained as an auditable progression.

Archive SHA-256 and size are recorded in the PR after creation (not inside the archives themselves).
