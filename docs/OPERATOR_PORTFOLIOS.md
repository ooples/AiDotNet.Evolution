# Cost-aware proposal portfolios

Use `EvolutionCostedPortfolio.Create` for an opt-in mixture of independently versioned
`ICostedEvolutionProposalSource<TGenome>` backends. The same contract handles mutation,
crossover, restart, local refinement and consumer-supplied model proposals.

```csharp
var ledger = new EvolutionResourceLedger("run", EvolutionResources.Of("cost_units", 1000));
var policy = new EvolutionOperatorRewardPolicy(
    EvolutionOperatorRewardKind.ParentImprovement,
    EvolutionOperatorCostBasis.ProposalAndEvaluation,
    "my-deterministic-cost-conversion-v1", qualityScale: 1);
var portfolio = EvolutionCostedPortfolio.Create(new[]
{
    new EvolutionCostedPortfolioArm<MyGenome>(mutationBackend, EvolutionResources.Of("cost_units", 2)),
    new EvolutionCostedPortfolioArm<MyGenome>(modelBackend, EvolutionResources.Of("cost_units", 20))
}, ledger, policy, explorationProbability: 0.2);
// Supply this portfolio to EvolutionEngine. Wrap the evaluator with
// ResourceMeteredEvolutionTask using this SAME ledger and declared stage maxima.
```

Each source supplies a stable model/prompt/configuration hash, actual resource receipt,
terminal feedback and checkpoint state. Include every operation performed inside the
proposal—model requests, parsing, repairs and inner refinement—in that receipt.
Do not also charge those operations through another wrapper. Evaluation charges are
separate. Missing receipts conservatively charge the reserved maximum; failed,
infeasible, duplicate, reused and unknown-cost outcomes cannot earn fresh credit.
Admission bounds do not sandbox an untrusted backend.

Every arm gets an initial trial. Epsilon-greedy selection then preserves exploration;
epsilon 1 is the uniform-mixture control. Rewards are bounded valid archive success
or direction-aware parent improvement divided by declared total cost, not elapsed time.
The cost conversion and quality scale must be fixed before search and comparable
across arms. This is not a claim that every adaptive mixture beats a static operator.

`CreditCommitted` identifies the child/configuration, evaluation, generation,
archive result and proposal/evaluation charges. Notification exceptions are isolated
and counted; mutating reentry is rejected. Notifications are attempted once in-process,
not transactionally delivered to external storage. Use an idempotent sink keyed by
run/evaluation identity. Child learning callbacks are part of commit and must not throw;
they are not isolated telemetry callbacks.

Persist the shared ledger at the same completed boundary as the engine checkpoint.
Restore both into compatible fresh instances. Engine-owned random streams, pending
credit and child learning restore through the variation checkpoint. A failed child
restore is not transactional: discard that partially restored instance.

The current [study protocol](../benchmarks/analysis/PORTFOLIOS.md) runs the real engine,
reports raw-bound metrics and compares adaptive strategies with both the best
development static strategy and a uniform mixture. Live model quality and OpenEvolve
head-to-head evidence are separate; these tests require no model credentials.
