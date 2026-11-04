# E-4 (#347): c3 at the broker maximum, 60 iterations per cell

The fixed re-pilot ([`../../pilot-e4-alphaevolve-fixed/a76592e/`](../../pilot-e4-alphaevolve-fixed/a76592e/)) found no
superior endpoint at 25 iterations. c5 sat at 0.9990 to 1.0000 in every cell, so it cannot separate the arms. After
that null result the maintainer chose a bigger budget per cell, and then this design: c3 only, 60 iterations, and a
63-call cap per cell, with a $60 cap on spend. 63 calls is the most the broker admits
(`MAX_MODEL_CALLS` 64 less the evaluation headroom).

The rest of the design matches the fixed re-pilot:
- 6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced.
- Seeds were drawn fresh at registration (`registration.json`).
- The endpoint is the verifier's score of the heights the search's own evaluation produced (`EvaluatedHeights`).
- Constructions get 60 s and 4 GiB each. The evaluator has numpy 2.5.4 and scipy 1.17.1.
- Sonnet (`claude-sonnet-5-5`). Our CLI ran from a frozen build of 7428bcd.
- Per cell: 2,000,000 tokens and 3,600 s. The power target is a 0.002 half-width.

## Two runs: the first run, and a supplement for its 2 failed cells

**First run.** It ran from a frozen worktree at fe0ea75: 18 cells, 975 calls, 52,937 s. 16 cells completed and 2
failed, both `SearchFailed: the broker closed during the cell` (`first-run/`, `cells.jsonl`).

- **Cause:** the account's weekly subscription limit.
  - Call 974, the 25th call of our arm's cell at seed 4035587021, returned "You've hit your weekly limit".
  - The transport closes admission after a failed call, so that cell failed.
  - The next cell, `matched` at the same seed, was refused at its first call. It recorded 1 model call and made no
    CLI call.
- **The CLI changed during the run.** The run pointed at the installed Claude Code, which replaced itself in place:

  | Calls | Binary (sha256) | Version the receipt records |
  |---|---|---|
  | 0 to 49 | 2.1.316 (`e896a9d8…`) | 2.1.316 |
  | 50 to 974 | 2.1.317 (`26b01ec2…`) | 2.1.316 |

  The transport read the version once, at the first call, so the receipts record 2.1.316 for every call. Their
  `executable_sha256` field shows the switch.
  - The first run's canary ran on 2.1.316 and read 605 for sonnet.
  - 2.1.317's canary reads 652, 47 tokens more on every model, with the resolved models unchanged.
  - #370 pins 2.1.317, re-records the canary and makes the transport hash the binary before every call. A binary that
    changes after its version was verified is refused, and admission closes.

**Supplement.** The maintainer chose to pin 2.1.317 and rerun only the 2 failed cells, at their registered seeds.

- It ran from a frozen worktree at e7ca811 (#370's first commit), pointed at a frozen copy of 2.1.317. The verifier
  sources and launcher match fe0ea75's, so the registration's seals and projection still match. Only
  `claude_transport.py`, its test and the canary changed under `benchmarks/external`.
- Its state directory carried the first run's 16 completed cells (`cells-supplement.jsonl`). The campaign runner skips
  finished cells, so it ran exactly the 2 failed ones and analysed all 18 together.
- Both completed: 121 calls, 5,581 s, all on 2.1.317. The canary read 652 for sonnet.

So 50 calls ran on 2.1.316, including the first run's 2 canaries, and 1,046 on 2.1.317.

## Result

All 18 cells completed. 1,068 calls and 11,014,006 tokens were spent in the cells.

`published_ratio` per cell (`rows.json`; 1 is the published AlphaEvolve value). An asterisk marks a cell from the
supplement:

| Seed | Ours | OpenEvolve recommended | OpenEvolve matched |
|---|---|---|---|
| 3778472211 | 0.9974 | 0.9942 | 0.9949 |
| 1529209595 | 0.9886 | 0.9937 | 0.9925 |
| 2033491959 | 0.9857 | 0.9955 | 0.9917 |
| 3312104218 | 0.9928 | 0.9975 | 0.9961 |
| 2697356328 | 0.9987 | 0.9973 | 0.9897 |
| 4035587021 | 0.9916* | 0.9949 | 0.9936* |
| **Mean** | 0.9925 | 0.9955 | 0.9931 |

- **Against the published value:** no cell beats it. The highest is ours at seed 2697356328, 0.9987.
- **Endpoints** (`MeanDifference` is ours minus theirs, so positive favours ours; paired bootstrap; `summary.json`):

  | Comparison | Mean difference | 95% interval | One-sided p | Holm p | Superior |
  |---|---|---|---|---|---|
  | vs recommended | -0.0031 | [-0.0063, +0.0003] | 0.954 | 1 | No |
  | vs matched | -0.0006 | [-0.0041, +0.0037] | 0.637 | 1 | No |
- **Cells lost** (`ranked-losses.json`):
  - To `recommended`: 4 of 6, worst by 0.0098 (seed 2033491959).
  - To `matched`: 4 of 6, worst by 0.0060 (the same seed).
- **Calls and tokens per arm:** every cell used 54 to 60 calls.

  | Arm | Calls | Tokens |
  |---|---|---|
  | Ours | 359 | 3,247,654 |
  | `recommended` | 356 | 4,118,720 |
  | `matched` | 353 | 3,647,632 |

## Sizing

`power-analysis.json` sizes the campaign at a 0.002 half-width. The runner's own sizing refuses it, because the
required N exceeds the cap. The normal approximation is N = (1.96 sd / half-width)^2:

| Comparison | Paired sd | Mean difference | N at 0.002 | N at 0.001 |
|---|---|---|---|---|
| vs recommended | 0.00471 | -0.0031 | 22 | 86 |
| vs matched | 0.00547 | -0.0006 | 29 | 115 |

At 25 iterations the fixed re-pilot's c3 paired standard deviations were 0.0048 and 0.0046, on different seeds.

## Spend

The account is a subscription, so the money charged is $0. The CLI's list-price estimates (`receipts-compact.json`):

| Run | Calls | Failed | Canaries | List price |
|---|---|---|---|---|
| First run | 975 | 1 | 2 | $51.46 |
| Supplement | 121 | 0 | 2 | $5.71 |
| **Total** | 1,096 | 1 | 4 | **$57.17** |

The failed call is call 974, the weekly limit. The maintainer approved a $60 cap, and approved the supplement inside
it.

## Files

- `rows.json`, `report.json`, `summary.json`, `power-analysis.json` and `ranked-losses.json` are the combined 18-cell
  analysis, written by the supplement run.
- `first-run/` holds the first run's own analysis, with the 2 failed cells counted at the failure value 0.
- `registration.json`, `sizing.json` and `iterations.json` are frozen for both runs.
- `cells.jsonl` is the first run's journal; `cells-supplement.jsonl` is the supplement's.
- `programs/` holds every cell's returned program, named `<task>__<arm>__<seed>.py`. For the 2 rerun cells, it is the
  supplement's.
- `receipts-compact.json` holds every call's receipt with its `executable_sha256`.
- `scripts/` holds the scripts that built the supplement root and this bundle.
