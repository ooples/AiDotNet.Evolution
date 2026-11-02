# E-4 (#347): the fixed AlphaEvolve re-pilot, sizing the gate on c5 and c3

The first AlphaEvolve pilot ([`../../pilot-e4-alphaevolve/7fa7801/`](../../pilot-e4-alphaevolve/7fa7801/)) found three
problems with the gate design, and the maintainer settled each one. This re-pilot repeats that pilot's design with all
three fixed, so the gate is sized from clean variance:

- **Endpoint:** the verifier's score of the heights the search's own evaluation of the returned program produced
  (`EvaluatedHeights`), not a fresh run of the program.
- **Libraries:** the statement says the evaluator's Python has numpy and scipy, and scipy 1.17.1 is installed.
  `summary.json` records numpy 2.5.4 and scipy 1.17.1.
- **Seed:** our arm gets each cell's paired seed (17a7aa8). In the first pilot it ran at seed 42 in every cell.

The rest of the design matches the first pilot:
- 6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced.
- `c5-erdos-overlap` and `c3-autocorrelation`, 25 iterations, a 35-call broker cap per cell.
- Constructions get 60 s and 4 GiB each.
- Seeds were drawn fresh at registration (`registration.json`).
- Claude Code 2.1.316, sonnet (`claude-sonnet-5-5`); our CLI ran from a frozen build of 7428bcd.

## Two runs: the first run, and a supplement for its 5 failed cells

**First run.** It ran from a frozen worktree at a76592e: 36 cells, 841 calls, 38,248 s. 31 cells completed and 5 failed,
all with `SearchFailed: the broker closed during the cell` (`first-run/`, `cells.jsonl`).

- **Cause:** a bug in a76592e's construction cleanup.
  - After a construction timed out, Windows released the killed child's file handles a moment after the child exited.
  - Removing the construction's temporary directory then raised `PermissionError [WinError 32]`, and the broker closes
    a cell on any evaluator exception.
  - In the 2 failed cells inspected, the evaluation that closed the broker ran to the 60 s timeout. Reproduced off-line
    with a busy-loop candidate: 1 timeout in 12 failed this way single-threaded, and more under 4 threads.
- **Fix:** main (fe0ea75) retries the cleanup and reaps the job before it. Under the same stress, main's construction
  had no failures in 72 timeouts.

**Supplement.** The maintainer chose to rerun only the failed cells, at their registered seeds, on main's fixed harness.

- It used a frozen worktree at fe0ea75, whose verifier sources and launcher are identical to a76592e's, so the
  registration's seals and projection still match.
- Its state directory carried the first run's 31 completed cells (`cells-supplement.jsonl`). The campaign runner skips
  finished cells, so it ran exactly the 5 failed ones and analysed all 36 together.
- All 5 completed: 127 calls, 6,191 s.

The fix changes only what happens after a construction is killed. It does not change how a search proposes,
evaluates or selects.

## Result

All 36 cells completed; 900 calls and 8,054,217 tokens in the cells. Every cell used 25 calls.

`published_ratio` per cell (`rows.json`; 1 is the published AlphaEvolve value). An asterisk marks a cell from the
supplement:

| Task | Seed | Ours | OpenEvolve recommended | OpenEvolve matched |
|---|---|---|---|---|
| c5 | 870798037 | 0.9997 | 0.9995 | 0.9999 |
| c5 | 1371484982 | 0.9999 | 0.9997 | 0.9990 |
| c5 | 1851964264 | 0.9995 | 0.9998 | 0.9998 |
| c5 | 2666403315 | 1.0000 | 0.9998 | 0.9997 |
| c5 | 3598861017 | 0.9996 | 0.9999* | 0.9998 |
| c5 | 3685940066 | 0.9994 | 0.9996 | 0.9999 |
| c3 | 870798037 | 0.9967* | 0.9914 | 0.9906 |
| c3 | 1371484982 | 0.9911 | 0.9985 | 0.9932 |
| c3 | 1851964264 | 0.9915 | 0.9909 | 0.9955 |
| c3 | 2666403315 | 0.9893* | 0.9929 | 0.9901 |
| c3 | 3598861017 | 0.9982 | 0.9940 | 0.9968* |
| c3 | 3685940066 | 0.9909 | 0.9895* | 0.9834 |

