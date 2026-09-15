# US-04 fixed-budget program study protocol

This finishes the program-specific reporting/validation path in US-04. It does
not promise competitive superiority or replace US-02's larger efficacy study.
No API-key calls, release, merge or protection changes are authorized by it.

## Prospective design

- Fixed task panel: base64 encoding, SHA-256 hashing and connected components,
  using verified original AlgoTune modules and the shared isolated evaluator.
- Six tracks per task: controlled Evolution/OpenEvolve/one-shot/single-parent;
  native-bounded Evolution/OpenEvolve. No native tuning or post-calibration
  method changes. The internal `aidotnet` label refers to the Evolution-owned host.
- Calibration search seeds: 101, 103. Fresh confirmation search seeds: 1009, 1013.
  Each block uses distinct search, diagnostic and adversarial-audit instance
  seeds. Task/track execution order is seeded; there is no concurrent measurement.
- Two model proposals per track except one-shot, with three timing repetitions
  per evaluator call. Maximum **132 subscription-backed model calls** across
  72 task/method/search-run cells. Timing repetitions are never independent runs.
- All sample counts are fixed before calibration. The purpose is **budget-limited
  estimation**, not a powered release test. No count is extended until a win occurs.
  Failed/missing cells remain in the scheduled grid; unknown work stops dispatch.

## Endpoint and uncertainty

Runtime is the Docker daemon's fresh-process batch duration, including startup,
imports, input decoding, solving and serialization—not isolated kernel latency.
The predeclared bounded utility is `1 / (1 + seconds / task_scale)`, with scales
0.30 seconds (base64), 0.22 (SHA-256), 0.35 (components). These scales were chosen
from the preceding development pilot, not the new confirmation data. An unchanged
or failed candidate uses the independently measured original-program fallback.
Unmeasured/unknown deployment outcomes remain unknown and prevent a complete CI.

Each comparator is paired to Evolution within the same task/search seed and mode.
Paired utility differences lie in [-1,1]. For T fixed tasks, C comparisons and n
independent search runs per task, the two-sided taskwise Hoeffding half-width is
`sqrt(2 * log(2*T*C/alpha) / n)`. A union bound covers tasks and comparisons;
equal-weight fixed-task means inherit that half-width, clipped to [-1,1].
Cross-task independence is not needed; independence and stationarity within each
task ARE assumptions. Shared provider/hardware drift can violate them. The
requested model is pinned but its resolved snapshot remains unreported, so this
is conditional estimation, not certified release inference or unseen-family power.

Calibration reports paired search-run variances, an explicitly advisory normal
approximation and the existing conservative bounded-effect planning formula,
using alpha0.05, power0.8 and anticipated utility effect0.05. The powered
recommendation is never truncated to the two-run budget. If infeasible, it stays
infeasible; the separately predeclared two-run estimation exercise can still
complete and honestly return wide/inconclusive bounds. Low observed variance
from two calibration runs is not evidence of zero population variance.

## Registration, reporting and adversarial acceptance

- **Given** the schedule, artifact hashes and expected registration SHA-256,
  **when** execution is requested, **then** the hash and artifacts are verified
  and a create-new consumption receipt is written before dispatch. Reuse refuses
  without model calls; artifact changes between blocks stop further execution.
- **Given** calibration results, **when** confirmation starts, **then** the
  advice and confirmation registration are written first. Both final seeds and
  counts were already bound by the original registration. No data-dependent
  change to methods, counts, scales, targets or final seeds is permitted.
- **Given** complete or truncated broker traces, **when** progress is rendered,
  **then** separately maintained attempt/token/evaluation counters remain intact.
  Dropped/reordered records or inconsistent counters suppress curves instead of
  fabricating missing progress. Admission also uses these independent counters.
- **Given** search progress, **when** target-cost information is reported,
  **then** the fixed utility target0.5 is labeled search-only; non-hits end at
  actual observed work, not unused budget. Unknown work remains unknown, and
  potentially informative early stops are disclosed.
- **Given** a partial block failure, **when** the study report is produced,
  **then** all scheduled cells remain and filesystem model/container receipts
  retain independently known costs and unknown attempts, even for the failed block.
- **Given** completed data, **when** reports are generated, **then** JSON,
  Markdown and offline escaped HTML/SVG include paired effects/intervals,
  win/tie/loss, every cell, actual costs, trace completeness and explicit unknown
  peak memory. A configured memory limit is not a measured memory peak.

Hash consistency requires trusted filesystem custody and an externally retained
registration hash; it is not authentication. The agent's adversarial review and
regression tests are not substituted for the user's independent PR review.

## Commands

```powershell
python benchmarks/analysis/program_study.py prepare --root <new-directory> --image <immutable-image> --upstream <AlgoTune-checkout> --openevolve <OpenEvolve-checkout> --dll <EvolutionComparison.dll> --codex <validated-subscription-CLI>
# Retain the printed hash independently before execution.
python benchmarks/analysis/program_study.py execute --root <same-directory> --registration-sha256 <retained-hash>
```

Omitting `--codex` uses scripted fixtures only; those must never be presented as
model efficacy. The frozen analysis and executable hashes bind the actual run.
