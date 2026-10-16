# V1-04d (#113): dev-data headroom, and which tasks can carry a claim

`headroom.json` is the raw output of
[`benchmarks/external/symbolic_regression_headroom.py`](../../../external/symbolic_regression_headroom.py)
at `--budget 20000`, main `e083c3b`. **No model calls.**

The ground truth lives in the family module, so *"a perfect candidate exists"* is true by construction
and demonstrates nothing. What is shown here is that a deliberately weak search finds candidates
materially better than the seed **while never seeing the test split** - and how much room it leaves.

| task | gap closed on test (3 dev seeds) | test NMSE spread over 8 seeds | effect / spread |
| --- | --- | ---: | ---: |
| `rational_gain` | **+0.899 to +0.948** | 0.049 | the floor already solves it; nothing left to win |
| `damped_oscillator` | **+0.300 to +0.322** | **0.124** | **~2.4x - the task to compete on** |
| `two_variable_ratio` | +0.001 to +0.041 | 0.112 | the floor cannot touch it at all |

**This bundle reproduces exactly.** The enumeration is seeded, so

```text
python benchmarks/external/symbolic_regression_headroom.py --budget 20000 --noise-seeds 8 --output fresh.json
```

produces a file byte-identical to `headroom.json` - verified at `main` `0354e33`, nine commits and
two months after the `e083c3b` the bundle is named for, which is what says the family, the splits,
the seals and the search are all unchanged **on the platform that produced it**.

**That qualifier is a defect, not a caveat.** `seal` digests each value to **17** significant digits
(`%.16e` keeps one digit before the point and sixteen after) and
the closed forms go through libm (`exp`, `sin`, `cos`), whose last bits differ between platforms, so
the committed seals rebuild on Windows - where they were measured - and not on CI's Linux. A sealed
partition that only verifies where it was produced is weaker than it looks, and the same `seal`
backs the pilot campaign's frozen `TestPartitionHashes`, so correcting it would move a hash a paid
pre-registration already committed to. `damped_oscillator` is the only task whose closed form goes through libm - `exp(-0.3 t) sin(2 t)`
- and the other two are polynomial ratios, reproducible bit for bit anywhere. CI measured exactly
that: **3 of 9** seals differ on Linux, all three of them `damped_oscillator`. So the tests assert
the invariant rather than skipping (a required gate that skips is a failure here, by design): the
committed seals must equal the ones the test pins, the two rational tasks must rebuild theirs on
every platform, and any seal that cannot be rebuilt must belong to that one transcendental task -
with a further assertion that it *is* the only one, so the carve-out cannot widen unnoticed. The
exposure itself is measured by `test_the_seal_binds_more_precision_than_libm_agrees_on`. Tracked on
#113.

The 20,000-candidate run takes about 56 seconds, so
`benchmarks/external/test_program_symbolic_regression_evidence.py` guards the cheap half of it in
CI: every committed `test_seal` is rebuilt from the family module, the quoted aggregates are
recomputed from the rows they summarise, `gap_closed` is re-derived from its two NMSE columns, and
the eligibility ordering between the three tasks is pinned.

`gap_closed` is `1 - found_test_nmse / baseline_test_nmse`, where the baseline predicts the
**training** mean. Review caught this card taking that mean from the held-out rows instead, which made
`baseline_test_nmse` exactly 1.0 for every row - it is the definition of this NMSE's denominator - so
every gap was measured against a baseline that had seen the test split. Corrected, the baselines run
**1.000529 to 1.048824** and the readings above move very little, because the two means are close for
tasks sampled from one distribution; the one visible change is `two_variable_ratio` crossing from
marginally negative to marginally positive. The spread column is the same floor across **8
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

## `two_variable_ratio` is dead for this basis, and that is a paired decision

The floor scores **1.000 to 1.112** on it - at or slightly worse than predicting the mean - because its
truth needs a product of two variables and the basis has only single-variable functions. A task where
both arms score 1.0 orders nothing, so as it stands this one cannot contribute to a comparison.

Probed, **not changed**: adding a single product term `c * x_i * x_j` to the sampler takes the floor to
**0.104 to 0.481** on the same eight seeds, a headroom of **+0.724** - but with a spread of **0.377**,
about half that headroom, so the task would arrive about as resolvable as `damped_oscillator` rather
than cleanly better.

That is left as a measurement because the basis and the task set are one decision, not two: enriching
the floor makes this task usable and simultaneously raises the bar a real searcher has to clear, and
`rational_gain` is already solved at the current strength. Whoever pre-registers the campaign should
pick both together.

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
