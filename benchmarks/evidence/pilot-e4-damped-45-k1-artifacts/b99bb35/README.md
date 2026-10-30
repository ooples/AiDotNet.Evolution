# E-4 (#347): the third pilot, with the task's data given to every arm

This uses the same 18-cell design as the re-pilot ([`../../pilot-e4-damped-45-k1/a8c0c1d/`](../../pilot-e4-damped-45-k1/a8c0c1d/)):

- 6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced.
- `damped_oscillator` dev partition, 45 iterations, a 63-call broker cap per cell, held-out seed 20261017.
- 600/200 selection rows; our champion rule at k = 1.

The seeds were drawn fresh at registration (`registration.json`), as every pilot's are. The one change, made in
b99bb35, applies to every arm:

- The system message in `config.yaml`, which all three arms read, names the input: `t = xs[0]`, drawn from [0, 6].
- Each valid evaluation returns an artifact listing the inputs and ranges, plus 8 **training** rows with the
  candidate's prediction and residual. Validation and held-out rows are never shown.
- OpenEvolve receives the artifact as `EvaluationResult` artifacts. Ours runs with `--retain-artifact-text true`, so
  the text reaches the prompt instead of `[artifact content withheld]`.

The CLI ran from a frozen copy built at b99bb35 (`F:\evo-pilot\cli-b99bb35`), so no test build could change it
mid-run.

## A stop, and the resume

The first run stopped at call 380 (`failed-call-380/`).

- The model's reply carried thinking but no visible text, so Claude Code added its own user turn:
  `[Your previous response had no visible output. ...]`, flagged `isSynthetic`, with no tool involved.
  The result reported `num_turns` 2.
- The transport refused the call as multi-turn, and that refusal closed admission. The cell in flight, and the 9
  after it, failed at 0 tokens (`cells-original-run.jsonl`).
- 8f8d931 makes the transport accept exactly that synthetic continuation, counted against `num_turns`.
  Any other user turn is still refused.
- The resume (`RESUME-NOTES.txt`) used the same registration and the same `ProjectionSha256` (`8a9764dc...`).
  It carried over the 8 completed cells unchanged and re-ran the 10 failed ones.
- The new rule fired once in the resume's 465 calls (`synthetic_continuations` in `receipts-compact.json`).

## Result

All 18 of 18 cells completed: 833 model calls and 2,847,367 tokens in the cells.

Held-out NMSE per cell (`rows.json`); 0 is the exact formula, `exp(-0.3 t) sin(2 t)`:

| Seed | Ours | OpenEvolve recommended | OpenEvolve matched |
|---|---|---|---|
| 2853926153 | 0 | 0 | 0 |
| 4238139272 | 0.0139 | 0 | 0 |
| 2219668636 | 0 | 8.1e-33 | 0 |
| 3015410414 | 0 | 0 | 0 |
| 3665334120 | 0.2409 | 0.2758 | 0 |
| 2128383920 | 0 | 0 | 0 |

- **14 of 18 cells reached the exact formula** (13 at 0, plus 8.1e-33).
- **The other three cells** are damped cosines with wrong constants:
  - Ours, seed 4238139272: `0.84 e^{-0.27t} cos(1.98t - 1.52)`.
  - Ours, seed 3665334120: `0.031 + 0.362 e^{-0.085t} cos(1.97(t - 3.86))`.
  - Recommended, seed 3665334120: `0.015 + 0.34 e^{-0.06t} cos(1.9(t - 3.88))`.
- **Our champion beat the seed in 6 of 6 cells.** In the re-pilot, where the model saw a scalar score only, it did
  so in 0 of 6. The first pilot had no champion rule.

Endpoints (`MeanDifference` is theirs minus ours in held-out NMSE, so positive favours ours; paired bootstrap;
`summary.json`):

| Comparison | Mean difference | 95% interval | One-sided p | Holm p | Superior |
|---|---|---|---|---|---|
| vs recommended | +0.0035 | [-0.0069, 0.0175] | 0.283 | 0.565 | No |
| vs matched | -0.0425 | [-0.1228, 0] | 1 | 1 | No |

The ranked losses (`ranked-losses.json`) are:

- vs matched: ours lost 2 of 6 cells (+0.2409, +0.0139).
- vs recommended: ours lost 1 of 6 (+0.0139) and won on seed 3665334120 (0.2409 vs 0.2758).

The power analysis puts the required N at 2 (recommended) and 4 (matched) for the frozen 0.1 half-width target
(`power-analysis.json`).

## Spend

The account is a subscription, so the money charged is $0. The CLI's list-price estimates
(`receipts-compact.json`):

| Run | Calls | Failed | Canaries | List price |
|---|---|---|---|---|
| First run, stopped at call 380 | 381 | 1 | 2 | $5.60 |
| Resume | 465 | 0 | 2 | $9.00 |
| **Total** | 846 | 1 | 4 | **$14.60** |

That is $2.60-3.60 over the ~$11-12 estimate the pilot was approved at. The overrun is the 10 cells' partial spend
in the first run plus the resume's second pair of canaries. Across the three pilots, list price totals $36.77.
