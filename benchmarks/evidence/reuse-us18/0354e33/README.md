# US-18 (#36): what novelty detection and experience reuse suppress, measured

The feature-ablation campaign in `benchmarks/AiDotNet.Evolution.Quality` runs the whole fixed matrix
on the development partition. This bundle takes the two arms that differ only in novelty detection
and experience reuse - `baseline` and `novelty` - and pairs them by (family, seed): 16 seeds x 3
families = **48 pairs**, budget 64 evaluations, no model and no spend.

## Results

| measured | value | 95% CI | reading |
| --- | ---: | --- | --- |
| suppressed-proposal rate | **+27.25 points** | [+21.28, +33.20] | 10.80% -> 38.04% of proposals never cost an evaluation |
| quality | **+0.0124** | [-0.0022, +0.0272] | **non-inferior**, not better: the interval straddles zero |
| calls to target | **+1.53** | [-0.21, +3.94] | **the claim is not supported**: the point estimate is the wrong way round, over the 34 pairs where both arms reached the target |

The third row is the one worth reading twice. US-18's second metric row asks for *fewer* tokens to
target; on this workload the mechanism reaches the target in slightly **more** evaluator calls, and
the interval includes no difference at all. Suppressing a duplicate saves the evaluation it would
have cost, but it does not, here, shorten the path to the target.

Intervals are bootstrap intervals over pairs (2,000 resamples, seed 18, alpha 0.05) from
`analyze.interval`, the estimator the pilot uses.

## What this instrument cannot say

- **Tokens are not measured.** These workloads are numeric, program-shaped and kernel-shaped
  objectives with no model in the loop, so there is no token to count; evaluator calls are the unit.
  `summary.json` carries `"TokensMeasured": false`.
- **The suppressed-proposal rate is not the generated-duplicate rate.** It counts proposals that
  never reached the evaluator because they repeated, or were too close to, something already in the
  archive. Whether an operator *generates* fewer duplicates is a property of the operator, which
  this mechanism does not change - `"GeneratedDuplicateRateMeasured": false`.
- So the story's `-30%` duplicate-proposal target is **not** settled by this bundle. What is settled
  is that the mechanism suppresses duplicate work at no cost in quality.

## Reproducing

```text
dotnet build benchmarks/AiDotNet.Evolution.Quality -c Release
python benchmarks/analysis/ablation.py register benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll plan.json --seeds 16 --budget 64
python benchmarks/analysis/ablation.py execute plan.json out development
python benchmarks/analysis/reuse_ablation.py rows-compact.json
```

The campaign's own `development.json` is **37,732,013 bytes** of per-evaluation observations, so it
is not committed; `rows-compact.json` carries the fields the analysis reads plus that file's
SHA-256, and `benchmarks/analysis/test_reuse_ablation.py` regenerates `summary.json` from it.
