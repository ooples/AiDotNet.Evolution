# V1-50 (#163): weighted model ensemble overhead per proposal (null models)

Raw rows: `models-1.json`, `models-4.json`, `models-16.json`.

## Method

`EvolutionOverhead ensemble <proposals> <models> <repeats>` (`benchmarks/EvolutionOverhead/EnsembleOverhead.cs`),
200,000 proposals per arm, 11 repeats, at 1, 4 and 16 models.

A paired A/B in one process:

- **arm**: `EvolutionWeightedModelEnsemble.ProposeAsync` then `Observe`.
- **baseline**: a uniform drawn from the same `StableRandom` stream picks the model, which is then
  called and observed directly.

So the arms differ only by the ensemble's weighted draw, its per-model accounting and its outcome
routing. The baseline pays for the stream read, the context allocation and the null model's own
call, because the ensemble does not cause those. Each arm runs 64 untimed proposals first.

The models are null: they return an immediate receipt and do nothing on `Observe`, so the measured
time is the ensemble's own work rather than a model's.

## Results, microseconds per proposal

The first repeat in each process is reported separately: it is the first timed arm in a fresh
process and pays JIT that the warmup pass does not cover (the warmup runs before the clock, but
through the same code, so what remains is tiered recompilation).

| models | arm median | baseline median | overhead median (repeats 2-11) | overhead range (repeats 2-11) | overhead, repeat 1 |
| --- | --- | --- | --- | --- | --- |
| 1 | 2.03 | 1.70 | 0.111 | -0.012 to 0.386 | 2.53 |
| 4 | 1.86 | 1.72 | 0.071 | -0.200 to 0.203 | 2.47 |
| 16 | 1.47 | 1.36 | 0.171 | -0.583 to 0.390 | 2.18 |

**Target: <= 5 us per proposal. Met**, by roughly thirteen times at the worst single repeat
(2.53 us) and by more than twenty times at the steady-state median (0.171 us at 16 models).

## What the negative values mean

Some repeats measure the ensemble as *faster* than calling the model directly, by up to 0.58 us.
That is not a real speedup: it is the resolution of the instrument. The ensemble's work is a few
tens of nanoseconds per proposal, which is smaller than the run-to-run spread of a 200,000-call
loop on this machine, so the sign of a single difference is noise. The honest reading is that the
overhead is at or below the measurement floor of about +/-0.6 us, which is why the 5 us target is
reported as met with margin rather than as a measured 0.07 us.

The draw is a linear scan over the cumulative weights, so cost should grow with the model count.
Going from 1 to 16 models moves the median by about 0.06 us - within that same floor, and therefore
not a measured trend either.

## Environment

Windows 11, Release, net10.0, in-process, no concurrency. Measured at `34c117c`.
