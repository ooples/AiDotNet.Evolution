# Diagonal CMA-style development comparison

Source: `d62d5cbd5918122f7aac92bb512b49948e52ea25`. Six methods, four eight-dimensional objectives,
seeds 0–9 and 256 evaluator calls including eight identical initial genomes. All 240 runs completed:
61,440 evaluator calls, no failed runs. [Per-run evidence](numeric-pilot-d62d5cb.json) retains every seed,
resource totals, distribution statistics and the SHA-256 of the full trace. No model/API calls were made.

## Median final loss (lower is better)

| Task | Random | Hill climb | Fixed MAP-Elites | Adaptive portfolio | Uniform portfolio | Diagonal CMA |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sphere | 16.2584 | 0.3606 | 25.6772 | 16.1701 | 18.9426 | 0.3058 |
| Shifted quadratic | 16.2334 | 0.7504 | 29.8930 | 15.0947 | 20.0586 | 0.3429 |
| Anisotropic quadratic | 247.8624 | 36.3571 | 629.8937 | 200.2311 | 246.9495 | 16.9485 |
| Rippled quadratic | 69.2167 | 35.8947 | 60.7968 | 63.2529 | 68.0234 | 44.7425 |

The new emitter has lower median final loss than hill climbing on three fixtures, but loses to hill climbing
on the rippled objective. All five earlier methods reproduce the previous pilot's final-loss medians.
This is a descriptive development comparison, not independent confirmation, a significance test or evidence
of superiority to OpenEvolve. No runtime-speedup claim follows from these synthetic scores.

The emitter uses positive-weight diagonal covariance updates, population size 10 in eight dimensions,
initial normalized step 0.2, clipped bounds and serialized proposals. It cannot learn rotated correlations.
The engine evaluates the same initial population before proposing; the emitter initializes its distribution
from its first selected parent. Other methods retain their disclosed selection and variation policies.
Proposal calls and actual evaluator work are charged separately; all initialization is inside the evaluator cap.

Decision: expose the emitter as an opt-in tool for continuous domains, not a new universal default. Keep the
simpler hill-climbing baseline and the unfavorable rippled result in subsequent comparisons. Wider task families,
external implementations, fixed statistical plans and held-out confirmation remain necessary for competitive claims.

## Reproduce

Run the first command from the pinned source revision. The exporter is a later analysis-only utility;
it never changes the experiment, drops failed runs or selects favorable seeds.

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Quality -c Release -- 10 256 d62d5cbd5918122f7aac92bb512b49948e52ea25 pilot.json
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Export-NumericEvidence.ps1 -InputPath pilot.json -OutputPath compact.json
```

Full JSON includes every terminal proposal and best-so-far trace. Compact evidence excludes those long traces
and the bounded receipt tail, retaining their raw file hash and reconciled totals. This is local CPU evidence;
whole-file hashes include runtime/OS metadata and are not expected to match across platforms.
