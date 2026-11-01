# E-4 (#347): the AlphaEvolve pilot, sizing the gate on c5 and c3

The post-fix damped pilot ([`../../pilot-e4-damped-45-k1-postfix/7428bcd/`](../../pilot-e4-damped-45-k1-postfix/7428bcd/))
saturated: all 18 cells returned the exact formula, so it cannot separate the arms. The maintainer moved the gate to the
AlphaEvolve tasks and approved a capped pilot on them first, to size the gate. The cap was $50 list price, with a stop at $45.

- **Design:** 6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced, on two
  of the five tasks.
  - `c5-erdos-overlap` (minimum overlap) and `c3-autocorrelation`.
  - 25 iterations and a 35-call broker cap per cell, a 60 s limit per construction.
- **Seeds:** drawn fresh at registration (`registration.json`).
- **Harness and CLI:**
  - The harness is 7fa7801. The CLI ran from a frozen copy built at 7428bcd (`F:\evo-pilot\cli-7428bcd`).
- **Model:** Claude Code 2.1.316, sonnet (`claude-sonnet-5-5`).
- **Endpoint:** `published_ratio`, the verifier's `combined_score`. It is the fraction of the published AlphaEvolve value
  (maximize, 1 = the published value). It is scored from a fresh run of the returned program's construction.

## Result

All 36 of 36 cells completed: 900 model calls and 8,447,598 tokens, 39,608 s wall. Every cell used 25 calls.

`published_ratio` per cell (`rows.json`):

| Task | Seed | Ours | OpenEvolve recommended | OpenEvolve matched |
|---|---|---|---|---|
| c5 | 1756722251 | 0.9941 | 0.9982 | 0.9978 |
| c5 | 3721011351 | 0.9964 | 0.9979 | 0.9970 |
| c5 | 305925402 | 0.9988 | 0.9971 | 0.9975 |
| c5 | 2901860563 | 0.9980 | 0.9987 | 0.9981 |
| c5 | 2413275516 | 0.9984 | 0.9991 | 0.9990 |
| c5 | 1353004307 | 0.9970 | 0.9992 | 0.9982 |
| c3 | 1756722251 | 0.9902 | 0.9755 | 0.9963 |
| c3 | 3721011351 | 0.9944 | 0.9957 | 0.9951 |
| c3 | 305925402 | 0.9775 | 0.9644 | 0.9962 |
| c3 | 2901860563 | 0.9982 | 0.9914 | 0.9615 |
| c3 | 2413275516 | 0.9981 | 0.9636 | 0.9946 |
| c3 | 1353004307 | 0.9975 | 0.9965 | 0.9943 |

Endpoints (`MeanDifference` is ours minus theirs, so positive favours ours; paired bootstrap, 1,000 samples; `summary.json`):

| Task | Comparison | Mean difference | 95% interval | One-sided p | Holm p | Superior |
|---|---|---|---|---|---|---|
| c5 | vs recommended | -0.0013 | [-0.0026, +0.0001] | 0.972 | 1 | No |
| c5 | vs matched | -0.0008 | [-0.0022, +0.0004] | 0.901 | 1 | No |
| c3 | vs recommended | +0.0115 | [+0.0032, +0.0217] | 0.003 | 0.012 | Yes |
| c3 | vs matched | +0.0030 | [-0.0087, +0.0178] | 0.366 | 1 | No |

- **Cells lost** (`ranked-losses.json`):
  - c5: ours lost 5 of 6 cells to each arm.
  - c3: ours lost 1 of 6 to `recommended` and 3 of 6 to `matched`. The worst is seed 305925402, by 0.0188.
- **Calls and tokens per arm:** each arm made 150 calls per task. Tokens:

  | Task | Ours | `recommended` | `matched` |
  |---|---|---|---|
  | c5 | 1,315,619 | 1,473,713 | 1,422,951 |
  | c3 | 1,188,717 | 1,604,436 | 1,442,162 |
- **Power analysis** (`power-analysis.json`), at the frozen 0.02 half-width:
  - c5: required N is 2 for each endpoint.
  - c3: required N is 2 vs `recommended` and 4 vs `matched`.
  - The campaign N is 4. The observed c5 differences (about 0.001) are a twentieth of that half-width.
- **Canary:** sonnet read 637 input tokens. That matches harness 7fa7801's transport, which scrubbed `DO_NOT_TRACK`;
  a5cd684 later forced `DO_NOT_TRACK=1`, which moved the baseline to 605.

