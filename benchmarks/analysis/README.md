# Paired, failure-aware development analysis

This dependency-free Python tool adds experiment reporting without adding statistics dependencies to the core
package. It accepts the full numeric protocol-v3 trace or protocol-v4 external comparison and an explicit, fixed analysis specification.

```powershell
python -m unittest discover -s benchmarks/analysis -v
python benchmarks/analysis/analyze.py --input TestResults/quality/pilot-d62d5cb.json --plan benchmarks/analysis/pilot-d62d5cb.plan.json --output-dir TestResults/quality/pilot-d62d5cb-analysis
```

The output directory must not exist. Outputs are Markdown, offline HTML/SVG, machine-readable results and a copy of the specification,
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

For the [external baseline](../external/README.md), valid convergence can stop below the cap: optimizer/controller/C#
counts and binary/initialization identities must agree. Its final measured incumbent carries forward for progress
at later budgets without adding measurements or cost. Other early stops fail. Full-cap core runs retain their
existing validation. Reports preserve external counter disagreement and unknown work instead of rewarding a failed run.

## Prospective fixed-sample planning (US-04)

`design.py` computes failure-inclusive paired pilot variances, a clearly advisory
normal-approximation sample size, and a conservative fixed-task Hoeffding design.
A degenerate pilot cannot make the selected sample size collapse to two runs.
If the required sample exceeds the declared cap or analysis work bound, the design
is infeasible: it is not truncated to the cap or relabeled adequately powered.

```powershell
python benchmarks/analysis/design.py --pilot pilot.json --analysis-plan pilot-plan.json --effect 0.2 --maximum-runs 1000 --output design.json
python benchmarks/analysis/run_fixed.py prepare --pilot pilot.json --analysis-plan pilot-plan.json --effect 0.8 --evaluator benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll --directory new-registration
# Save the returned hash outside the writable registration directory.
python benchmarks/analysis/run_fixed.py run --directory new-registration --expected-hash SAVED_HASH --evaluator benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll
```

The `0.8` effect above is a large utility difference chosen to keep a demonstration
small, not a recommended practical threshold. The planning CLI accepts explicit
alpha/power; the convenience `prepare` command uses 0.05/0.8. Frozen plans pin
tasks, scales, targets (if declared), methods/comparators, budget, run count,
multiplicity, no-extension stopping rule, bootstrap settings, code/runtime hashes,
and fresh random search seeds disjoint from the pilot. A one-use claim is created
before dispatch. A crash cannot be retried with the same registration; unknown
work and every scheduled failed run remain in `failed-schedule.json`.

The supplied actual C# controller executes numeric protocol-v3 tasks with the exact
frozen seed list. Protocol-v4 reports can inform planning through the Python API,
but this runner does not execute SciPy or the newer US-01/US-02 suite/program
schemas. Unsupported schemas fail explicitly; never silently relabel them as v3.
The API `execute(..., runner)` permits another trusted controller to honor the same
schedule/receipt contract. This is not a sandbox or independent preregistration
authority: privileged filesystem owners can replace files or create new registrations.
Protect the registration hash externally, artifact custody, and the no-peeking policy.

### Statistical contract

For each task/comparator the paired difference is bounded in [-1,1]. For T fixed
tasks, M comparisons, n independent paired runs per task, family alpha, desired
power 1-beta, and anticipated task-balanced effect delta versus zero, choose

`n = ceil(2 * (sqrt(log(T*M/alpha)) + sqrt(log(T/beta)))^2 / delta^2)`.

The one-sided aggregate lower bound subtracts
`sqrt(2*log(T*M/alpha)/n)` from the task-balanced observed effect. This is our
conservative application of [Hoeffding's bounded-variable inequality](https://www.stat.cmu.edu/~cshalizi/sml/21/lectures/06/lecture-06.html):
union bounds over tasks avoid assuming cross-task independence. Search runs within
each fixed task must be independent. No task-population generalization is implied.
The pilot normal estimate uses the maximum task paired variance and the
[NIST known-variance normal approximation](https://www.itl.nist.gov/div898/handbook/prc/section2/prc222.htm);
substituting estimated pilot variance makes it advisory, not a power guarantee.
The selected bound concerns detecting a true anticipated effect versus zero; it
does not prove an effect exceeds a minimum practical gain. Bootstrap intervals
remain separately labeled descriptive approximations, not this decision rule.

### Scorecard and trajectory semantics

The scorecard retains known resource totals with unknown-work run counts, paired
win/tie/loss, and a left-continuous utility-versus-cost AUC conditional on complete
successful trajectories. No quality is invented before the first measurement;
missing curves are not interpolated. AUC-known denominators are explicit. HTML
escapes all text, has no scripts or external assets, and breaks curves at missing
points. Cost-axis curves are not elapsed-time curves.

Analysis-plan schema 2 adds required `TargetLoss` to each task (all other plan
fields unchanged). Target hits use complete successful trajectories, retain all
scheduled runs in the denominator, censor non-hits at the declared budget and
separately flag unknown trajectories. These are cost-to-target observations, not
measured time-to-target or a survival estimate treating unknowns as observed data.
Schema 1 remains compatible and leaves target metrics unknown. Declaring a target
retrospectively cannot turn the old pilot into prospective target evidence.
Independent correctness, numerical error, speedup, memory, and common-grid QD
metrics remain null where the input protocol never measured them. Engine completion
is not correctness; native archive occupancy is not comparable normalized QD score.

Final gate (after the single Release build):

```powershell
python -m unittest discover -s benchmarks/analysis -v
python benchmarks/analysis/verify_reports.py --evaluator benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll --source-revision YOUR_COMMIT --output new-verification
```

## What this does not establish

The standalone `analyze.py` tool supports **retrospective development**. Creating a plan after seeing a pilot is not
preregistration. `ConfirmatoryEligible` is always false. For a release campaign, freeze the tasks, partitions,
methods, primary endpoint, normalization, seeds/sample size, compute budget, multiplicity policy and stopping rule
*before* running the final comparison. Use pilot variance for planning, not to keep adding seeds until a result wins.
A seed count of 30 is not a power guarantee; task-population uncertainty also cannot be removed by adding timing
repetitions or seeds to the same handful of tasks. The new fixed-development runner
does not supply representative sealed task custody, program correctness/isolation,
sequential testing, or held-out promotion gates. Those dependencies remain separate.
# Real program pilot reports

`program_report.py --pilot <directory> --audit <directory> --output <new-directory>`
reads the US-02 pilot and post-selection audit directly and emits JSON/Markdown/offline
HTML. It preserves scheduled failures, validates timing/identity bindings, and reports
actual known costs with unknown counts. This single-search-seed schema cannot supply
paired-search variance, confidence intervals or a powered sample-size estimate; those
remain explicitly null. It is not silently converted into a numeric-v3/v4 campaign.
See [scope and adversarial acceptance](../../docs/evidence/us04/PROGRAM_REPORTING.md).
