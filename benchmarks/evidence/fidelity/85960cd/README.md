# US-15 current-stack fidelity and recovery verification

Runtime: `85960cd`. Fixed campaigns: 32 seeds, 128 actual-training runs plus
128 authored learning-curve runs. All 256 completed without invalid evidence;
full byte-identical replay also passed. Primary campaigns made 9,216 objective
calls; replay another 9,216. Three-process recovery adds 64 calls across its
uninterrupted baseline, paused prefix and resumed suffix. No model/API calls.

## Actual training: quality and declared work

Each candidate trains a four-feature regression model by gradient descent using
4/16/64 epochs. Search and full-fidelity confirmation use separate data and
initialization. Confirmation retrains from scratch, without a search token.
Every method starts with the same eight candidates and a 2,100-unit cap.

| Method | Mean confirmed quality | Epochs/run | Declared cost/run |
|---|---:|---:|---:|
| Full cohort | 0.999139370254 | 2,048 | 2,064.08 |
| Restart-only halving | 0.999139370254 | 704 | 712.08 |
| Incremental greedy halving | 0.999139370254 | 608 | 616.08 |
| Incremental exploratory halving | 0.999139329285 | 608 | 616.08 |

Greedy matches full-cohort and restart-only confirmed quality on all 32 seeds:
**70.15% fewer declared work units than full cohort**, and **13.48% fewer than
restart-only halving**. These reproduce the earlier authored-pilot result;
integration has not established a new quality improvement. Costs include actual
epochs, a declared scoring/data/token tariff and setup. They are not measured
wall-clock, energy or monetary costs. Equal caps are not equal actual spending;
full cohort independently confirms more candidates (16 calls versus 4).

Exploration loses a small amount of regression quality on 14/32 seeds and ties
on 18. Mean paired difference versus greedy is -4.0969e-8. This adverse result is
retained; there is no universal exploration-policy recommendation.

## Premature rejection / late improvement

At equal 92.08-unit spending on the deliberately misleading curve, exploration
raises mean confirmed quality from 0.800719 to 0.886058, winning 28/32 seeds.
Mean paired difference +0.085339, unadjusted 95% seed-bootstrap interval
[+0.058925,+0.109033]. On the aligned curve it loses in 19/32 seeds; its mean
difference interval includes zero. These authored stress tests demonstrate the
tradeoff, not representative superiority or an OpenEvolve result.

## Actual process restart

`recovery.zip` contains three distinct-process artifacts: baseline, paused start,
and resume. The 18-call/88-epoch prefix plus 14-call/520-epoch suffix equals the
uninterrupted 32-call/608-epoch run. Final reports, ledger receipts and original
measurement sequences match exactly; no reevaluation or duplicate charges.

SHA256: `e81a4a1bb8b9b99431e36c45a23876c5c162034e993034d03d06b44513cf630e`.
This is coordinated settled-boundary recovery, not arbitrary in-flight crash
recovery. Checkpoint storage must be trusted; checksums do not authenticate data.
Checkpoint filesystem/startup costs are not automatically included in work tariffs.

The `trained` and `curves` directories retain complete gzip raw measurements,
receipts, paired analyses and hashes. Tests verify both historical and current
artifact chains, recompute analyses, and retain invalid runs with worst-support
penalties. No tuning was performed after these results. Default policies unchanged.
