# Numeric quality development harness

This is a small, reproducible starting point for roadmap US-01/02/04/07/12, not evidence of superiority over OpenEvolve.
It exercises four explicitly defined eight-dimensional objectives on [-5, 5] with random search, best-parent hill climbing,
fixed MAP-Elites, adaptive MAP-Elites, the same portfolio with uniform allocation and diagonal CMA. Descriptors are two coordinates,
not the objective value. Each paired seed uses
the same eight initial genomes. Initial evaluations count against every method's budget.

```powershell
dotnet run --project benchmarks/AiDotNet.Evolution.Quality -c Release -- 10 256 <source-commit-sha> results.json
```

The output path must not exist. JSON retains every run, every terminal evaluation, best-so-far loss, evaluator calls,
cost units, proposals, coverage, initial-population identity, state hash, adaptive operator statistics and CMA state.
Proposal calls and evaluator cost units reconcile through the run-scoped resource ledger. Failed or
under-budget runs produce a nonzero exit code and stay in the report. Mean best loss is averaged over actual evaluator
calls, including initial evaluations; lower is better. Coverage is meaningful for MAP-Elites, not a hill-climber target.

All methods share the engine's canonicalization/cache infrastructure. Random search ignores the archive when proposing;
hill climbing always chooses its best member; fixed and adaptive MAP-Elites sample occupied cells uniformly. Adaptive
MAP-Elites adds coarse mutation and random restart, so its comparison with fixed MAP-Elites changes both the portfolio
and its allocation. It is **not** an isolated bandit ablation. `UniformPortfolioMapElites` uses exactly the adaptive
method's operators, initial trials and parent selection, but epsilon 1 rather than 0.1; compare these two to test
the allocation policy. Mutation radii are 0.1 and 1.

`DiagonalCma` uses the typed-space positive-weight diagonal emitter with normalized initial step 0.2 and population
size 10. Bounds are clipped; only fresh feasible measured candidates train its distribution. It uses the same initial
population and evaluator cap and reports pending/stale populations. This is not full-covariance CMA-ME.

Given identical arguments and the same runtime/platform, when the harness is rerun, then its semantic JSON records must
match (output filename excluded). Given a task and seed, when methods are compared, then initial-population hashes and
actual evaluator-call budgets must match. Given a failed run, when a report is generated, then the run must remain visible.

Missing before a competitive claim: representative program/kernel/AutoML tasks, held-out partitions, matched external
adapters and model access, true end-to-end resource accounting, randomized experiment ordering for runtime measurements,
paired hierarchical uncertainty analysis, and predeclared promotion thresholds. Do not turn development fixtures into
held-out validation by merely changing a label. These fixtures measure search quality, not throughput or GPU speedup.
