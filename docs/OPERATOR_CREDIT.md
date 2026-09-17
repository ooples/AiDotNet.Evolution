# Measured gain and proposal-cost credit

US-08 now has opt-in reward policies and a proposal-resource adapter, not only an archive-success counter.
The original `AdaptiveVariationPortfolio(operators, explorationProbability)` constructor, binary signature,
version formula and checkpoint representation remain available unchanged. New behavior requires an explicit policy.

```csharp
var policy = new EvolutionOperatorRewardPolicy(
    EvolutionOperatorRewardKind.ParentImprovement,
    EvolutionOperatorCostBasis.ProposalAndEvaluation,
    costUnitVersionHash: "my-deterministic-work-unit-v1",
    qualityScale: 0.25,
    minimumCostUnits: 1);

// Each backend implements ICostedEvolutionProposalSource<MyGenome> and returns an actual receipt.
var metered = new ResourceMeteredVariationOperator<MyGenome>(
    backend, ledger, EvolutionResources.Of("cost_units", 10), policy.CostUnitVersionHash);
var portfolio = new AdaptiveVariationPortfolio<MyGenome>(new[] { metered }, policy, explorationProbability: 0.2);
// Pass portfolio to the engine. Meter evaluation separately with ResourceMeteredEvolutionTask.
```

The backend, genome and shared ledger are application-supplied. No model call or paid provider is built in.

## Given / When / Then

- Given different useful scalar improvements, when a fresh feasible proposal changes the archive, then parent-relative
  credit uses the parent score captured **at proposal time**, not a newer parent or a mutable live archive at commit time.
- Given maximize or minimize objectives, when parent credit is computed, then the direction-aware improvement is divided
  by a predeclared positive quality scale and clipped to `[0,1]`. No improvement, direction mismatch, failed/infeasible work,
  cache hits, missing evaluation attempts or no archive change earn zero. Archive-success mode remains a separate option.
- Given proposal-inclusive credit, when an outcome commits, then the selected child's complete proposal receipt is read
  before feedback consumes its attribution, and added to terminal evaluator-attempt charges without charging the ledger again.
- Given a selected operator/configuration, when feedback commits, then `LastCredit` identifies the child, its version, policy,
  generation, archive outcome, included charges and applied reward. It is a detached diagnostic record, not a learning callback.
- Given an exception or cancellation without a proposal receipt, when admitted work ends, then the maximum is charged as
  unknown. A pre-dispatch denial costs zero and does not send feedback to an undispatched backend. Known failures and maximum
  overruns keep their actual charges. A missing explicit `cost_units` field is not interpreted as free work.
- Given matching complete-boundary engine and ledger checkpoints, when a run resumes, then backend state, parent baselines,
  pending costs and learned arm totals reproduce the uninterrupted trajectory. Malformed pending credit is rejected.
- Given equal valid gain but unequal included costs, when repeated outcomes guide selection, then lower-cost strategies can
  receive more proposals while nonzero epsilon-greedy exploration remains available. This is a policy contract, not a quality claim.

## Exact reward and accounting boundaries

For explicit policies, `reward = gain / max(1, chargedCost / minimumCostUnits)`.
`ArchiveSuccess` supplies gain one; `ParentImprovement` supplies
`clamp(directionAwareDifference / qualityScale, 0, 1)`. Parent improvement also requires an actual archive insertion,
replacement or insertion-with-eviction. This does not measure global marginal quality, hypervolume, regret or statistically
confirmed improvement. Noise and adaptive selection can still distort scalar feedback; use appropriate replication/confirmation.

`Evaluation` includes only the terminal evaluator's accumulated attempt charges. `ProposalAndEvaluation` additionally includes
work inside the cost-receipting proposal backend, such as model calls, parsing, compilation and repair **if that backend actually
accounts for them**. It is not automatically total run cost: initialization, external refinement, surrogate work, hardware setup
and work performed elsewhere must be charged separately. A shared ledger pays every actual operation once. Never put a model
call into both the proposal receipt and evaluator receipt. Declared `cost_units` conversion semantics must match across all
operators, the evaluator and policy; the core cannot validate a currency conversion or reconstruct an omitted bill.

Known conservative-evaluation-cost diagnostics suppress explicit-policy credit, as do unsuccessful/unknown proposal receipts.
The generic evaluator adapter preserves such costs through retries; caller-written adapters must preserve equivalent evidence.
Diagnostic retention is bounded, so these checks are not a universal proof of exact external spending. Full receipt auditing remains necessary.

The metered proposal adapter permits at most 65,536 pending generations and 16 MiB serialized state. Backend identity/configuration,
resource maxima and unit semantics determine compatibility. It rejects concurrent calls and checkpoints while a proposal is running;
this does not implement the separate concurrent-proposal roadmap story. A backend must enforce its own time, process, model and
device limits, return actual costs on failures, and checkpoint every piece of state that can affect proposals or learning.
Persist the matching ledger separately; an old checkpoint is not permission to replay already-spent work.

`LastCredit` holds only the latest committed attribution and resets to null on restore. It is excluded from checkpoint learning
state and cannot be used as hidden policy memory. Persist emitted records externally for a full audit trail. Arm totals and pending
credit are checkpointed. If a backend rejects restoration after mutating itself, discard that instance before retrying.

## Runnable comparison

```powershell
dotnet run --project examples/OperatorCreditSearch -c Release -- 2 64
powershell -ExecutionPolicy Bypass -File eng/Test-OperatorCredit.ps1
```

The example compares fixed small/large mutation with archive/parent rewards and evaluator-only/proposal-plus-evaluator denominators:
six methods, two four-dimensional objectives, identical eight-genome starting populations, and the same whole-run cost cap.
Small proposals cost 0.1 synthetic units, large proposals cost one, each true evaluation costs one, and population setup costs 0.08.
These prices are predeclared synthetic values, **not measured runtime or actual model prices**. All policies pay both work stages even
when their credit denominator ignores one. Every method stops at its first reservation denial, so unspent remainder can differ.

The smoke verifies 24 paired runs, exact replay, measured-only winners, complete attribution and all-stage receipt reconciliation.
The engine integration tests separately verify checkpoint resume. Representative held-out comparisons against the best static preset,
realistic cost ratios, consumer integrations and any decision to promote adaptation as a default remain open.
