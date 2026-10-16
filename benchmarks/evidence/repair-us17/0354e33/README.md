# US-17 (#35): what compiler-guided repair recovers, and what it costs

`benchmarks/EvolutionRepair` runs the same scripted proposals twice - once with `MaxRepairs = 0`,
once with `MaxRepairs = 2` - so the only difference between the arms is whether a proposal that fails
to compile gets the compiler's own diagnostics back and another attempt. 8 seeds x 16 proposals per
arm, no model and no spend.

## What this instrument is, and is not

The story's metrics table says *measured by paired pilot*. A paired pilot needs a paid campaign, so
this measures the **mechanism** instead, and the difference matters in two specific ways:

- **The broken-first-reply rate is an input, not a measurement.** `BreakRate = 0.5`, decided per
  (seed, proposal) by a SHA-256 draw so both arms see an identical sequence of replies. How often a
  real model proposes a broken patch is unknown until the gate campaign (#122) runs, so the absolute
  lift below is bounded by that 0.5 and is **not** evidence for the story's `+>= 15 points` target.
- **Tokens are not measured.** The scripted client reports a fixed 100 input / 50 output tokens per
  call, so any token figure would restate the call count. `summary.json` carries
  `"TokensMeasured": false`, and a test asserts it.

What the arms *do* establish is that the repair loop recovers broken proposals, and what that
recovery costs in model calls - which is the part of the criterion a scripted client can speak to,
because the compiler, not the script, decides whether an edit compiles. The broken edit replaces a
numeric literal with `MISSING`, which is not an expression.

## Results

| measured | value | 95% CI | reading |
| --- | ---: | --- | --- |
| valid-proposal rate | **+48.44 points** | [+39.84, +57.03] | every broken proposal was recovered: 0.5156 -> 1.0000 |
| calls per valid candidate | **-0.5928** | [-0.9126, -0.3090] | repair is *cheaper* per valid candidate |
| model calls | **+7.75** | [+6.38, +9.13] | and it does spend more calls to get there |

The second row is the non-obvious one. Repair adds roughly eight calls per seed and still lowers the
cost of a valid candidate, because without it the calls spent on a broken proposal buy nothing: at
`MaxRepairs = 0` the arm spends 16 calls for 5 to 11 valid candidates, and at `MaxRepairs = 2` it
spends 21 to 27 calls for 16.

Intervals are bootstrap intervals over seeds (2,000 resamples, seed 17, alpha 0.05) computed by
`analyze.interval`, the same estimator the pilot uses.

## Reproducing

```text
dotnet run --project benchmarks/EvolutionRepair -c Release -- --output rows.json
python benchmarks/analysis/repair_ablation.py rows.json
```

The run is deterministic: same seeds, same draws, same rows. `benchmarks/analysis/test_repair_ablation.py`
regenerates `summary.json` from the committed `rows.json` and fails if either drifts.