- **Against the published values:** no cell beats them. The highest is ours, c5, seed 2666403315, at 0.99997.
- **Endpoints** (`MeanDifference` is ours minus theirs, so positive favours ours; paired bootstrap, 1,000 samples;
  `summary.json`):

  | Task | Comparison | Mean difference | 95% interval | One-sided p | Holm p | Superior |
  |---|---|---|---|---|---|---|
  | c5 | vs recommended | -0.00005 | [-0.0002, +0.0001] | 0.682 | 1 | No |
  | c5 | vs matched | +0.00001 | [-0.0003, +0.0004] | 0.529 | 1 | No |
  | c3 | vs recommended | +0.00006 | [-0.0035, +0.0034] | 0.449 | 1 | No |
  | c3 | vs matched | +0.0013 | [-0.0019, +0.0046] | 0.226 | 0.903 | No |
- **Cells lost** (`ranked-losses.json`):

  | Task | To `recommended` | To `matched` |
  |---|---|---|
  | c5 | 3 of 6 (worst by 0.0003) | 4 of 6 (worst by 0.0005) |
  | c3 | 2 of 6 (worst by 0.0075) | 3 of 6 (worst by 0.0040) |
- **Tokens per arm:** each arm made 150 calls per task.

  | Task | Ours | `recommended` | `matched` |
  |---|---|---|---|
  | c5 | 1,247,742 | 1,448,551 | 1,476,736 |
  | c3 | 1,156,301 | 1,290,158 | 1,434,729 |
- **Canary:** sonnet read 605 input tokens, the baseline under the transport's forced `DO_NOT_TRACK=1`.

## Sizing

`power-analysis.json` reports N = 2 for every endpoint at the frozen 0.02 half-width. Every observed difference is at
most 0.0013, so that width cannot separate the arms. The required N at narrower widths uses the same normal
approximation, N = (1.96 sd / half-width)^2, with a floor of 2:

| Task | Comparison | Paired sd | Mean difference | N at 0.002 | N at 0.001 | N at 0.0005 | N at 0.0002 |
|---|---|---|---|---|---|---|---|
| c5 | vs recommended | 0.00027 | -0.00005 | 2 | 2 | 2 | 7 |
| c5 | vs matched | 0.00050 | +0.00001 | 2 | 2 | 4 | 25 |
| c3 | vs recommended | 0.00483 | +0.00006 | 23 | 90 | 360 | 2,246 |
| c3 | vs matched | 0.00459 | +0.00134 | 21 | 82 | 325 | 2,026 |

The sizing in the first pilot, from seed-42 cells and a fresh-run endpoint, had c3 paired standard deviations of 0.013
and 0.018. Here they are 0.0048 and 0.0046.

## Spend

The account is a subscription, so the money charged is $0. The CLI's list-price estimates (`receipts-compact.json`):

| Run | Calls | Failed | Canaries | List price |
|---|---|---|---|---|
| First run | 843 | 0 | 2 | $39.13 |
| Supplement | 127 | 0 | 2 | $5.82 |
| **Total** | 970 | 0 | 4 | **$44.94** |

**Against the cap:** the maintainer approved a $50 cap with a stop at $45. They approved the supplement, inside the
same cap, after the failures.

## Files

- `rows.json`, `report.json`, `summary.json`, `power-analysis.json` and `ranked-losses.json` are the combined 36-cell
  analysis, written by the supplement run.
- `first-run/` holds the first run's own analysis, with the 5 failed cells counted at the failure value 0.
- `registration.json`, `sizing.json` and `iterations.json` are frozen for both runs.
- `cells.jsonl` is the first run's journal; `cells-supplement.jsonl` is the supplement's.
- `programs/` holds every cell's returned program, named `<task>__<arm>__<seed>.py`. For the 5 rerun cells, it is the
  supplement's.
