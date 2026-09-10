# Paired, failure-aware development analysis

This dependency-free Python tool adds experiment reporting without adding statistics dependencies to the core
package. It accepts the full numeric protocol-v3 trace and an explicit, fixed analysis specification.

```powershell
python -m unittest discover -s benchmarks/analysis -v
python benchmarks/analysis/analyze.py --input TestResults/quality/pilot-d62d5cb.json --plan benchmarks/analysis/pilot-d62d5cb.plan.json --output-dir TestResults/quality/pilot-d62d5cb-analysis
```

The output directory must not exist. Outputs are Markdown, machine-readable results and a copy of the specification,
with SHA-256 hashes of the input and plan. Task/method/seed duplicates, unplanned cases, mismatched revisions,
budgets or paired initial populations are errors. Missing scheduled runs are explicit failures with unknown work,
not omitted rows or invented zero-cost successes. Reported independent evaluator/resource counters survive a
dropped or malformed trace; incomplete trajectories are flagged and are not interpolated.

## Endpoint and uncertainty

The comparison endpoint is the task-balanced mean paired difference in `scale / (scale + final loss)`. A completed,
accounted nonnegative-loss run has utility in `(0,1]`; failed, incomplete and missing runs have utility zero,
even if they retained an attractive incumbent. Higher utility is better. Each task's positive scale is specified
explicitly; changing scales changes the comparison's meaning and must never be hidden. The included retrospective
specification uses scale 1 for every task, not a tuned or official AlgoTune normalization.

Within each task, bootstrap draws resample *paired run differences*. Candidate and baseline are never independently
resampled, and timing repetitions/trajectory points never become search seeds. `ResampleTasks=false` keeps the
declared suite fixed and estimates run-seed variability conditional on those tasks. `true` additionally resamples
whole tasks before resampling paired runs within each selected task; this models a task population but cannot make
a deliberately selected development suite representative. Tasks receive equal weight in either mode.

Aggregate percentile intervals use nominal Bonferroni-adjusted tails for the declared comparisons. Per-task
intervals are explicitly exploratory and unadjusted. These are finite-sample bootstrap approximations, not exact
coverage guarantees; small, all-failed or degenerate samples may produce misleadingly narrow intervals. The code
does not compute p-values or promote a winner. The [rliable work](https://arxiv.org/abs/2108.13264) motivates reporting
uncertainty and robust aggregate comparisons; this implementation is not a port or verification of rliable.

Every scheduled run and independent work count is in JSON. Markdown includes every task/method and every failed or
missing run. Completed-only loss medians and known-only progress curves are labeled conditional summaries and must
not substitute for the failure-inclusive endpoint. Reports retain aggregate budget-indexed progress and each run's
trajectory completeness/point count; unaggregated points stay in the input identified by SHA-256. Input/plan and bootstrap work are bounded; the recorded Python
version and bootstrap seed support same-environment replay.

## What this does not establish

This tool deliberately supports **retrospective development** only. Creating a plan after seeing a pilot is not
preregistration. `ConfirmatoryEligible` is always false. For a release campaign, freeze the tasks, partitions,
methods, primary endpoint, normalization, seeds/sample size, compute budget, multiplicity policy and stopping rule
*before* running the final comparison. Use pilot variance for planning, not to keep adding seeds until a result wins.
A seed count of 30 is not a power guarantee; task-population uncertainty also cannot be removed by adding timing
repetitions or seeds to the same handful of tasks. A prospectively locked runner, representative sealed suites,
validated power/sequential design and held-out promotion gates remain open roadmap work.
