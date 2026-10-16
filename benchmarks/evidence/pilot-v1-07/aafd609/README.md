# V1-07 (#116): the pilot campaign, run

18 cells - 6 paired seeds x 3 arms, Williams balanced - on the `rational_gain` dev partition, through
the metered broker against the real subscription CLI. **180 model calls, 26.3 minutes, every cell
`completed`, receipt coverage 1.0, zero API-key calls.** Raw beside this file: `rows.json`,
`report.json`, `power-analysis.json`, `summary.json`, the frozen `registration.json`, and
`receipts-compact.json` (the 182 receipts' identity and usage; the bodies are 3.8 MB and are not
committed).

Launched by `benchmarks/external/run_pilot_campaign.py` with the printed `ProjectionSha256`
`7042534e58c080745fea847c0ef66367080d6c98c8e982f42c646cad73e6d7cd` handed back, against registration
`abb44f490d86` whose seeds were drawn before anything ran.

**The scored partition is reproducible from these inputs**, which the sealed hash alone does not
give a reader:

```text
run_pilot_campaign.py --task rational_gain --seed-count 6 --iterations 10     --model-calls 10 --model-tokens 600000 --wall-seconds 1800     --target-half-width 0.05 --holdout-seed 20261015     --cli dotnet <repo>/src/AiDotNet.Evolution.Cli/bin/Release/net10.0/aidotnet-evolve.dll     --upstream <openevolve @ 411fb59c886c> --executable <claude.exe 2.1.299>
```

`pilot_cells.holdout_seal("rational_gain", 20261015)` reproduces the sealed partition hash
`d857373eebc0df79...`, which is the value `registration.json` committed before the run. The six
paired seeds the registration drew are in `registration.json`; nothing here chose them.

## The result: ahead on the mean, tied on the cells, significant nowhere

The endpoint **minimises** NMSE, so `analyze` orients the paired difference as *theirs minus ours*:
positive means our held-out error is lower. The raw values in the table below it are NMSE, where
lower is better - the two columns read in opposite directions by design.

| endpoint | mean difference (oriented so positive favours us) | one-sided p | superior |
| --- | ---: | ---: | --- |
| `openevolve-recommended` | **+0.3327** | 0.0889 | **no** |
| `openevolve-matched` | **+0.0260** | 0.4116 | **no** |

Held-out NMSE per seed, lower better, scored outside every search process from a holdout seed no
search was told:

| seed | ours | recommended | matched | cell winner |
| --- | ---: | ---: | ---: | --- |
| 149773946 | 0.2812 | 1.0790 | **0.1328** | matched |
| 283099245 | 0.2708 | 1.0790 | **0.2120** | matched |
| 782783836 | 0.7562 | **0.2705** | 0.3998 | recommended |
| 1840865027 | **0.2507** | 1.0790 | 0.4619 | ours |
| 2095417139 | 0.3545 | **0.1328** | 0.7635 | recommended |
| 2700339690 | **0.1754** | 0.4443 | 0.2748 | ours |

**Cells won: 2 / 2 / 2.** The mean favours us because `recommended` is bimodal - three of its six
runs sit at **1.0790**, which is where a run that never improved on the seed lands, while two of the
other three are the best cells in the table. Ours is the consistent arm (0.1754 to 0.7562, median
~0.28) and wins no more cells than either competitor. A reader who took `+0.3327` as "we are better"
would be reading one arm's failures as our skill.

**Equal model-call counts held exactly**: every one of the 18 cells spent **10** model calls, the
per-cell broker ceiling, so no arm bought its result with extra calls. Token spend did vary -
21,486 to 35,361 per cell, median 25,933 - because a call's cost depends on the prompt each engine
builds, so this is equality of the budgeted unit rather than of tokens.

## What this sizes: N = 514, which is the real finding

| endpoint | sd of paired differences | observed 95% half width | required N for 0.05 | scaled | normal | agreement |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| `recommended` | 0.5778 | 0.4118 | **514** | 407 | 514 | 1.26 |
| `matched` | 0.2721 | 0.2045 | **114** | 101 | 114 | 1.13 |

The gate campaign is sized by its widest endpoint, so **514 paired seeds** for the declared 0.05 CI
half width - against 6 here. Both estimators agree to within 26%, so this is not an artefact of
one: the endpoint is simply noisy, mostly because `recommended` either solves the task or does not.
At this campaign's rate (10 calls per cell, 3 arms) 514 seeds is **15,420 model calls**.

That is the number #121 needed and it is a planning problem, not a code one. Three honest routes:
widen the declared CI target, reduce the endpoint's variance (more iterations per cell, or a task
whose failure mode is less binary), or accept a much larger campaign. Nothing here chooses.

## What this run is not

- **One family, one task.** `rational_gain` only, which the V1-04d headroom card picked as already
  solved by a weak floor - so it flatters every arm and is the least discriminating of the three.
  `damped_oscillator` was the task that card nominated to compete on.
- **Not a verdict.** Ties fail by the registered decision rule, and both endpoints are ties. The
  campaign machinery's ability to report a win is proven separately, by a positive control in
  `test_program_pilot_campaign.py` rather than by this run.
- **10 iterations per cell**, which is what the budget bought, not what the engines want.
- **Measured cost ~2,661 tokens per call**, far under the 40,000 the runner's planning constant
  assumed *at the time of this run* - 479k tokens for the whole campaign rather than the projected
  7.2M. That measurement is why `TOKENS_PER_CALL` is now 8,000, so the 7.2M projection above
  reproduces only with the historical 40,000: today's constant projects 1.4M for the same 180 calls.
