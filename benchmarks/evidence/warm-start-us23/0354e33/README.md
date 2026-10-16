# US-23 (#41): what a warm start saves, in evaluations

`benchmarks/EvolutionWarmStart` runs three phases per seed on a rippled-quadratic objective - one
broad basin with local structure, so progress is gradual rather than instant - over 8 seeds, with no
model and no spend:

| phase | what it is |
| --- | --- |
| **prefix** | half the budget (60 evaluations), checkpointing. The work a previous run already paid for. |
| **cold** | a fresh run with the full budget (120). Its final best quality is **the bar**, and the evaluation at which it first reached that bar is what reaching it cost from nothing. |
| **warm** | a fresh engine resuming the prefix checkpoint, charged **only for evaluations after the resume**. |

## Result

| measured | value | 95% CI |
| --- | ---: | --- |
| evaluations to the cold bar, warm / cold | **0.4108** | [0.2741, 0.4872] |

Over the 6 seeds that still had work to do after resuming. **2 of 8 seeds met the bar on resume at
no cost at all** - the restored state already matched what the cold run needed its whole budget to
reach - and those are counted separately rather than averaged in, because their zeros would flatter
the mechanism on exactly the seeds where it did most. Worst seed 0.5000; **8 of 8 within the story's
0.50 target**, with the interval's upper bound (0.4872) under it too.

Cold runs reached their own final quality anywhere from **10 to 121** evaluations in, which is why
this is paired by seed rather than averaged across arms - and a test pins that spread.

## What this instrument cannot say

**Tokens are not measured.** The objective is numeric and there is no model in the loop, so an
evaluation is the unit the budget is spent in; `summary.json` carries `"TokensMeasured": false`. The
story's row is stated in tokens, and a token reading needs the gate campaign (#122).

## One correction worth recording

The first version of this harness started the warm run's best at negative infinity, ignoring the
state it had just restored - so the two seeds whose restored state *already met the bar* were
reported as runs that **never reached it**, the exact opposite of the truth. The warm run now starts
from the prefix's best, and `test_seeds_that_met_the_bar_on_resume_are_counted_apart_from_the_ratio`
asserts those seeds both charge zero and genuinely meet the bar.

## Reproducing

```text
dotnet run --project benchmarks/EvolutionWarmStart -c Release -- --output rows.json
python benchmarks/analysis/warm_start.py rows.json
```
