# V1-40 (#121/#122): the re-pilot on `damped_oscillator`, and why it cannot size the gate campaign

18 cells - 6 paired seeds x 3 arms, Williams balanced - on the `damped_oscillator` dev partition,
through the metered broker against the real subscription CLI. **180 model calls, 25.6 minutes,
16 cells `completed` and 2 `failed`, receipt coverage 1.0, zero API-key calls, 472,556 broker-counted
tokens over 182 receipts** (180 cell calls and 2 canaries).

The first pilot (`../../pilot-v1-07/aafd609`) ran `rational_gain` and left the quality row undecided:
`recommended` +0.3327 at Holm 0.178, `matched` +0.0260 at Holm 0.412, on a task whose model-free floor
closes 89.9-94.8% of the gap. `damped_oscillator` was chosen for the re-pilot because its floor study
left the most room. **It did not discriminate either, and this bundle is the measurement that says so.**

## Reproducing the scored partition

```text
run_pilot_campaign.py --task damped_oscillator --seed-count 6 --iterations 10
    --model-calls 10 --model-tokens 600000 --wall-seconds 1800
    --target-half-width 0.05 --holdout-seed 20261017
    --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll
    --upstream <openevolve @ 411fb59c886c> --executable <claude.exe 2.1.299>
    --confirm a9f657992a8137833fd129701bdda408166bcecfb357eee2bf668354be5a20aa
```

`pilot_cells.holdout_seal("damped_oscillator", 20261017)` reproduces the sealed partition hash
`e4f8be2df3ca41b28ceaef2bfd84f4970a4f6fd1f2a7dc504914850b1f9b382b`, which is the value
`registration.json` committed before anything ran. The six paired seeds are in `registration.json`;
nothing here chose them.

## The finding: no arm improved on the seed program, in any cell

The seed program scores **1.002775** held-out NMSE on this sealed partition - computed directly with
`pilot_cells.score_held_out("damped_oscillator", 20261017, symbolic_regression_task/initial_program.py)`,
not read off a run. Against that:

| | cells |
| --- | ---: |
| scored (excludes the 2 failed) | 16 |
| **better** than the seed program | **0** |
| equal to it, to within 5e-4 | 12 |
| worse than it | 4 |

Held-out NMSE per seed, lower better, scored outside every search process from a seed no search was told:

| seed | ours | recommended | matched |
| --- | ---: | ---: | ---: |
| 3927176509 | 1.1356 | 1.0028 | 1.0028 |
| 1653076365 | **failed** | 1.0028 | 1.0028 |
| 1198854388 | 1.0395 | 1.0028 | 1.0028 |
| 3892904201 | **failed** | 1.0028 | 1.0028 |
| 1197696667 | 1.0028 | 1.0028 | 1.0028 |
| 2623196786 | 1.0293 | 1.0028 | 1.0168 |

**Every 1.0028 in that table is the seed program's own score.** OpenEvolve returned the seed in 11 of
its 12 cells and something worse in the twelfth; our engine returned the seed once and something worse
three times. At 10 iterations and 10 model calls per cell, this task is a **dead control**: it cannot
separate two engines because neither engine moves on it.

That is the honest reading of the two "losses" the runner prints. They are not evidence that OpenEvolve
searches better here - its arm barely searches at all. They are evidence that **our arm accepts a
candidate that is worse held-out than the program it started from**, which is a finding about our
selection under a tiny budget rather than a comparison.

## Why the reported mean difference is not a result

`report.json` carries `MeanDifference` -333333.03 on both endpoints, with `OneSidedP` 1.0 and
`Superior` false. That number is the registration's own `FailureValue` of 1,000,000 entering the mean
for the two cells our arm failed, divided across six seeds. It is working as designed - a failure is a
loss, not a missing row - but it means **the mean carries the failures and nothing else**, so the per-cell
table above is the only readable comparison in this bundle. Nobody should quote -333,333.

## The two failures are an engine defect, reproduced live

```text
FAILED aidotnet seed 1653076365  SearchFailed: the CLI reported 13 calls but the broker admitted 10
FAILED aidotnet seed 3892904201  SearchFailed: the CLI reported 13 calls but the broker admitted 10
```

Our CLI counted 13 model calls where the broker had admitted 10, on 2 of our 6 cells. The broker's
ceiling held - `rows.json` records `ModelCalls` 10 for both, the receipts total 182, and
`cells_with_unknown_spend` is 0 - so nothing overspent; the cells were refused for disagreeing about
what had been spent.

This matters because the same symptom was seen earlier under `--stub` at a larger magnitude (26 vs 10)
and was attributed to the stub: a one-shot canned edit stopped applying, the engine's repair path
re-prompted, and its counter ran past the ceiling. The stub exaggerated it. **It did not cause it.**
The live `rational_gain` pilot had 18/18 cells at exactly 10 calls, which is why the earlier reading
looked like a stub artifact, and this run is the counter-example: on a task where candidates fail to
apply more often, the mismatch appears against the real model too.

## The sizing refusal

```text
sizing refused: Required N exceeds the cap; a campaign that large is a plan to change, not to run
```

`power-analysis.json` carries `Unachievable` rather than an N. With no cell better than the seed, the
measured effect is not an effect, so no N reaches a 0.05 CI half width. **The re-pilot therefore does
not re-size #122; it removes `damped_oscillator` from the list of tasks that could gate it.**

## What this bundle settles, and what it leaves open

- **Settled:** `damped_oscillator` at 10 iterations / 10 model calls cannot decide the quality row.
  Two of the three tasks in the family have now been measured and neither discriminates at this budget.
- **Settled:** the call-count mismatch is real and not a stub artifact.
- **Open, and blocking #122:** the gate campaign needs a task and budget where *some* arm improves on
  its seed. Until one exists, "we exceed them on search quality" is unmeasured, and the gate's own rule
  applies - engine changes must win on dev first, and a new pre-registration with new sealed tasks
  precedes any re-run.
