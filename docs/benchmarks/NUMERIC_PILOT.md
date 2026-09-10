# Numeric development pilot — September 10, 2026

Source: `2d56ac4a2dc3eb26896e09f76ff1b6a7b05799c1`. Five methods, four eight-dimensional tasks, seeds 0–9,
256 evaluator calls per run including eight initial genomes. All 200 runs completed: 51,200 evaluator calls.
The [per-run evidence](numeric-pilot-2d56ac4.json) retains every seed, initial-population hash, final loss,
mean best-so-far loss, coverage and run-state hash, plus runtime metadata and the full local trace's SHA-256.

Reproduce the full traces from that commit:

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Quality -c Release -- 10 256 2d56ac4a2dc3eb26896e09f76ff1b6a7b05799c1 pilot.json
```

## Median final loss (lower is better)

| Task | Random search | Hill climb | Fixed MAP-Elites | Adaptive portfolio | Uniform portfolio |
| --- | ---: | ---: | ---: | ---: | ---: |
| Sphere | 16.2584 | 0.3606 | 25.6772 | 16.1701 | 18.9426 |
| Shifted quadratic | 16.2334 | 0.7504 | 29.8930 | 15.0947 | 20.0586 |
| Anisotropic quadratic | 247.8624 | 36.3571 | 629.8937 | 200.2311 | 246.9495 |
| Rippled quadratic | 69.2167 | 35.8947 | 60.7968 | 63.2529 | 68.0234 |

The adaptive and uniform portfolios use identical operators and parent selection, differing in allocation probability
(epsilon 0.1 versus 1). Adaptive allocation has lower median loss on these four tasks than uniform allocation. This is
a descriptive observation from a development pilot, not a significance test or held-out generalization result.

Hill climbing has the best final-loss median on every task here. The adaptive portfolio instead fills an average of
87.3–90.2 cells versus hill climbing's 10.3–12.6. Coverage and best score are different objectives: high coverage alone
does not make the optimizer better for a user seeking one fastest algorithm. On the rippled task, fixed MAP-Elites also
has a lower median final loss than the adaptive portfolio. These unfavorable comparisons are retained deliberately.

Decision: keep adaptation opt-in. Do not advertise this feature as improving every optimization task or make it the
default based on this pilot. Next experiments should separate archive discovery rewards from scalar improvement,
study task-specific parent selection and add representative held-out program/kernel/AutoML problems.

## Limits

These tasks were chosen for cheap development checks, not as a preregistered representative sample. Only ten seeds
were used; no confidence intervals, power analysis, multiplicity correction or independent confirmation is claimed.
Task descriptors are input coordinates, not an objective-derived success metric. Coverage is not a correctness test.
All objectives are deterministic; this experiment says nothing about noisy timing, sandbox safety or model quality.
No OpenEvolve, paid API or GPU benchmark was run. Run duration is not a reported performance metric.

Full local traces are generated artifacts; they are reproducible with the command above, while the compact per-run
evidence is committed. Identical whole-file hashes require the same runtime/OS metadata and numerical behavior;
cross-platform comparison should compare semantic numerical results with an appropriate declared tolerance.
