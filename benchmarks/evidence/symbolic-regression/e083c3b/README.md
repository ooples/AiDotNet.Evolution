# V1-04d (#113): dev-data headroom, and which tasks can carry a claim

`headroom.json` is the raw output of
[`benchmarks/external/symbolic_regression_headroom.py`](../../../external/symbolic_regression_headroom.py)
at `--budget 20000`, main `e083c3b`. **No model calls.**

The ground truth lives in the family module, so *"a perfect candidate exists"* is true by construction
and demonstrates nothing. What is shown here is that a deliberately weak search finds candidates
materially better than the seed **while never seeing the test split** - and how much room it leaves.

| task | gap closed on test (3 dev seeds) | reading |
| --- | --- | --- |
| `rational_gain` | **+0.902 to +0.938** | the floor already solves it; it cannot carry a claim about a better searcher |
| `damped_oscillator` | **+0.292 to +0.484** | **headroom, and the task to compete on** |
| `two_variable_ratio` | **-0.025 to -0.000** | the floor cannot touch it at all |

`gap_closed` is `1 - found_test_nmse / baseline_test_nmse`, where the baseline predicts the training
mean and so scores NMSE 1.0 by construction.

## Why the searcher is deliberately bad

Seeded random enumeration over a fixed 8-function basis, up to three terms, no gradients and no tuning.
A weak floor is the point: if a random enumeration already closes 90% of the gap, as it does on
`rational_gain`, then a result on that task says little about the searcher that produced it.

**`two_variable_ratio` is the opposite failure, and worth stating.** Its truth is
`xy / (1 + x^2 + y^2)`, and the basis has only single-variable functions, so the floor is stuck at a
constant - its best expressions are things like `-1*1 + 1`. A task the floor cannot touch may be
measuring the floor rather than a difference between searchers, which is the same hazard as a task it
already solves, from the other side.

So the honest selection for a future comparison is `damped_oscillator`: the floor reaches it but does
not solve it.

## How the held-out number is kept held out

Selection uses train and validation only. The test split is scored **once**, at the end, through
`family.score`, which refuses a split whose seal no longer matches - so the reported test NMSE cannot
have steered the search that produced it. That is asserted, not described:
`test_program_symbolic_regression_headroom.py` tampers with the held-out rows before the search and
requires the chosen candidate to be unchanged.

## What this is not

Not the LLM-SRBench subset. `deep-symbolic-mathematics/llm-srbench` declares no license, so it is not
pinned here; the tasks are an in-repo family with the same shape. **#113's first acceptance box stays
unticked.**

Not a comparison against OpenEvolve, and not a release claim. The metric row (*held-out NMSE vs
OpenEvolve, CI-better*) needs the pre-registered gate campaign; this says only that two of three dev
tasks have room left to compete over, and names which.
