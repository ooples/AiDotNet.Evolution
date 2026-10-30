# E-4 (#347): the capped pilot on `damped_oscillator` at 45 iterations

18 cells: 6 paired seeds x 3 arms (ours, OpenEvolve `recommended`, OpenEvolve `matched`), Williams balanced, on
the `damped_oscillator` dev partition. They ran through the metered broker against the subscription CLI
(`2.1.312 (Claude Code)`, `sonnet` resolving to `claude-sonnet-5-5`). **18/18 cells `completed`, 829 model calls
(831 receipts with the 2 canaries), 2,316,225 broker-counted tokens, 0 lost calls, 0 API-key calls, receipt
coverage 1.0, 9,234.6 s wall.**

This is the pilot the user approved on 2026-10-29: a capped run before any gate campaign or AlphaEvolve spend.

## Spend

| | |
| --- | --- |
| Monetary cost | **none recorded**: every receipt is `auth: subscription`, `monetary_cost: null` |
| CLI list-price estimate (sum of `total_cost_usd` over 831 calls) | **$11.00** (`receipts-compact.json`: `list_price_estimate_usd` 10.9974) |
| Approved cap | $25-50 |
| API-key calls | 0 |

## Reproducing it

```text
run_pilot_campaign.py --task damped_oscillator --seed-count 6 --iterations 45
    --model-calls 63 --model-tokens 1100000 --wall-seconds 3600
    --target-half-width 0.1 --holdout-seed 20261017
    --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll
    --upstream <openevolve @ 411fb59c886c> --executable <claude.exe 2.1.312>
    --confirm 5329935a393e2d4dd2e58832bbbe8c482dc6ae3c12e7d55b0549f9f45eed4cf7
```

Code: commit `1ba7c0e` of `feat/v1-e4-pilot` as built before its rebase onto `main`. That build predates the
commit that writes `hardware` into `summary.json`, so `hardware.json` was recorded afterwards on the same machine
with the same `describe_hardware()`: AMD Ryzen Threadripper 3990X, 128 logical processors, 96 GB, Windows 11,
Python 3.14.6. `registration.json` (SHA-256 `174465551aa8...`) and the sealed partition hash
`e4f8be2df3ca...` were fixed before anything ran; the partition is the same one the V1-40 re-pilot used.

## Per cell

Held-out NMSE, lower is better, scored outside every search process. The seed program scores **1.002775** on
this partition (V1-40 re-pilot, `../../pilot-v1-40-damped/884c765/`).

| seed | ours (model calls) | recommended (45) | matched (45) |
| --- | ---: | ---: | ---: |
| 1352180087 | 1.1293 (47) | 1.0028 | 1.0879 |
| 3018608857 | 1.0028 (47) | 1.0028 | 1.0028 |
| 2865726832 | 1.0395 (49) | 1.0343 | 1.0028 |
| 850262075 | 1.0053 (47) | 1.0058 | 1.0028 |
| 1699984856 | **0.9856** (49) | 1.0028 | 1.0028 |
| 268050748 | **0.9184** (50) | 1.0028 | 1.0206 |

Against the seed program (within 5e-4 counts as equal):

| arm | better | equal | worse |
| --- | ---: | ---: | ---: |
| ours | **2** | 1 | 3 |
| OpenEvolve recommended | 0 | 4 | 2 |
| OpenEvolve matched | 0 | 4 | 2 |

At 10 iterations (V1-40) no arm improved on the seed in any cell. At 45 iterations ours did in 2 of 6 cells and
OpenEvolve in none of 12. Ours also returned something worse than the seed in 3 of 6 cells, the same
held-out regression V1-40 reported.

## The registered endpoints

| config | n | mean difference (positive favours ours) | 95% interval | one-sided p | Holm p | superior |
| --- | ---: | ---: | --- | ---: | ---: | --- |
| recommended | 6 | -0.0050 | [-0.0603, 0.0414] | 0.587 | 0.827 | no |
| matched | 6 | +0.0064 | [-0.0261, 0.0474] | 0.414 | 0.827 | no |

Neither endpoint is superior, so the pilot does not support a quality claim either way.

## Sizing

`power-analysis.json` gives **N = 2**. That number answers the registered question: how many seeds bring the 95%
interval's half width under the 0.1 target. The pilot's own half widths (0.051 and 0.037) already meet it. The 0.1
target is 15-20x the observed mean differences, so a 2-seed campaign could not reject on either endpoint. For
scale, by a normal approximation (one-sided alpha 0.05, power 0.8), resolving the `matched` difference of +0.0064
at its SD of 0.0521 takes about 403 paired seeds. The `recommended` difference is negative, so no N makes it a
win.

## Arm parity

Every OpenEvolve cell made exactly 45 model calls (one per iteration). Ours made 47-50 under the same 63-call
broker cap, 4-11% more, from the engine's repair re-prompts. The broker counts agreed with ours in every cell
(the V1-40 call-count mismatch did not recur). A gate registration should state whether repair calls count against
the iteration budget, so the arms spend alike.

## What this bundle settles, and what it leaves open

- **Settled:** at 45 iterations `damped_oscillator` is no longer a dead control: one arm moves off the seed.
- **Settled:** spend at this budget is about $0.0133 of list price per model call. A gate campaign's cost scales
  with seeds x arms x calls per cell.
- **Open:** the observed differences are small against their variance, and ours regresses held-out in half its
  cells. A gate sized from these numbers is about 400 seeds per config, so it needs either a larger effect (an
  engine change that wins on dev first) or a different task and budget. The gate's pre-registration has to commit
  to one of those before it runs.