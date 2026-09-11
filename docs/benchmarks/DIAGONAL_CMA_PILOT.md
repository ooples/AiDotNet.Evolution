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

## US-13 current-revision verification

The complete 240-run campaign was repeated at `a071dc6842e0b45edc2604aad00aabf68440eee0` after integrating
measurement-origin-aware learning, adding explicit typed presets and fixing collapsed logarithmic intervals.
[New compact evidence](numeric-pilot-a071dc6.json) retains all runs, costs and the raw trace SHA-256
`56eac7161f0724bebd329766fc5278bfaa3d97496a5806ffbe6e01b91ae49d15`.
All 61,440 evaluator calls completed; every paired initial-population hash, final loss, mean loss, evaluator-call
count and proposal count matches the earlier evidence exactly. This confirms unchanged fresh-measurement behavior
on these real-valued fixtures; it is not an independent confirmation set. State hashes are not compared across
changed learning semantic versions. The unfavorable rippled result and the opt-in/default decision remain unchanged.

Reproduce by replacing the source revision in the command above with `a071dc6842e0b45edc2604aad00aabf68440eee0`
and choosing new output filenames. The separate narrow-log and preset tests, not these broad real-valued fixtures,
verify the new domain edge case, ownership, checkpoint compatibility and factory behavior.

## US-13 review-fix re-verification

The complete campaign was repeated again at `7617468c0a9b626922cbf9ee71582ccc29c44882`, after the review fixes:
logarithmic domains mapped by ratio with pinned endpoints, typed preset factories, and split domain-validation
errors. [Compact evidence](numeric-pilot-7617468.json) retains all runs, costs and the raw trace SHA-256
`52041a1bf2398541669f0cd88e21ccc40be873dd16963089772314457e047b9a`.

All 240 scheduled runs completed, 61,440 evaluator calls, no failed run. Every run matches the `a071dc6` evidence
exactly on initial-population hash, status, evaluator calls, proposals, final loss, mean best loss, occupied cells
and state hash, so every median in the table above is unchanged and no recorded number needed regeneration. This is
the expected outcome rather than a confirmation of the fix: these fixtures declare only real-valued domains, and the
ratio/endpoint change affects logarithmic coordinates alone. The narrow-log, preset and validation unit tests, not
this campaign, exercise the changed paths. The unfavorable rippled result and the opt-in/default decision stand.

Reproduce by substituting this revision and new output filenames in the command above.
