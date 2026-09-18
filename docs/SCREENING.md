# US-06 product screening integration

The warm program evaluator now exposes a real cheap-to-full evaluation path for
every controller through the common broker. Enable it using `run_warm_study.py
prepare --screening` alongside the existing registration parameters. Default is
off. A new v5 registration is required; policy cannot change between partitions.
The audit seed is generated before search and never included in candidate prompts
or exposed inside the isolated candidate filesystem.

## Given / When / Then

- **Given** a candidate, **when** screening is enabled, **then** run one input at
  full workload scale but only one input and one candidate execution, with host
  correctness checks and normal Docker isolation. The cheap original baseline
  uses three executions and their median to resist one noisy initial observation.
- **Given** an incorrect cheap result or a cheap timing exceeding both 2ms and
  four times the original's cheap timing, **when** screening decides, **then**
  return an invalid search score without full evaluation. Otherwise advance.
  This conservative timing threshold is a heuristic, not a confidence interval.
- **Given** an advancing candidate, **when** full evaluation fails correctness,
  **then** reject it despite the cheap pass. Selection sees only full-scale scores.
- **Given** a frozen rejection population, **when** search ends, **then** sample
  at most two owner/candidate pairs across the task/seed block using the registered
  seed and canonical identities, and run three fresh interleaved original/candidate pairs at full
  scale. Audit results cannot improve that search's selected program.
- **Given** unknown work, interrupted execution or insufficient capacity, **when**
  dispatch/reconciliation fails, **then** keep charges/evidence and fail closed.
  Search cannot spend the reserved rejection-audit/final-confirmation capacity.
- **Given** a baseline failing cheap or full evaluation, **when** initialization
  fails, **then** stop the screening instance permanently; retrying cannot select
  a lucky baseline. Each timing must be finite and positive before aggregation.
- **Given** missing audits, altered populations, forged cheap/full receipts or
  changed classifications, **when** v5 reporting runs, **then** reject the report.

## Audit interpretation

Full-scale audit means new measurements on the public full search input batch,
not exposure of final confirmation inputs to search. Both classification tails
use the conditional paired-log Student protocol; a Hoeffding sampling bound covers
the sampled rejected population. "Useful" means improving the original, not
necessarily beating the final search incumbent. Half of the family error allowance is reserved
for classifications and half for sampling across tracks and three partitions.
Unresolved classifications count as potentially useful rejections. Small audit
samples usually give wide bounds: zero observed mistakes is not proof of safety.
Tracks with no sampled reject receive bounds [0,1], never a fabricated zero rate.
Sharing the sample across a frozen block amortizes audit overhead; every selected
audit remains attributed to its actual owner and cannot influence selection.
Repeated identical observations do not establish unlimited timing precision.

## Adversarial findings addressed

Cheap correctness cannot substitute for full correctness. Cheap scores cannot
enter archives as full scores. Rejection audits must be charged, independent of
search and protected by reserved capacity. Sampled populations must be frozen
before audit outcomes, without dropping unresolved results or reusing samples.

The existing full evaluator already stops on its first incorrect repetition, so
filtering incorrect programs alone does not necessarily save container attempts.
Workload-size crossover can hide useful full-size algorithms. Accordingly, the
public ablation includes correct-but-slow rejects, an all-pass overhead control
and a size-crossover negative control. No threshold is tuned from these outcomes.

## Local measured ablation

`run_screening_pilot.py --output <new-directory> --image <pinned-image>` runs fixed
public sorting programs with identical candidate order, input sets and worst-case
budgets in screening-on/off arms. It records all physical attempts, host elapsed
time, measured cgroup CPU, rejection audits and retained full-run best quality.
Loops intentionally create known relative work differences; this is an evaluator
ablation, not naturally generated model candidates or broad efficacy evidence.
An independent full arm provides descriptive useful-rejection counts (>20% median
improvement); those are not significance claims. No provider calls are made.

The first policy (one baseline observation, one-eighth scale) was rejected during
local calibration: its reject-heavy arm used 68 containers versus 51 without
screening and rejected none of the 12 deliberately slow correct programs. The
original cheap observation was 103ms, while slow-program cheap observations were
16–21ms despite 359–405ms full times. Tiny-input ranking and one noisy baseline
masked real full-size slowness. The raw failed calibration is retained, not erased.

The revised `cheap-screen-v2` policy uses a three-observation median baseline and
one full-size input instead. Candidate screening is still cheaper than three
full evaluations of two inputs. Audits are shared across each frozen task/seed
block instead of reserving two complete audits for every short controller run.
## Measured v2 results

One registered local run, identical candidate sets and caps, zero provider calls:

| Public sorting panel | Containers off → on | CPU seconds off → on | Host seconds off → on | Useful candidates missed |
| --- | ---: | ---: | ---: | ---: |
| 12 slow + 4 fast candidates | 51 → 46 | 22.644 → 15.123 | 103.708 → 111.738 | 0 of 4 |
| All 4 candidates advance | 15 → 22 | 3.063 → 4.304 | 28.140 → 38.000 | 0 of 4 |
| Size-crossover candidate | 6 → 10 | 1.255 → 2.378 | 11.767 → 25.907 | 0 of 1 |

The reject-heavy panel saves **9.8% of attempts and 33.2% of measured CPU**, but
host elapsed time increases **7.7%**. Both no-rejection controls show overhead.
Every panel retains its full-run best candidate (fraction 1.0). "Useful" in this
table uses the separate full arm's descriptive >20% median improvement rule.
Two sampled slow rejects were classified not useful; the rejected-population
interval remains [0,1] because the audit sample is small. This is not proof of a
universally safe preset or a statistically established wall-time improvement.

Registration SHA-256:
`3757b800c918a366b9040f90bda3bd90be5a0e071b2637e8ea5283e9ac5035c6`.
All raw timings and the failed v1 calibration are retained with the verification
evidence. Screening remains opt-in; workload qualification must consider total
cost and useful-rejection uncertainty, not just fewer full evaluations.
