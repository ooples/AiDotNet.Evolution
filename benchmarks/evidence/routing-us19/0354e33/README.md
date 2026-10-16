# US-19 (#37): the router against the best single method, measured

`portfolio_study.py` already runs the registered comparison: every method on a development partition,
which **selects** the best static comparator per family, then every method again on a confirmation
partition, which **measures** against it. This bundle pairs the adaptive router
(`adaptive-archive`) against that selection by (family, seed) - 16 confirmation seeds per family,
budget 64, no model and no spend.

The comparator was selected on development and copied from the study's own claim file, together with
the hash of the development rows it came from: `{"kernel": "restart", "numeric": "restart",
"program": "crossover"}`.

## Results

| family | comparator | quality difference | 95% CI | calls vs comparator | 95% CI |
| --- | --- | ---: | --- | ---: | --- |
| kernel | restart | -0.0039 | [-0.0105, +0.0027] | **x0.9861** | [0.9645, 1.0057] |
| numeric | restart | -0.0153 | [-0.0557, +0.0263] | **x1.0521** | [1.0399, 1.0625] |
| program | crossover | +0.0345 | [-0.0320, +0.1069] | **x1.0521** | [1.0399, 1.0625] |

**Quality is non-inferior on all three families** - every interval straddles zero, so the router
matches its comparator rather than beating it.

**The call-reduction target is not supported.** US-19 declares `<= 0.75x` the calls of the best single
model; measured, the router spends about **5% more** calls on two families and 1% fewer on the third.
`test_the_call_reduction_target_is_not_supported_by_this_evidence` pins that reading.

`numeric` and `program` share a call ratio because they produce identical per-seed call counts under
the same seeds and budget - the schedule, not a copied row. Their qualities differ, and a test
asserts both facts so the coincidence cannot be mistaken for an error.

## What this instrument cannot say

This study routes **operators**, and every operator costs the same per call. US-19 is about routing
**models** by measured return, where the saving comes from a cheap-model / expensive-model mix - a
difference this workload cannot express at all, because there is no model and therefore no price.
So what is measured here is the router's **overhead and quality parity**; the `0.75x` claim needs the
gate campaign (#122) with real models at real prices. `summary.json` carries `"TokensMeasured": false`.

## Reproducing

```text
python benchmarks/analysis/portfolio_study.py register <quality.dll> plan.json --count 8 --budget 64
python benchmarks/analysis/portfolio_study.py execute plan.json out development
python benchmarks/analysis/portfolio_study.py execute plan.json out confirmation
python benchmarks/analysis/routing_study.py rows-compact.json
```

The study's `confirmation.json` is **27,464,688 bytes**, so the bundle commits the ten fields the
analysis reads for the four methods involved (192 rows) plus that file's SHA-256;
`benchmarks/analysis/test_routing_study.py` regenerates `summary.json` from it.
