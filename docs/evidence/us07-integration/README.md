# US-07 v2 integration evidence

Archive: 44,490,854 bytes; SHA-256:
`4c17de6bcd9467cc7fb083a6926be79a77f0ff5ad6311b4e65419ebfb272eb13`.

`verification.zip` preserves both the initial failed nullable-quality build and
the final passing build/tests/format, pinned binaries, registration, one-use claims,
all raw observations, configuration outcomes, frozen nomination and scorecard.

Registration SHA-256:
`2cc280c967d6c327041c9ea3aa9679e5b56d950afa2639c6ec835f6c907a0c1a`.

- Development: 1,824 runs, 116,736 evaluator calls, zero failures.
- Confirmation: 6,144 runs, 393,216 evaluator calls, zero failures.
- Verification: 2,037 .NET tests and 104 Python analysis tests, no failures/skips.
- No live provider calls, paid APIs, changed defaults or competitor win claims.

Extract `us07-integration-final/scorecard.json` for all 54 before/after comparisons
and the generated guarded presets. Raw per-run observations and timing receipts
are in `study/development.json` and `study/confirmation.json`. The full matrix and
all negative/inconclusive results are retained; the three development nominees
are not cherry-picked as confirmed winners.

[Given/When/Then and measured summary](../../evolution-stories/US-07.md) ·
[Reproduction protocol](../../../benchmarks/analysis/ABLATIONS.md).
The preserved used registrations must not be replayed as new confirmation.
