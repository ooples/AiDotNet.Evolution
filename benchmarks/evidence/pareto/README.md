# Pareto campaign evidence

`b097034.json` contains 180 runs from source `b097034a7ba7acf4d9a94ffab6388b1bb3868760`: two authored tasks, three search methods, 30 paired seeds, 256 evaluation attempts per run. Every run reached that budget and retained a feasible front. This is a synthetic feature comparison, not an OpenEvolve campaign or a representative algorithm-optimization result.

All deltas are Pareto minus baseline. Hypervolume is normalized against the same fixed `(1,1)` reference; higher is better. Confidence intervals are descriptive paired bootstrap intervals, not multiplicity adjusted.

| Authored task | Scalar baseline | Mean hypervolume delta | 95% interval |
|---|---|---:|---|
| Convex quadratic | 32-slot grid | +0.013750 | [0.010202, 0.017925] |
| Disconnected quadratic | 32-slot grid | -0.008092 | [-0.027781, 0.008474] |
| Convex quadratic | Single best | +0.189027 | [0.188447, 0.189447] |
| Disconnected quadratic | Single best | +0.175322 | [0.156681, 0.189677] |

The equal-capacity disconnected-task result is **inconclusive and slightly negative on average**. Pareto improves coverage of tradeoffs versus a one-slot archive, but its best scalar quality is slightly worse on average on both tasks: +0.000299 and +0.000703 (lower scalar quality is better). Do not hide these outcomes behind a hypervolume-only summary. Individual-objective minima, complete retained vectors/genomes, seed pairs, counters and state hashes are in the raw report. A later method change must run a new recorded campaign, not relabel this one.

## Failed approaches retained

- `a79b396-failed.json`: five seed preparation failures and zero evaluations because the example's record genome did not implement `IImmutableEvolutionGenome<T>`. Fixed by an explicit owned-snapshot implementation in `9669bd3`; this failed attempt contributes no quality evidence.
- The next attempt at `9669bd3` stopped while constructing a front from scalar results on the constrained task: the legacy MAP-Elites archive does not reject positive constraint violations by itself. No completed campaign report was produced. Filtering only the final front would leave a biased search because invalid winners could displace valid ones. `b097034` instead applies the same feasible-only admission rule to both scalar comparators through an example-local wrapper; legacy production scalar semantics are unchanged.

The campaign was rerun at `b097034` to generate the checked-in report. An earlier local run at the same revision remains under `TestResults/pareto/campaign-feasible.json`; wall-clock values can differ. No paired seed was dropped or selected after seeing its outcome.
