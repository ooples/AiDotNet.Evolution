# US-08 portfolio comparison

```powershell
python benchmarks/analysis/portfolio_study.py register benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll plan.json --count 32 --budget 128
python benchmarks/analysis/portfolio_study.py execute plan.json results development
python benchmarks/analysis/portfolio_study.py execute plan.json results confirmation
python benchmarks/analysis/portfolio_study.py report plan.json results scorecard.json
```

Register after the final build. The binary/core hashes, budgets, all methods, disjoint seeds and decision criteria are fixed before evaluation. One-use claims beside the plan prevent retrying the same registration in another directory. Preserve interrupted/failed runs. The low-level C# runner is not itself a registration or defense against a filesystem owner modifying evidence.

Four static operators—mutation, crossover, restart and two-candidate local refinement—are compared with the same portfolio using parent-improvement or archive-success reward. Every arm receives an initial trial; epsilon 0.2 preserves uniform exploration thereafter. All methods use the same seeded genomes, archive, best-parent rule and total resource ceiling. **One cost unit is one proposal dispatch or objective invocation**, not money, equal CPU time or FLOPs. Refinement consumes two actual inner objective invocations plus its proposal dispatch (three units) before the final evaluation; it cannot purchase hidden free optimization. The first denied proposal/evaluation ends the run, so unused budget is retained and reported.

The controlled driver uses the actual archive, resource-metered proposal/task adapters and explicit terminal outcome callbacks. Engine orchestration/checkpoint replay and the real consumer facade/compilation path are separately covered by integration tests; this driver is not a model-driven competitor benchmark.

The study uses tasks different from US-07: Rosenbrock chain → maximum coupled square; quartic/cosine expression fitting → frequency/absolute expression fitting; blocked Gram product → blocked L1 distance. Kernels have exact correctness checks, one warmup and three timings. Program search is a bounded interpreted grammar, not unrestricted generated code. Kernel search-time best timings are selection-biased and do not prove incumbent speedups.

Select the highest mean-quality **static** method per family on development only. Freeze that comparator before confirmation. Compare both adaptive policies to it on different tasks and seeds. Failed runs retain zero quality in denominators. Six one-sided Hoeffding bounds on paired differences in [-1,1] split alpha 0.05 across two policies × three families; promotion requires a lower gain greater than 0.02. Weights, methods and criteria cannot be changed after observing results. Small campaigns can be inconclusive: do not tighten the bound retrospectively, substitute baseline for a stronger static winner, or promote from development results. New task-specific evidence is required before changing any default; the portfolio remains optional.

Typed `CreditCommitted` notifications identify each responsible child/configuration, terminal evaluation/generation, archive result, evaluator attempts and proposal/evaluator charges. Notification failures are counted and isolated; mutation/checkpoint reentry is rejected. Notifications are attempted once in-process and not replayed after restore: this is not an exactly-once external database transaction. Persist using run + evaluation identity with an idempotent sink when durable delivery is needed. Credit history/pending attribution and child state are checkpointed; randomness is supplied by engine-owned deterministic streams, not a hidden portfolio RNG. Persist the shared ledger with the corresponding engine boundary.
