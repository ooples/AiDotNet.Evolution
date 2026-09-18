# US-04: reporting the real US-02 program pilot

Historical single-pilot bridge. Its remaining reporting gaps were addressed by
the [completed four-block program study and final acceptance](PROGRAM_STUDY.md).
The measurements and test counts below remain the original bridge's evidence.

Sandbox failure handling was completed in US-02 PR #46, including the cross-track
failure latch and adversarial tests. It does not need a separate user story.

US-04 / issue #22 / PR #47 is the next recommended story because the existing
numeric analysis/planning path could not consume the real program-pilot schema.
Without this bridge, observed candidate speedups could be mistaken for repeatable
optimizer superiority or used to invent a sample-size estimate from timing repeats.

## Implemented acceptance

- **Given** a stopped US-02 pilot and its post-selection audit, **when** reporting
  runs, **then** the source/plan/audit bindings and selected identities are checked
  before JSON, Markdown and offline escaped HTML are emitted.
- **Given** a missing track, failed search or failed correctness audit, **when**
  results are summarized, **then** the scheduled row remains, failed selections
  use their measured original fallback, and unknown measurements remain unknown.
- **Given** original and selected timing samples, **when** performance is derived,
  **then** medians are verified and ratios recomputed; a supplied speedup is not
  trusted. Optimizer comparisons use deployed runtimes directly, not ratios with
  different noisy original-program denominators.
- **Given** one independent search run per task/method, **when** confidence or
  sample-size estimates are requested through this reporting path, **then** both
  remain null with an explicit insufficiency reason. Timing repeats and unrelated
  tasks do not become independent search seeds.
- **Given** unknown costs or unavailable trajectories, **when** the scorecard is
  rendered, **then** known cost subtotals retain unknown counts and unavailable
  curves remain unavailable, not fabricated from endpoint summaries.

## Actual evidence

The existing US-02 pilot produced 18 rows, all validated, 331,366 reported model
tokens and 161.18 search-evaluator wall seconds. No additional model calls were
needed to produce these reports. All four controlled/native comparison groups
are marked `insufficient-independent-search-runs`; none receives a confidence
interval, paired-search variance estimate, power guarantee or superiority claim.

Input provenance is the committed US-02 lossless archive and report hashes in
[REAL_PROGRAM_PILOT.md](../us02/REAL_PROGRAM_PILOT.md). Hash consistency is not
authentication: trusted evidence custody and externally retained hashes remain
required. This bridge does not silently convert a program experiment into the
numeric planner's bounded utility protocol.

## Reproduce

```powershell
python benchmarks/analysis/program_report.py --pilot <US02-pilot-directory> --audit <US02-audit-directory> --output <new-directory>
python -m unittest discover -s benchmarks/analysis -v
```

The output directory must be new. The existing CI analysis gate discovers the new
regression tests. Tests challenge missing/duplicate tracks, wrong input/audit/source
bindings, fabricated medians, failed selection fallback, unknown costs and timing
pseudoreplication. The original numeric reporting/design tests remain unchanged.

## Local verification

One final Release solution build passed with zero warnings/errors. Core tests:
654 net10.0, 608 net8.0 and 608 net471 passed; formatting passed. The full analysis
batch passed 48 tests; the final unchanged-source/noisy-timing adversarial repair
was covered by a targeted 13-test program-reporting gate (49 distinct analysis
tests now). No second full build or model calls were needed.
The final reporter reproduced the committed 18-row JSON exactly after that repair.
[Current core TRX results](program-report-tests.zip), SHA-256
`0690d2af4d748f255803c895815c122597bfc4cd52fd89ebbcedcfda3d3504ce`.

[Generated JSON](program-analysis/report.json), [Markdown](program-analysis/report.md)
and [offline HTML](program-analysis/report.html) retain the real pilot's 18 rows,
four descriptive comparisons and explicit inference refusals.

## Remaining empirical acceptance

This completes the single-seed program-reporting bridge, not the full empirical
study. Repeated independent program-search calibration, a program-specific frozen
design and complete held-out execution remain. Complete progress curves require
an independently reconciled trajectory importer, not just endpoint summaries.
US-04's existing numeric fixed-design implementation is retained; no duplicate
planner, arbitrary extra model budget, release claim or automatic issue closure
has been added. Review and merging remain with the user.
