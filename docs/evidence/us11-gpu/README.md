# US-11 GPU consumer evidence — 2026-09-19

Status: **working kernel-consumer slice; US-11 remains open**. No measured product
improvement and no OpenEvolve superiority claim.

Source: clean AiDotNet.Tensors commit `94aeab89` (full revision in environment.json).
Hardware: NVIDIA GeForce RTX 3080, 12 GiB, driver 610.88, Windows/WDDM.
The user's dirty PTX checkout was not changed. Evolution orchestrates public
Tensors APIs; no kernel implementation or production dispatch was modified.

Three independent processes each screened both tactics on three shapes, then ran
five fresh-seed confirmation pairs per shape. All nine selections retained the
original fused tactic. All numerical checks passed. **Actual before/after product
change: none.** Measurements below compare the original with a fresh execution
of the selected *same* implementation, so differences are noise, not gains.

| M × K × N | Original median µs | Selected remeasurement µs | Max absolute error | Explicit resident bytes |
|---|---:|---:|---:|---:|
| 1 × 64 × 64 | 11.03125 | 11.16875 | 9.33e-8 | 17,152 |
| 16 × 256 × 256 | 14.69375 | 14.78125 | 4.98e-7 | 295,936 |
| 64 × 256 × 256 | 15.05000 | 15.00625 | 4.85e-7 | 394,240 |

Times are medians of 15 per-replicate medians across three processes, synchronized
host timing, not device-only latency. The alternative's screening medians across
three processes were 38.1875, 39.6375 and 47.6 µs respectively; these are screening
observations, not independently confirmed speedup estimates.

`verification.zip` contains raw JSON samples, environment, logs and build/CPU
acceptance evidence. Total real work: 18 screening evaluations, 90 confirmation
measurements and 50,148 operations including warmups/correctness launches. Source
build: zero warnings/errors. Eleven CPU-only numerical/promotion cases passed.
The separate package-mode build/CPU checks passed using Tensors 0.130.3; GPU
metrics above are **only** from the pinned source checkout, not that package.

Adversarial outcomes:

- NaN/infinity, missing confirmations, <=5% gains and regressions cannot qualify.
- Baseline wins are retained, not reframed as improvements.
- Existing PTX work is isolated from the proof and not overwritten.
- The unbounded cross-target restore failed NU1605; source proof explicitly targets
  net10.0 instead of suppressing dependency validation.
- Missing unpublished Tensors 0.131.0 failed NU1102; package mode pins 0.130.3.

Next evidence gap: instrument actual PTX dispatch and add tunable kernel candidates
in a dedicated Tensors companion PR, with strong vendor baselines and controlled
device timing. This slice cannot claim hardware-near PTX optimization, complete
US-11, or substitute for its program/OpenEvolve and ask/tell acceptance criteria.
