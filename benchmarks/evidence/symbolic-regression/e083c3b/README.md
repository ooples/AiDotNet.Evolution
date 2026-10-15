# V1-04d (#113): dev-data headroom, and which tasks can carry a claim

`headroom.json` is the raw output of
[`benchmarks/external/symbolic_regression_headroom.py`](../../../external/symbolic_regression_headroom.py)
at `--budget 20000`, main `e083c3b`. **No model calls.**

The ground truth lives in the family module, so *"a perfect candidate exists"* is true by construction
and demonstrates nothing. What is shown here is that a deliberately weak search finds candidates
materially better than the seed **while never seeing the test split** - and how much room it leaves.

| task | gap closed on test (3 dev seeds) | test NMSE spread over 8 seeds | reading |
| --- | --- | ---: | --- |
| `rational_gain` | **+0.902 to +0.938** | **0.093** | the floor already solves it; nothing left to compete over |
| `damped_oscillator` | **+0.292 to +0.484** | **0.875** | headroom, but **smaller than its own seed noise** |
| `two_variable_ratio` | **-0.025 to -0.000** | 0.294 | the floor cannot touch it at all |

`gap_closed` is `1 - found_test_nmse / baseline_test_nmse`, where the baseline predicts the training
mean and so scores NMSE 1.0 by construction. The spread column is the same floor across **8
consecutive seeds**, one arm against itself.

**No task here is ready to carry a comparison at three seeds, and the noise column is why.** An
earlier version of this card named `damped_oscillator` as the task to compete on, from the headroom
column alone. The sweep retracts that: its test NMSE runs **0.460 to 1.335** across eight seeds, so a
0.3 difference between two arms on that task is about half its own seed noise, and at three seeds
would be indistinguishable from the draw. `rational_gain` is the only tight one (0.093) and it has
almost nothing left to win. The family therefore needs more seeds per task, or a tighter searcher,
before a result on it means anything - which is a finding about this instrument, not about any
searcher.

This is a measurement, not a pre-registration input: it is one arm against itself, and an arm that
calls a model may be noisier still. The sample count for any real campaign comes from V1-40's power
analysis (#121).

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
`two_variable_ratio` - but ordering is not readiness: see the noise column above, which is why no task
is nominated here.

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
