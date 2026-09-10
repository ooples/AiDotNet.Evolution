# Numeric development analysis

Source: `d62d5cbd5918122f7aac92bb512b49948e52ea25`.

Retrospective only. No confirmatory or competitive-win claim.

Endpoint: mean task-balanced paired difference in scale/(scale+final loss); failures have utility 0.

Intervals use 5000 paired bootstrap replicates, with 95% nominal family-wise confidence after adjusting aggregate comparisons.
Tasks are fixed; intervals describe seed variability on this suite only.

## Paired effects

Positive differences favor the primary method. Failed, incomplete and missing runs remain in the denominator.

| Primary | Comparator | Utility difference | Adjusted bootstrap interval |
| --- | --- | ---: | --- |
| DiagonalCma | HillClimb | 0.0624892 | [-0.0262443, 0.156583] |
| DiagonalCma | RandomSearch | 0.359818 | [0.334258, 0.384524] |
| DiagonalCma | FixedMapElites | 0.372907 | [0.347246, 0.398293] |
| DiagonalCma | AdaptiveMapElites | 0.354301 | [0.328296, 0.379777] |
| DiagonalCma | UniformPortfolioMapElites | 0.365101 | [0.340985, 0.39021] |

## Every task and method

Loss medians below are completed-only; utility includes every scheduled run.

| Task | Method | Completed / planned | Median loss | Mean utility | Incomplete traces |
| --- | --- | ---: | ---: | ---: | ---: |
| Sphere | RandomSearch | 10 / 10 | 16.2584 | 0.0605657 | 0 |
| Sphere | HillClimb | 10 / 10 | 0.360571 | 0.651008 | 0 |
| Sphere | FixedMapElites | 10 / 10 | 25.6772 | 0.0391665 | 0 |
| Sphere | AdaptiveMapElites | 10 / 10 | 16.1701 | 0.0776336 | 0 |
| Sphere | UniformPortfolioMapElites | 10 / 10 | 18.9426 | 0.0542047 | 0 |
| Sphere | DiagonalCma | 10 / 10 | 0.305817 | 0.75129 | 0 |
| ShiftedQuadratic | RandomSearch | 10 / 10 | 16.2334 | 0.0748199 | 0 |
| ShiftedQuadratic | HillClimb | 10 / 10 | 0.750448 | 0.625113 | 0 |
| ShiftedQuadratic | FixedMapElites | 10 / 10 | 29.893 | 0.0444827 | 0 |
| ShiftedQuadratic | AdaptiveMapElites | 10 / 10 | 15.0947 | 0.0786976 | 0 |
| ShiftedQuadratic | UniformPortfolioMapElites | 10 / 10 | 20.0586 | 0.0600852 | 0 |
| ShiftedQuadratic | DiagonalCma | 10 / 10 | 0.342898 | 0.74532 | 0 |
| AnisotropicQuadratic | RandomSearch | 10 / 10 | 247.862 | 0.00476759 | 0 |
| AnisotropicQuadratic | HillClimb | 10 / 10 | 36.3571 | 0.0434715 | 0 |
| AnisotropicQuadratic | FixedMapElites | 10 / 10 | 629.894 | 0.0023844 | 0 |
| AnisotropicQuadratic | AdaptiveMapElites | 10 / 10 | 200.231 | 0.00544175 | 0 |
| AnisotropicQuadratic | UniformPortfolioMapElites | 10 / 10 | 246.949 | 0.00509361 | 0 |
| AnisotropicQuadratic | DiagonalCma | 10 / 10 | 16.9485 | 0.0745495 | 0 |
| RippledQuadratic | RandomSearch | 10 / 10 | 69.2167 | 0.0154444 | 0 |
| RippledQuadratic | HillClimb | 10 / 10 | 35.8947 | 0.0253218 | 0 |
| RippledQuadratic | FixedMapElites | 10 / 10 | 60.7968 | 0.01721 | 0 |
| RippledQuadratic | AdaptiveMapElites | 10 / 10 | 63.2529 | 0.0158915 | 0 |
| RippledQuadratic | UniformPortfolioMapElites | 10 / 10 | 68.0234 | 0.0150841 | 0 |
| RippledQuadratic | DiagonalCma | 10 / 10 | 44.7425 | 0.0237108 | 0 |

## Failed or missing runs

None.

## Interpretation limits

- Retrospective development analysis; no preregistration, power guarantee, release gate or competitor superiority claim.
- Timing samples and trajectory points are not independent search runs.
- Intervals are approximate; small or degenerate samples can understate uncertainty.
- Completed-only losses and known-only curves are descriptive, not failure-inclusive comparison endpoints.
- Task resampling does not make a purposively selected suite representative.

Task-level exploratory intervals, budget-indexed progress, independent resource counters and every run are in `report.json`.
Full unaggregated trajectories remain in the original input, identified by its SHA-256 in the report.
