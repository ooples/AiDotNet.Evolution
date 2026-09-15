# Feature ablations (US-07)

Run after the final Release build, with Python's standard library:

```powershell
python benchmarks/analysis/ablation.py register benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll plan.json --seeds 32 --budget 64
python benchmarks/analysis/ablation.py execute plan.json ablation-results development
python benchmarks/analysis/ablation.py execute plan.json ablation-results confirmation
python benchmarks/analysis/ablation.py report plan.json ablation-results scorecard.json
```

Registration pins executable/core hashes, budgets, disjoint seeds, matrix, endpoint and decision criteria before work. A durable claim beside the plan prevents reusing that registration even with a different output directory. Interrupted runs remain consumed; retain them and explain any new registration. Local claims are audit controls, not protection against a malicious filesystem owner. Do not copy/modify plans to bypass one-use claims. Direct `--ablation` invocation is a low-level contract runner, not a registered confirmation experiment.

## Design

The 19 configurations include Uniform, Ratio, Curiosity, Double; single-feature additions; all-on and six removals; calibration × growth and islands × continuous differences-in-differences. Migration is conditional on islands: `islands-migration` versus `islands` measures it. Removing islands from all-on necessarily also removes migration; it is a joint removal, not an independent migration interaction estimate. Higher-order and selection × feature interactions are not estimated by this matrix.

Every configuration gets the same eight initial genomes, evaluator-call ceiling, 8× proposal ceiling, four workers and total capacity of 64 elites. Four islands receive 16 each. Calibration reads the same seed coordinates available to every method; it makes no uncharged objective calls. Growth changes bin ranges, never this elite-cap ceiling. Diversity projects the final union into an independent fixed 8×8 grid; it cannot multiply scores by duplicating elites across islands. The operator uses inspirations for every selector, making Double's inspiration behavior observable. Continuous dispatch changes scheduling only; no sleeps simulate evaluator work. Configuration order is shuffled independently of results. Numeric/program search replays deterministically; measured kernel timings do not.

Costs include actual evaluator and proposal calls, elapsed run seconds, and raw primitive-case counts. Kernel evaluations each include input setup, a reference calculation, one warmup, three timed invocations, and four correctness checks. Timings include only the candidate kernel; elapsed run cost includes setup/checks. This is an equal **call-budget**, worker-count and elite-count experiment, not an equal CPU-second or measured RAM experiment. Proposal exhaustion remains an outcome with actual unused evaluation budget shown; failed runs receive zero endpoint credit and remain in denominators.

## Tasks and limits

| Family | Development | Untouched confirmation | Meaning |
| --- | --- | --- | --- |
| Numeric | Rippled quadratic | Coupled absolute objective | Inexpensive four-variable CPU search |
| Program | Quadratic + cosine target | Absolute + sine target | Trusted two-node weighted expression grammar, 2,048 interpreted inputs per evaluation |
| Kernel | Blocked matrix product | Blocked squared-distance matrix | Real 24×24 CPU kernels, three tile parameters and loop order, exact integer-valued correctness oracle |

Program search is materially more evaluator work than the numeric fixture, but **not** unrestricted C#/Python synthesis or model-driven costly program search. Kernel results measure this local machine under four-worker contention; search-time best timing is selection-biased and is **not** a fresh-incumbent speedup proof. Generalizing presets to LLM programs, production kernels, hardware accelerators or broad task families requires those consumer workloads, not relabeling these fixtures.

## Confirmation and presets

Development reports quality, fixed-reference diversity, target success (quality ≥ 0.8), failures, evaluator calls and wall time for every configuration/family. Interactions are descriptive, not unadjusted significance claims. A deterministic development ranking nominates one configuration per family using the fixed endpoint `0.75*quality + 0.25*diversity`. Confirmation runs nominees and baseline on **different tasks and seeds**, with unchanged budget/criteria. One-sided Hoeffding bounds on paired differences in [-1,1] split alpha 0.05 across three family hypotheses. Only a lower gain greater than 0.02 confirms a nominee. No optional stopping or post-result weight changes.

The generated `Presets` objects retain baseline whenever evidence is inconclusive. They are scoped experiment configurations, not automatic library defaults. At 32 seeds the simultaneous radius is approximately 0.506: modest gains will remain inconclusive. This is an explicit low-cost evidence limit, not proof of equivalence or an excuse to substitute a narrower retrospective confidence interval. A broader, powered follow-up must use a new preregistered task set; the current confirmation tasks have then been consumed.

Adversarial defenses include canonicalizing quantized program/kernel parameters, refusing unknown matrix variants, exact row coverage, paired seed identities, executable/request hashes, raw timing reconstruction, budget reconciliation, failure penalties, duplicate-key rejection, frozen nomination receipts and one-use registration.