## Three things the gate design must settle

These were found in this pilot's own data. None of them changes a number above; each changes what the gate's number would
mean.

### 1. The endpoint re-runs a construction that is bounded by wall-clock time

Every one of the 36 returned programs reads the clock (`time.time`, `perf_counter` or `monotonic`), and 18 of them name
a deadline or time budget. So a fresh run of the construction, which is the endpoint, can differ from the run the search
evaluated.

`search-vs-endpoint.json` lists, for each cell, the best score the search evaluated next to the endpoint:

- 33 of 36 cells differ by less than 0.0006.
- Three differ by more:
  - ours, c3, seed 305925402: 0.9874 in the search, 0.9775 at the endpoint (0.0099).
  - ours, c5, seed 1756722251: 0.9967 in the search, 0.9941 at the endpoint (0.0026).
  - `matched`, c3, seed 1353004307: 0.9958 in the search, 0.9943 at the endpoint (0.0015).

  All three are larger than the c5 mean differences in the endpoint table.

**Re-runs.** After the pilot, every returned program was run and scored twice more, one at a time, on the otherwise idle
machine (`endpoint-reruns.json`; one run overlapped a short `dotnet test`).

- Median spread across the three endpoint runs of a program: 0.00002. Six programs gave the same value all three times.
- Spread above 0.0005 in 3 of 36 programs:
  - ours, c3, seed 305925402: 0.9775, 0.9896, 0.9861 (spread 0.0121). Each run took about 48 s.
  - `recommended`, c3, seed 1756722251: spread 0.0010.
  - ours, c3, seed 3721011351: spread 0.0007.
- **Ours, c5, seed 1756722251 is a timing effect, not run-to-run scatter.**
  - `best.py` is the genome the search scored at 0.99668 (evaluation 25 in the cell's checkpoint). It is not a
    champion-selection error.
  - In the search that construction took 23.3 s. The two re-runs took 16.8 s and 20.2 s, and with the endpoint all
    three scored 0.99410.
  - Its stages stop at fixed deadlines, so how far each stage got depended on how fast the machine ran it.

So a single fresh run of one program scattered by up to 0.012 on c3, and a program's score depended on the machine's
speed while it ran. The c5 differences the gate would test are about 0.001.

### 2. The statement does not say which libraries are installed, and scipy is not

The evaluation interpreter has numpy and no scipy. The task statement does not say so.

- 13 of the 36 returned programs reference scipy. That is 3 of ours, 5 of `recommended` and 5 of `matched`.
  - All 13 still returned a construction and scored, so each caught the failed import and returned without the scipy
    step (a `linprog` refinement in every one).
- 14 of our 150 c5 evaluations scored exactly the starting program's value, which a skipped refinement step would
  produce.

### 3. Our arm ran every pilot at seed 42

Our CLI reads `random_seed` from the task's `config.yaml`, and the pilot passed the task's own config. That config sets no
seed, so every cell of ours, in this pilot and the earlier ones, ran at seed 42, while the OpenEvolve arms received the
cell's paired seed.

- 17a7aa8 fixes it: our arm now gets a per-cell config with only `random_seed` changed.
- The fix landed after this pilot ran, so the pilot's results for ours are six runs of one seed's search under different
  model samples, not six seeds.

## Spend

The account is a subscription, so the money charged is $0. The CLI's list-price estimate (`receipts-compact.json`):

| Run | Calls | Failed | Canaries | List price |
|---|---|---|---|---|
| Pilot | 902 | 0 | 2 | $44.42 |

**Against the cap:** the maintainer approved this pilot with a $50 cap and a stop at $45.

**Calls with no receipt:** the `--stub` rehearsals made no model calls.

## Files

- The runner wrote `cells.jsonl`, `power-analysis.json`, `ranked-losses.json`, `registration.json`, `report.json`,
  `rows.json`, `sizing.json` and `summary.json`.
- `programs/` holds every cell's returned program, named `<task>__<arm>__<seed>.py`.
- `scripts/` holds the three scripts written after the run. They use the run's local paths (`F:\evo-pilot\...`).
  - `bundle_ae.py` copied the files and compacted the receipts.
  - `endpoint_vs_search.py` wrote `search-vs-endpoint.json`.
  - `rerun_endpoints.py` made the re-runs that `endpoint-reruns.json` summarizes.
