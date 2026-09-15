# US-05 final verification

Production implementation: `acfa6659006652f7a9012e36f5ea51b8350001c4`.
Final built/tested revision: `2f5f33815fe631ae4fb4bb0e2b8e57c1afa5490e`
(test-only explicit LINQ reversal repair). Based on US-04 #47, retaining #46/#45/#15.

| Final local gate | Result |
| --- | --- |
| Release solution build | 0 warnings/errors; 10.22 seconds |
| net10.0 | 678 passed, no failures/skips |
| net8.0 | 632 passed, no failures/skips |
| net471 | 632 passed, no failures/skips |
| Reporting Python contracts | 36 passed |
| External numeric contracts | 15 passed, all nine suite families and actual pyribs |
| External replay | Eight external runs / 256 shared evaluations, byte-identical replay |
| Fixed-design regression | 48 pilot / 1,320 fixed numeric runs, repeated consumption refused |
| Coverage | 90.76% line / 76.23% branch; minima 88.80% / 73.51% |
| Full solution format verification | Exit 0 |

The first final build failed in two new test expressions: `.Reverse()` selected a
void in-place array extension on net8/net471. Explicit `Enumerable.Reverse` repaired
the tests; no production algorithm change was needed. The failed build log is
retained alongside the successful final gate. No per-edit test/build loop was used.

## Verified behavior

Twenty-four new cases run on all three targets. They cover overrun rejection across
all ten resource stages, full actual charges and admission shutdown, complete
per-stage totals despite zero receipt retention, failed/rejected retries, unknown
charges, deterministic wave selection under reversed submission/worker order,
transactional validation of invalid waves, pending/reentrant capture refusal,
paired envelope integrity, no overwrite and no spending erasure under reduced caps.

The existing engine-resume integration now uses the coordinated envelope and a
fresh checkpoint store. Its final ledger equals uninterrupted execution byte for
byte. Both cascade refund configurations retain four screening units and four
full-evaluation units despite different engine attempt counters. Stage totals do
not depend on retained trace/receipt detail.

## Boundaries

This is verified core infrastructure, not proof that arbitrary consumer producers
are fully instrumented. No companion source change is required for these generic
APIs; consumer model/compiler/device receipts, currency conversion and hard bounds
remain explicit integration responsibilities. Generic stages are tested with
bounded local callbacks, not paid model calls or production GPU workloads.

Batch reproducibility is conditional on identical starting ledger state and a
predetermined wave sequence. Checkpoint capture requires a paused engine and no
pending work; it is not a crash journal. Never resume an old envelope after
unjournaled external spending. Keep the expected checksum/latest-boundary identity
in trusted custody. File flush/rename does not guarantee portable directory fsync.
No merge, publication, deployment or competitive-superiority claim is made.

`verification.zip` retains build failures/successes, test logs/TRX, coverage, fixed
campaign reports and numeric replay. Hosted CI and human review are separate gates.

Archive: 5,893,301 bytes; SHA256
`af3298366ba3640fd0c65f9a01073e1b8c1c351c3a005ddbcd2d58982f74eb84`.
All 46 entries passed CRC verification with no duplicate paths.
