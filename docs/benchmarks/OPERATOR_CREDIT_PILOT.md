# Operator-credit development pilot

Source: `0408a0d6ed2b428fd9c58c0d273078bce915b9cc`. This is descriptive development evidence, not held-out confirmation.

The [example protocol](../OPERATOR_CREDIT.md) compares two fixed mutation controls and four reward/cost policies on two
four-dimensional objectives, ten paired seeds and a whole-run cap of 128 synthetic units. Each pair shares all eight starting
genomes. All policies actually pay proposal and evaluation costs, including the evaluator-only credit controls. Setup costs 0.08,
small proposals 0.1, large proposals one and each true evaluation one synthetic unit. These prices are not measured runtime or model bills.
Every method stops at its first reservation denial; unused remainder differs and is retained rather than inventing extra measurements.

All **120 runs completed**, with **11,597 true evaluator calls**, **15,300.20 synthetic units**, zero unknown costs/maximum violations
and byte-identical replay. Raw JSON and replay are each 27,161,054 bytes; SHA-256
`110ea28be8ebbe68f17d84a8a8c5c49e88bab46449b0c0f440a2d2ef9bc1d5d2`.
Assembly SHA-256: `62603ff74976542b33e2beea6cc594652c9e36f6089294fa9ea1554373a32257`.
[Committed per-run evidence](operator-credit-0408a0d.json) retains outcomes, charges, attribution totals and replay/source hashes.
Full measurements, proposals and receipts remain in `TestResults/quality/operator-credit-0408a0d.json` and its `-replay.json` sibling.

Median final loss, lower is better:

| Policy | Smooth quadratic | Rippled quadratic |
| --- | ---: | ---: |
| Static small-step | 0.000411302 | 0.260280 |
| Static large-step | 0.0103141 | 0.201441 |
| Archive success / evaluator cost | 0.000420823 | 0.179525 |
| Archive success / proposal + evaluator cost | 0.000436779 | 0.203341 |
| Parent improvement / evaluator cost | 0.00333301 | 0.179525 |
| Parent improvement / proposal + evaluator cost | 0.000546659 | 0.211701 |

Fixed small-step search has the lowest smooth-fixture median. Including proposal cost helps parent-improvement credit on that
fixture but worsens both adaptive policies' rippled-fixture medians. Correct accounting is necessary for fair measurement; it is
not proof that one cost-normalized reward is the best search policy. These results reject treating the new policies as universally
better defaults. Keep them optional. No sample-size/power claim, interval-based winner declaration, production price ratio,
OpenEvolve/model comparison or broad transfer claim is made.

Reproduction:

```powershell
dotnet build examples/OperatorCreditSearch/OperatorCreditSearch.csproj -c Release
dotnet run --project examples/OperatorCreditSearch -c Release --no-build -- 10 128 new-credit.json
dotnet run --project examples/OperatorCreditSearch -c Release --no-build -- 10 128 new-credit-replay.json
```

Use the pinned source and same runtime/platform for the recorded hashes. Output files must not already exist. The exporter verifies
source metadata and raw/replay hashes before retaining the compact evidence; it does not turn a development pilot into a confirmatory study.
