# V1-04d (#113): dev-data headroom, and which tasks can carry a claim

`headroom.json` is the raw output of
[`benchmarks/external/symbolic_regression_headroom.py`](../../../external/symbolic_regression_headroom.py)
at `--budget 20000`, main `e083c3b`. **No model calls.**

The ground truth lives in the family module, so *"a perfect candidate exists"* is true by construction
and demonstrates nothing. What is shown here is that a deliberately weak search finds candidates
materially better than the seed **while never seeing the test split** - and how much room it leaves.

| task | gap closed on test (3 dev seeds) | test NMSE spread over 8 seeds | effect / spread |
| --- | --- | ---: | ---: |
| `rational_gain` | **+0.899 to +0.947** | 0.049 | the floor already solves it; nothing left to win |
| `damped_oscillator` | **+0.292 to +0.322** | **0.124** | **~2.4x - the task to compete on** |
| `two_variable_ratio` | -0.006 to -0.000 | 0.112 | the floor cannot touch it at all |

`gap_closed` is `1 - found_test_nmse / baseline_test_nmse`, where the baseline predicts the training
mean and so scores NMSE 1.0 by construction. The spread column is the same floor across **8
consecutive seeds**, one arm against itself: a between-arm difference smaller than it is seed noise,
not a result.

## The held-out split had to be ten times the training split, and that was measured

An earlier revision of this card used 40 held-out rows and nominated `damped_oscillator` from the
headroom column alone. An eight-seed sweep retracted that: its test NMSE swung **1.133**, far larger
than the ~0.3 of headroom the task has, so no comparison on it could have meant anything.

The cause was sampling noise in the held-out draw, not the task, and it collapses with size - same
floor, same seeds, budget 4000:

| held-out rows | `damped_oscillator` | `rational_gain` | `two_variable_ratio` |
| ---: | ---: | ---: | ---: |
| 40 | 1.133 | 0.155 | 0.294 |
| 120 | 0.305 | 0.119 | 0.104 |
| **400** | **0.183** | 0.145 | 0.112 |
| 1200 | 0.166 | 0.134 | 0.170 |

400 is where it flattens, so `build` defaults there. What remains after that is the **searcher's own**
seed-to-seed variation - `rational_gain` barely moves across the sweep, because its residual spread was
never sampling noise - and that is what more seeds address, not a bigger test split.

With the split at 400 the nomination holds with a margin: `damped_oscillator`'s effect is about 2.4x
its spread, where before it was about half.

This is still a measurement, not a pre-registration input: one arm against itself, an arm that calls a
model may be noisier, and the sample count for any real campaign comes from V1-40's power analysis
(#121).

## Why the searcher is deliberately bad

Seeded random enumeration over a fixed 8-function basis, up to three terms, no gradients and no tuning.
A weak floor is the point: if a random enumeration already closes 90% of the gap, as it does on
`rational_gain`, then a result on that task says little about the searcher that produced it.

**`two_variable_ratio` is the opposite failure, and worth stating.** Its truth is
`xy / (1 + x^2 + y^2)`, and the basis has only single-variable functions, so the floor is stuck at a
constant - its best expressions are things like `-1*1 + 1`. A task the floor cannot touch may be
measuring the floor rather than a difference between searchers, which is the same hazard as a task it
already solves, from the other side.

So the floor orders the tasks by distance - `rational_gain`, then `damped_oscillator`, then
`two_variable_ratio` - and the noise column decides which of them that ordering can be trusted on.

## How the held-out number is kept held out

Selection uses train and validation only. The test split is scored **once**, at the end, through
`family.score`, which refuses a split whose seal no longer matches - so the reported test NMSE cannot
have steered the search that produced it. That is asserted, not described:
`test_program_symbolic_regression_headroom.py` tampers with the held-out rows before the search and
requires the chosen candidate to be unchanged.

## A limit of this family, found while wiring it to the gate

`benchmarks/analysis/release_gate.py` already names this family `sr` in its fixtures, and seals a
family's test partition two ways: the opened bytes must hash to `TestPartitionHashes[family]`, and
**no blob in the registration commit's tree may have exactly those bytes** (`partition_was_visible`).

`partition()` emits that shape, but the second check is not sufficient for *this* family, and the
module says so. Its rows are a pure function of a seed with a closed form, so a partition built from
a committed seed - `DEV_SEEDS`, or anything else in the tree - would pass a byte-visibility check
while being public in substance. A campaign must therefore choose its test seeds **at registration
time** and keep both the seeds and the partition out of the repository until the bundle is opened.

That is a property of generated data, not a defect in the gate: a family whose test partition is a
fetched dataset cannot be rebuilt from the tree, and the byte check is enough for it.

## What this is not

Not the LLM-SRBench subset. `deep-symbolic-mathematics/llm-srbench` declares no license, so it is not
pinned here; the tasks are an in-repo family with the same shape. **#113's first acceptance box stays
unticked.**

Not a comparison against OpenEvolve, and not a release claim. The metric row (*held-out NMSE vs
OpenEvolve, CI-better*) needs the pre-registered gate campaign; this says only that two of three dev
tasks have room left to compete over, and names which.
