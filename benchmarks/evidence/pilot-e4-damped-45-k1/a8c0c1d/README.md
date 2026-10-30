# E-4 (#347): the re-pilot with 800 selection rows and the seed-margin champion rule

The same 18-cell design as the first pilot ([`../../pilot-e4-damped-45/1ba7c0e/`](../../pilot-e4-damped-45/1ba7c0e/)):
6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced, `damped_oscillator`
dev partition, 45 iterations, 63-call broker cap per cell, held-out seed 20261017. Two things changed, both chosen
after the first pilot traced its held-out regressions to noisy selection on 80 rows:

- **Selection rows 60/20 -> 600/200 for every arm.** At 80 rows a constant and the seed program differ by 0.112 NMSE
  (SD across row draws). At 800 rows the SD is 0.029.
- **Ours keeps the seed unless a candidate beats it by k = 1 combined standard error**
  (`--champion-standard-errors 1 --standard-error-metric quality_standard_error`). The broker reports each
  candidate's quality standard error by the delta method. OpenEvolve has no such rule, and its arms are unchanged.

**18/18 cells `completed`, 830 model calls (832 receipts with the 2 canaries), 2,415,221 broker-counted tokens,
0 lost calls, 0 API-key calls, receipt coverage 1.0, 9,802.8 s wall.** CLI `2.1.314 (Claude Code)`, revalidated
before the run (canary baselines 658/637/633, unchanged from 2.1.312). `sonnet` resolved to `claude-sonnet-5-5`.

## Spend

| | |
| --- | --- |
| Monetary cost | **none recorded**: every receipt is `auth: subscription`, `monetary_cost: null` |
| CLI list-price estimate (sum of `total_cost_usd` over 832 calls) | **$11.17** (`receipts-compact.json`: `list_price_estimate_usd` 11.1734) |
| Approved for this re-pilot | about $11-25 |
| API-key calls | 0 |

The two pilots together come to $22.17 at list price.

## Per cell

Held-out NMSE, lower is better, scored outside every search process. The seed program scores **1.002775**.

| seed | ours (model calls) | recommended (45) | matched (45) |
| --- | ---: | ---: | ---: |
| 3365596871 | 1.0028 (50) | 1.0028 | 1.0028 |
| 600375634 | 1.0028 (48) | 1.0028 | 1.0032 |
| 2504790974 | 1.0028 (47) | 1.0028 | 1.0028 |
| 663733166 | 1.0028 (47) | 1.0028 | 1.0028 |
| 380179826 | 1.0028 (49) | 1.0028 | 1.0206 |
| 4217085262 | 1.0028 (49) | 1.0028 | 1.0028 |

Against the seed program (within 5e-4 counts as equal), next to the first pilot:

| arm | better | equal | worse | first pilot (better / equal / worse) |
| --- | ---: | ---: | ---: | --- |
| ours | 0 | **6** | **0** | 2 / 1 / 3 |
| OpenEvolve recommended | 0 | 6 | 0 | 0 / 4 / 2 |
| OpenEvolve matched | 0 | 5 | 1 | 0 / 4 / 2 |

## Our arm's champion decisions

`champion-recovered.json` shows, per cell, the seed's selection quality and standard error and the best candidate's
gain in combined standard errors (Z). It was recovered from the traces and `best.py` because `campaign_runner.run`
dropped the `Champion` field from the rows (fixed in the commit that adds this bundle, with an end-to-end test).

| seed | candidates scored | best candidate's Z | champion |
| --- | ---: | ---: | --- |
| 3365596871 | 45 | 0.00 | seed |
| 600375634 | 45 | 0.10 | seed |
| 2504790974 | 45 | 0.06 | seed |
| 663733166 | 45 | 0.00 | seed |
| 380179826 | 45 | 0.21 | seed |
| 4217085262 | 45 | 0.01 | seed |

No candidate came within a quarter of the k = 1 margin. On 800 selection rows no candidate in 270 looked better than
the seed by more than 0.0037 quality. The rule therefore decided nothing in this run, and the larger selection set
alone accounts for ours returning the seed. The first pilot's two held-out wins (0.9856 and 0.9184) did not recur
on these seeds.

## The registered endpoints

| config | n | mean difference (positive favours ours) | 95% interval | one-sided p | Holm p | superior |
| --- | ---: | ---: | --- | ---: | ---: | --- |
| recommended | 6 | 0.0000 | [0.0000, 0.0000] | 1.000 | 1.000 | no |
| matched | 6 | +0.0030 | [0.0000, 0.0090] | 0.093 | 0.186 | no |

Neither endpoint is superior. The `matched` difference comes from OpenEvolve regressing in one cell, not from ours
improving on the seed.

## What this bundle settles, and what it leaves open

- **Settled:** with 800 selection rows ours no longer regresses held-out on these seeds (0 of 6, against 3 of 6 on
  80 rows). The first pilot's regressions were selection noise, as the offline replay predicted.
- **Settled:** at this budget no arm improves on the seed of `damped_oscillator` once selection is honest. The best
  candidate in each of our cells was within 0.21 standard errors of the seed.
- **Open:** a gate needs an effect to detect. On this task and budget there is none in either direction for ours, so
  the gate's pre-registration must change the task, the budget or the engine first. That choice, and any gate or
  AlphaEvolve spend, is the user's decision.

## Reproducing it

```text
run_pilot_campaign.py --task damped_oscillator --seed-count 6 --iterations 45
    --selection-rows 600 200 --champion-standard-errors 1
    --model-calls 63 --model-tokens 1100000 --wall-seconds 3600
    --target-half-width 0.1 --holdout-seed 20261017
    --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll
    --upstream <openevolve-review-20260910 checkout> --executable <claude.exe 2.1.314>
    --python <venv python> --confirm eed21b7fb02ad8fcaf351188026e310c9bd26401454c95ef016370f29391e210
```

Code: `feat/v1-e4-pilot` at `a8c0c1d`. A test run in the same worktree rebuilt the CLI's two engine assemblies at
21:57, mid-campaign (`MID-RUN-REBUILD.txt`). Our cells for seed 3365596871 ran before it. Our five later cells ran
`1758ffd`, whose only source difference is `!(gain > noise)` -> `gain <= noise` in `EvolutionChampionSelection`,
equivalent for the finite gains a completed evaluation has. OpenEvolve cells do not load these assemblies. Hardware is in
`summary.json`: AMD Ryzen Threadripper 3990X, 128 logical processors, 96 GB, Windows 11, Python 3.14.6.
`registration.json` (RegistrationSha256 `d6753177215a...`) was fixed before anything ran.