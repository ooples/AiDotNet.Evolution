# V1-40 (#121/#122): `two_variable_ratio` at 25 iterations - the third task is also a dead control

18 cells, 6 paired seeds x 3 arms, Williams balanced. **466 model calls, 61.5 minutes, 18 of 18 cells
`completed`, zero failures, receipt coverage 1.0, zero API-key calls, 468 receipts, all on CLI
2.1.302.** Target half width **0.10**. This task had never been piloted at any budget.

## The result

| endpoint | mean difference (positive favours us) | CI95 | one-sided p | Holm p | superior |
| --- | ---: | ---: | ---: | ---: | --- |
| `openevolve-recommended` | -0.0150 | [-0.0362, +0.0009] | 0.9201 | 1.0000 | no |
| `openevolve-matched` | -0.0038 | [-0.0343, +0.0318] | 0.6094 | 1.0000 | no |

`report.json` carries `OpenEvolveGate: false`. Both intervals straddle zero, so neither arm is shown
better than the other - which is the correct reading of a task where neither arm does much.

## Nobody searches here either

The seed program scores **1.003401** held-out NMSE on this sealed partition. Of the 18 scored cells:

| | cells |
| --- | ---: |
| **better** than the seed | **2** - ours at 1.000641 (**0.27%**) and `openevolve-matched` at 0.992728 (**1.06%**) |
| equal to it, within 5e-4 | 12 |
| worse than it | 4 |

`openevolve-recommended` returned **exactly 1.003401 in all six of its cells** - it never moved once.

Two cells in eighteen improved, by a fraction of a percent. On `rational_gain` our arm moves the same
metric by 63-87%. The three tasks in this family are not three difficulties of one measurement; one of
them admits search and two do not.

## The sizing N = 2 is precision on a null effect

Observed half widths 0.0186 and 0.0331, both inside the 0.10 target, so `power-analysis.json` reports
`RequiredN: 2` with `PilotAlreadyMeetsTarget`. The differences are tiny and consistent because nothing
is happening. **Do not read it as "this family is cheap to gate".**

## The family sweep, in one place

| task | budget | seed | cells better than the seed (of 18) | best endpoint | gate |
| --- | --- | ---: | ---: | --- | --- |
| `rational_gain` | 10 / 10 | 1.0790 | - | +0.3327 at Holm 0.178 | no |
| **`rational_gain`** | **25 / 35** | 1.0790 | - | **+0.6599 at Holm 0.0040, +0.2211 at Holm 0.0140** | **yes** |
| `damped_oscillator` | 10 / 10 | 1.002775 | 0 | sizing refused | no |
| `damped_oscillator` | 25 / 35 | 1.002775 | 1 (+0.48%) | **-0.0281, CI excludes parity against us** | no |
| `two_variable_ratio` | 25 / 35 | 1.003401 | 2 (0.27%, 1.06%) | -0.0150, straddles zero | no |

**One of three tasks discriminates, and raising the budget 2.5x did not change which one.** The gate
requires strict superiority on every family x config, so with this task set it cannot pass - not
because of the engine but because two of the three tasks have nearly nothing to find. That is a
decision about the gate's task set.

## Reproducing

```text
run_pilot_campaign.py --task two_variable_ratio --seed-count 6 --iterations 25
    --model-calls 35 --model-tokens 600000 --wall-seconds 2400 --cell-timeout 2400
    --target-half-width 0.10 --holdout-seed 20261017
    --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll
    --upstream <openevolve @ 411fb59c886c> --executable <claude.exe 2.1.302>
    --confirm 17f270e9ff5d39a560d8c02b30f23bc33830c7dde772ba0a793892c813bf95ea
```

Harness at `da65f1e` (the CLI 2.1.302 revalidation; canary baselines unchanged at 659/661/657).
