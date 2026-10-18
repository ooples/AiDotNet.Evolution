# V1-40 (#121/#122): `rational_gain` at a deeper per-cell budget - both endpoints superior

18 cells - 6 paired seeds x 3 arms, Williams balanced - on the `rational_gain` dev partition, through
the metered broker against the real subscription CLI. **460 model calls, 57.3 minutes, 18 of 18 cells
`completed`, zero failures, receipt coverage 1.0, zero API-key calls, 1,257,973 broker-counted tokens
over 462 receipts** (460 cell calls and 2 canaries).

This run changes **one thing** against the first pilot (`../../pilot-v1-07/aafd609`): the per-cell
budget, from 10 iterations and a 10-call ceiling to **25 iterations and a 35-call ceiling**. Same task,
**same holdout seed 20261015**, same sealed partition, same 0.05 sizing target, same three arms. The
seeds differ because each registration draws its own.

## The result

| endpoint | mean difference (positive favours us) | CI95 | one-sided p | Holm p | superior |
| --- | ---: | ---: | ---: | ---: | --- |
| `openevolve-recommended` | **+0.6599** | [+0.2802, +0.8766] | 0.00200 | **0.00400** | **yes** |
| `openevolve-matched` | **+0.2211** | [+0.0106, +0.4351] | 0.01399 | **0.01399** | **yes** |

Both intervals exclude zero and both reject after Holm correction at a 0.05 familywise alpha, so
`report.json` carries `OpenEvolveGate: true`. At 10 iterations the same endpoints were +0.3327 at Holm
0.178 and +0.0260 at Holm 0.412, neither superior. **The budget, not the engine, is what changed.**

Held-out NMSE per seed, lower better, scored outside every search process from a seed no search was told:

| seed | ours | recommended | matched | cell winner |
| --- | ---: | ---: | ---: | --- |
| 1622683038 | **0.2753** | 1.0790 | 0.5553 | ours |
| 2740129864 | **0.2686** | 1.0790 | 0.3030 | ours |
| 2789894567 | **0.1435** | 1.0790 | 0.1773 | ours |
| 314009189 | **0.2473** | 1.0790 | 0.6515 | ours |
| 2769294546 | 0.2366 | 1.0790 | **0.1289** | matched |
| 3301637546 | 0.3970 | **0.1328** | 1.0790 | recommended |

**Cells won: 4 / 1 / 1**, against 2 / 2 / 2 at the shallower budget.

## Read the two endpoints differently

`1.0790` is the value a run that never improved on its seed lands on. It appears in **5 of 6**
`recommended` cells, so most of that +0.6599 is their arm not moving rather than our arm excelling. The
`recommended` endpoint is the weaker evidence of the two despite the larger number.

**`matched` is the endpoint that carries the claim.** It improved in 5 of its 6 cells and we still beat
it - but narrowly: the CI95 lower bound is **+0.0106**, a hair above parity. That is a real win and a
small one, and it should be quoted with its interval rather than as "+0.2211".

What is solid in both is **consistency**: ours spans 0.1435-0.3970 across the six seeds, while
`recommended` spans 0.1328-1.0790 and `matched` 0.1289-1.0790. Both of their configurations produce the
best single cell in the table and also produce runs that never move; ours does neither.

## The caveat that does not go away

A **model-free** searcher on this task, at 20,000 evaluations, reaches held-out NMSE **0.0528-0.1015**
and closes 89.9-94.8% of the gap (`../../symbolic-regression/e083c3b/headroom.json`). Our arm reaches
0.1435-0.3970 at 25 iterations. The budgets are not comparable - the floor spends ~800x the evaluations
and no model calls at all - but the honest scope of this bundle is therefore:

> **better than OpenEvolve's search at an equal model-call budget on this task**, not "good search".

Anyone quoting this result without that sentence is overstating it.

## What the sizing says, and what it does not

| endpoint | sd | observed half width | required N |
| --- | ---: | ---: | ---: |
| `recommended` | 0.4552 | 0.2982 | 319 |
| `matched` | 0.2924 | 0.2122 | 132 |

**Campaign N = 319**, down from the first pilot's 514. Two things must not be conflated:

- **Significance** - the gate's actual acceptance criterion, strict superiority after Holm - is already
  met here at **n = 6**.
- **The 0.05 CI half width** the pre-registration declared is a *precision* target, and reaching it
  needs N = 319 seeds: 319 x 3 arms x 35 calls is a ceiling of **33,495 model calls** for one family.
  Deepening the cells lowered the required N and raised the total cost.

## The metering fix was not exercised by this run

Calls per cell ran **25 to 29 against a ceiling of 35**, so retries happened and **no cell reached the
ceiling**. That is why there are no failures - the budget had headroom, not because a refusal was
handled. The served-vs-attempted fix (#333) is verified by its own tests; this run avoided the
condition rather than surviving it. The lesson for the gate campaign is the budget shape: **a call
ceiling equal to the iteration count turns a retried answer into a failed cell**, which is what cost 2
of 6 cells on `damped_oscillator`.

## Reproducing the scored partition

```text
run_pilot_campaign.py --task rational_gain --seed-count 6 --iterations 25
    --model-calls 35 --model-tokens 600000 --wall-seconds 2400 --cell-timeout 2400
    --target-half-width 0.05 --holdout-seed 20261015
    --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll
    --upstream <openevolve @ 411fb59c886c> --executable <claude.exe 2.1.299>
    --confirm 8f8c6c839007930bbd8624865f3aded3d0afd432e3dbffceebfdbc49de3954b2
```

Harness at `42d0d5c` (the served-vs-attempted metering fix). `pilot_cells.holdout_seal("rational_gain",
20261015)` reproduces the sealed partition hash `registration.json` committed before anything ran.

## Scope

One family of three, on its **dev** partition, against OpenEvolve only. The gate (#122) needs every
family x config plus the AlphaEvolve set, on **new sealed test tasks** under a fresh pre-registration.
This bundle is a sizing pilot that now has a measurable effect to size; it is not the gate.
