# US-04 final verification

Latest: [completed real-program study and acceptance](PROGRAM_STUDY.md), including
72 audited selections, 132 model calls, 85 analysis tests and verified archives.
The numeric campaign evidence below is retained unchanged as historical evidence.

Implementation/build source: `1c4d6b49dbac27d71b23e72481c2165e01ed9ab5`.
Stacked on US-02 #46, retaining US-01 #45 and foundation #15.

| Final local gate | Result |
| --- | --- |
| Release solution build | 0 warnings, 0 errors; 10.33 seconds |
| net10.0 | 654 passed, 0 failed/skipped |
| net8.0 | 608 passed, 0 failed/skipped |
| net471 | 608 passed, 0 failed/skipped |
| Python reporting/design contracts | 36 passed |
| Full solution formatting verification | Exit 0 |
| Coverage ratchet | 90.68% line / 76.20% branch; minima 88.80% / 73.51% |
| Actual numeric pilot | 48 runs, two seeds, four tasks, six methods |
| Frozen actual numeric execution | 1,320 completed runs, 55 fresh seeds, same four tasks/six methods, 16 evaluations each |
| One-use lifecycle | Repeat refused before dispatch, raw results byte-unchanged |
| Adverse report fixture | Missing/failed runs retained; explicitly dropped trajectory flagged without losing independent work |

One final build and test batch was used. No failing gate required a rebuild. The
19 new Python cases and six net10 cases cover bounded planning, degeneracy,
infeasibility, schema validation, source/runtime/hash tampering, exact fresh seed
execution, repeated-use refusal, failure schedules, paired denominators, dropped
traces, known-only AUC, target censoring and escaped offline HTML/SVG.

## What the experiment establishes

The chosen demonstration effect is 0.8 utility units, alpha 0.05, target power
0.8. This intentionally large effect keeps a contract demonstration bounded; it
is not a recommended practical threshold. The pilot normal approximation proposed
two runs, while the selected conservative design required 55 per task/method.
Raw plan, seed schedule, registration, measured rows, output reports and artifact
identities are retained. The actual comparison is development-only and does not
establish an OpenEvolve win, held-out improvement, or unseen-task generalization.

All scheduled final runs were accounted. The synthetic adverse copy deliberately
removes one pilot run, marks one failed, and marks one trace dropped; it is labeled
separately and does not alter the successful raw campaign. No candidate code,
model calls, paid services, merges or publication were used.

The fixed controller supports numeric v3; legacy numeric v4 remains reportable.
New US-01/US-02 suite/program schemas are not silently imported or called official
AlgoTune scores. Unmeasured correctness, runtime, memory and common-grid QD fields
stay null. The one-use filesystem contract requires trusted custody and an
externally retained registration hash. US-03 correctness/isolation and competitive
confirmation are separate dependencies, not proven by this reporting gate.

`verification.zip` contains the raw logs, TRX/coverage, pilot, fixed design,
registration, real campaign, machine-readable/Markdown/HTML reports and adverse
fixture. Hosted CI and human review are separate from these local results.

Archive: 5,150,912 bytes; SHA256
`af04a81de33160f6aec99c06fef97bf52246685f67ecb28f48c1d7754de348cb`.
All 40 ZIP entries passed CRC verification, with no duplicate paths.
