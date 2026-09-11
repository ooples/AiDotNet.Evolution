# External numeric development pilot

Source: `921721bbfa88c15be4bbfe703b0222e0eea9966d`; September 10, 2026. The campaign contains 280 scheduled runs
(seven methods × four tasks × ten paired seeds), **71,528 evaluator calls and zero failures**. This is development
evidence, not held-out or representative confirmation. No model API or rented compute was used.

The [compact evidence](external-921721b.json) retains every run, independent counters, environment/configuration,
binary identity and full-trace SHA-256. The [analysis](analysis-921721b/report.md) and its
[retrospective specification](external-921721b.plan.json) retain failures in the endpoint and uncertainty calculation.
The complete external campaign replays byte-identically: raw SHA-256
`ef53b38c0048dc081125c8875e51e6983345be713e58db98e2562ac115572700`.
All 240 core run records exactly match the prior `d62d5cb` pilot after extracting the shared evaluator: the comparison
did not silently change the original objectives, starting populations or core trajectories.

## Result

Median final loss; lower is better. Each cell contains ten completed runs, not timing repetitions.

| Development objective | Hill climb | Diagonal CMA | SciPy DE, matched eight-member population |
| --- | ---: | ---: | ---: |
| Sphere | 0.360571 | 0.305817 | 0.289404 |
| Shifted quadratic | 0.750448 | 0.342898 | 0.268118 |
| Anisotropic quadratic | 36.357121 | 16.948544 | 10.770310 |
| Rippled quadratic | 35.894749 | 44.742507 | 28.432344 |

SciPy has lower median loss than the emitter on all four fixtures. The task-balanced paired utility difference
`CMA − SciPy` is **−0.0225513**, with approximate multiplicity-adjusted interval **[−0.0843922, 0.0490803]**. It crosses
zero; neither a statistically confirmed SciPy win nor a CMA superiority claim follows. The `CMA − hill climb`
interval also crosses zero. These intervals adjust for this report's two comparisons, unlike the earlier five-
comparison report; changing the comparison family changes the tails, not the underlying core observations.

The external baseline used 10,088 of its 10,240-call cap. Four runs converged normally at 184, 216, 224 and 248 calls.
All retain successful status and their actual charges. Progress carries the final measured incumbent forward without
inventing observations. The other six methods used 61,440 calls; exact evaluator/initialization and accounting checks
apply to both sides. See [protocol and commands](../../benchmarks/external/README.md).

## Decision and remaining work

Keep the external baseline in future campaigns and keep CMA/adaptation opt-in. Internal baselines alone overstated
how encouraging the earlier medians were. Improve search policies against this stronger control, then validate on
new task families; do not tune these four development tasks and call the resulting gain held-out performance.

The matched population is deliberately eight, not SciPy's native dimension-scaled default; polishing is disabled.
The external configuration was not tuned, and core settings were frozen from the prior pilot; earlier core
development is not a matched tuning-budget experiment. Separate native-default/tuned controls, representative
program/kernel/AutoML and noisy tasks, OpenEvolve with identical model access, prospective sample-size design and
full runtime/memory costs remain open. Shared C# subprocess overhead is not a runtime-speed comparison.
