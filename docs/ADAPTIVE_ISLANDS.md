# Adaptive islands and bounded exploration

`AdaptiveIslandSearch<TGenome>` is an opt-in fixed-pool variation operator and island scheduler. The existing round-robin engine behavior is unchanged unless the supplied variation implements `IEvolutionIslandProposalScheduler`. The engine requires a checkpointable scheduler, a matching `IslandCount`, and `RoundRobin` destination assignment; `InheritParent` is incompatible.

```csharp
var search = new AdaptiveIslandSearch<MyGenome>(new[]
{
    new EvolutionIslandStrategy<MyGenome>("small", new SmallMutation(), new RandomRestart()),
    new EvolutionIslandStrategy<MyGenome>("large", new LargeMutation(), new RandomRestart())
}, new EvolutionIslandPolicyOptions(
    proposalsPerIslandPerEpoch: 16, minimumPerIslandPerEpoch: 2,
    rewardWindow: 32, gainScale: 0.1, diversityWeight: 0.25,
    stagnationOutcomes: 64, restartProposals: 8));
// Supply search as the engine variation; set options.IslandCount = 2.
```

The task-specific operators above are illustrative. For a complete runnable local-CPU example:

```powershell
dotnet run --project examples/AdaptiveIslandSearch -c Release -- 2 128 new-results.json
```

The output file must not already exist. Four frozen policies (uniform/adaptive allocation, each with/without restart phases) use the same two initial seeds, two authored scalar objectives and evaluator-call cap. Every primary run is replayed; seed zero additionally restores aligned engine and resource-ledger snapshots. Raw measurements, best-quality curves, resource receipts, allocation decisions, statistics and state hashes are retained. This is development evidence, not held-out confirmation, a competitor benchmark or monetary accounting.

## Allocation and learning

An epoch contains `IslandCount * ProposalsPerIslandPerEpoch` actual variation proposals, excluding seeds and migration. The least-served islands receive their mandatory floor first. Afterward the policy draws from positive weights:

`0.01 + (1 - DiversityWeight) * mean(gain) + DiversityWeight * mean(diversity)`

Gain is declared-direction parent improvement divided by `GainScale`, clipped to `[0,1]`. Only completed, feasible, non-reused outcomes with the declared direction earn progress. An invalid/infeasible/opposite-direction parent cannot supply gain. Diversity is one for a fresh feasible new-cell insertion (including insertion with eviction), zero otherwise. Replacement alone is not diversity. Failures, duplicates and reused measurements contribute zero to the bounded recent terminal-outcome window, but still receive exactly-once child feedback.

`adaptiveAllocation: false` selects uniformly after the same floor. The floor is guaranteed only for a complete epoch; a run may end partway through it. These are proposal slots, not equal dollars, evaluator calls or elapsed time. Expensive children must use a shared resource ledger; the scheduler does not infer or authorize their spending. Report both allocated proposals and actual resources.

Selection is pure with respect to policy state. The dedicated proposal-local random stream does not consume the ordinary variation stream. Accounting occurs only when `ProposeAsync` actually runs, because parent selection can return nothing. An empty destination may borrow a parent from another occupied island while retaining its own destination and operator. A completely empty archive cannot bootstrap variation; supply valid seeds.

## Soft exploration restart

`StagnationOutcomes` terminal outcomes without fresh parent gain or new-cell diversity schedule at most `RestartProposals` independent exploration proposals. The configured restart child supplies them; it should generate task-valid independent candidates. Each attempt consumes one slot even when it fails. A subsequent phase waits until all current phase proposals have terminal outcomes.

Archives are never cleared. Existing measured elites, including the global best, remain subject to the same archive admission rules. Restarting does not upgrade search evidence into deployment confirmation. Ordinary child learning also remains intact: this is a soft exploration phase, not a covariance/state reset. Resetting a child while earlier outcomes are pending would break attribution. Failed/reused outcomes continue to route to the child that actually proposed them, even across a phase transition.

`RestartPhasesScheduled` counts scheduled phases, not completed ones; a phase can remain unstarted at shutdown. `RemainingRestartProposals`, `RestartProposals` and `RestartOutcomes` make this distinction auditable. Turn off substitution with `enableRestarts: false`.

## Persistence and limits

Use one to 64 uniquely named islands with independently owned normal/restart child instances. Child identities and configuration must be stable; stateful children must implement `ICheckpointableVariationOperator<TGenome>` and outcome-aware learning must use `IOutcomeAwareVariationOperator<TGenome>`. Sharing the same instance between slots is rejected, even if it happens to be stateless. Methods are serialized by the engine and not intended for concurrent external calls.

The policy fingerprints ordered membership, child identities/versions and every option. It checkpoints child states, recent rewards, allocation epoch, restart counters, pending generation attribution and the most recent 256 decisions. The engine separately checkpoints island archives, generations and migration state; its state hash includes the policy. Renaming/reordering members, changing configuration or child versions rejects restore. Dynamic membership is not implemented.

Restore validates the policy graph and child-state presence before invoking any child. Invalid counters, non-finite rewards, attribution, incompatible membership and oversized payloads fail closed. If a child itself rejects state after another child has restored, discard the wrapper instance before retrying. Child-defined restore cannot be made transactional by this wrapper. Policy state is bounded to 16 Mi UTF-16 characters, pending attribution to 65,536 entries, and the recent reward window to 4,096 outcomes per island.

Engine and resource-ledger snapshots must describe the same quiescent boundary. The resource ledger intentionally lives outside engine rollback because canceled/dispatched work can still cost resources. The example saves both together in process; it is not a durable distributed transaction protocol. Continuous-dispatch replay comparisons must also hold checkpoint scheduling fixed: checkpoint drains can change the context available to later proposals.

## Validation

`AdaptiveIslandSearchTests` covers exploration floors, productive allocation, reward expiry, minimization/scaling, declared reuse, failures, pending attribution, bounded restart phases, archive-best preservation, child feedback failure, identity mutation, corrupt state, ownership and configuration bounds, no-parent selection, worker determinism, and checkpoint replay with heterogeneous stateful operators and migration. See [US-16 delivery evidence](evolution-stories/US-16.md) for revision-specific results and limitations.
