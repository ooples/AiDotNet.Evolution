# V1-40 (#121/#122): `damped_oscillator` at 25 iterations - still a dead control

18 cells, 6 paired seeds x 3 arms, Williams balanced. **465 model calls, 59.6 minutes, 18 of 18 cells
`completed`, zero failures, receipt coverage 1.0, zero API-key calls, 467 receipts, all on CLI
2.1.302.** Target half width **0.10**, the width the release owner froze for the gate.

This is the same task as `../../pilot-v1-40-damped/884c765`, with the per-cell budget raised from 10
iterations / 10-call ceiling to **25 / 35** - the change that made `rational_gain` separate on both
endpoints (`../../pilot-v1-40-rational-deep/42d0d5c`). **It did not generalise.**

## The result

| endpoint | mean difference (positive favours us) | CI95 | one-sided p | Holm p | superior |
| --- | ---: | ---: | ---: | ---: | --- |
| `openevolve-recommended` | -0.0089 | [-0.0340, +0.0191] | 0.7423 | 1.0000 | no |
| `openevolve-matched` | **-0.0281** | **[-0.0633, -0.0053]** | 1.0000 | 1.0000 | no |

`report.json` carries `OpenEvolveGate: false`. Note the second row: its interval **excludes zero on
the side that favours them**. That is a small measured loss, not noise, and it is the first endpoint in
any of these pilots where the interval separates the arms against us.

## Why the result is not about search quality

The seed program scores **1.002775** held-out NMSE on this sealed partition, computed directly with
`pilot_cells.score_held_out("damped_oscillator", 20261017, symbolic_regression_task/initial_program.py)`.
Of the 18 scored cells:

| | cells |
| --- | ---: |
| **better** than the seed | **1** - `openevolve-recommended` at 0.997994, an improvement of **0.48%** |
| equal to it, within 5e-4 | 7 |
| worse than it | 10 |

So at 2.5x the budget, one cell in eighteen improved on the starting program, by half a percent. Set
against `rational_gain`, where our arm moved 1.0790 -> 0.1435-0.3970, a **63-87%** improvement, this
task is not a harder version of the same measurement - **it is a task on which neither engine
searches at all**, and the -0.0281 is our arm's attempts landing slightly below the seed more often
than theirs do.

## The sizing says N = 2, and that is a trap

`power-analysis.json` reports `RequiredN: 2` for both endpoints with observed half widths of 0.0266
and 0.0290 - already inside the 0.10 target, so `PilotAlreadyMeetsTarget` is true. **That is precision
on a null effect.** The paired differences are tiny and consistent because nothing is happening; a
reader who takes "N = 2" as "this family is cheap to gate" would be sizing a campaign to measure zero
very precisely. The sizing module is right and the number is useless here.

## What it means for the gate

#122 requires strict superiority on **every** family x config endpoint. This task supplies two
endpoints that cannot pass at either budget tried, one of them failing against us with an interval that
excludes parity. **No budget fixes that, because the task admits almost no improvement to find.** The
decision it forces is about the gate's task set, not about the engine.

## Reproducing

```text
run_pilot_campaign.py --task damped_oscillator --seed-count 6 --iterations 25
    --model-calls 35 --model-tokens 600000 --wall-seconds 2400 --cell-timeout 2400
    --target-half-width 0.10 --holdout-seed 20261017
    --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll
    --upstream <openevolve @ 411fb59c886c> --executable <claude.exe 2.1.302>
    --confirm 46781d63c1bf2afa0f7e76456404e66b5bd4de5d448b09e4e32e4df3d076f13e
```

Harness at `da65f1e`, which carries the CLI 2.1.302 revalidation: the CLI self-updated mid-campaign and
the version pin refused every call until the isolation flags and stream-json protocol were re-checked.
The canary baselines did not move (659/661/657), so this run and the 2.1.299 `rational_gain` run share
a prompt envelope that is identical in token terms.
