# US-11 (#29): AutoML on a consumer model, against grid search at equal budget

`benchmarks/EvolutionAutoMl` tunes a real `RidgeRegression<double>` from the **consumer package**
(`AiDotNet` 0.234.0, aliased) over `alpha` x `intercept`, with the engine and with exhaustive grid
search given **the same 36 model fits**. 8 seeds, no model calls and no LLM.

## Independent splits, and a seal that comes first

- Train (120 rows), validation (60) and test (400) are drawn from **disjoint seeds** - the test seed
  is offset by a prime, so consecutive seeds cannot share a grid.
- **Selection sees validation only**, for both searchers.
- The test split is sealed by SHA-256 **before either searcher starts**, re-verified before scoring,
  and each searcher's single chosen configuration is scored on it once. A run whose test rows no
  longer match the seal is a refusal, not a number. Every seed carries a distinct seal, and a test
  asserts that.

The target has a nonlinear term ridge cannot fit, so the reachable error is bounded away from zero
and the tuning problem has a genuine interior optimum rather than "use the smallest alpha".

## Result: parity, with a tight interval

| measured | value | 95% CI |
| --- | ---: | --- |
| test MSE ratio, engine / grid | **1.0012** | [0.9963, 1.0063] |

Engine better on **4** seeds, grid better on **4**; per-seed range 0.9874 to 1.0132.

**This is parity, not a win, and that is the expected result.** A two-parameter space is small enough
that an exhaustive grid is near-optimal, so the honest claim from this bundle is that the engine
*matches* a strong baseline on a consumer workload at equal cost - which is what the criterion asks
to be evaluated with independent splits and a strong baseline. A space too large to enumerate is
where a searcher earns its keep, and this workload is deliberately not that.

## Reproducing

```text
dotnet run --project benchmarks/EvolutionAutoMl -c Release -- --output rows.json
python benchmarks/analysis/automl_tuning.py rows.json
```

Deterministic: same seeds, same draws, same rows. `benchmarks/analysis/test_automl_tuning.py`
regenerates `summary.json` from the committed `rows.json`, refuses unequal budgets and a ratio that
disagrees with the two errors it came from, and pins the parity reading.
