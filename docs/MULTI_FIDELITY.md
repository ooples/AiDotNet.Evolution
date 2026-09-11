# Resource-aware multi-fidelity promotion

`EvolutionFidelityScheduler<TGenome>` evaluates a bounded candidate cohort at increasing resource levels, promotes
selected candidates, resumes compatible per-replicate evaluator state, and independently confirms final survivors.
It is **one synchronous successive-halving-style bracket**, not the complete multi-bracket Hyperband algorithm or
its theoretical/empirical speedup guarantees. The [Hyperband paper](https://arxiv.org/abs/1603.06560) motivates adaptive
resource allocation; this implementation adds explicit repository-specific identity, accounting and confirmation contracts.

## User story and behavior

As a user with expensive evaluators, I want promising candidates to receive larger budgets while preserving some
late improvers, so that full evaluation is concentrated without mistaking screening scores for deployment evidence.

- Given 2–64 unique immutable candidates and 2–8 increasing fidelity levels, when the bracket runs, then it evaluates
  each active candidate with 2–16 actual replicates. Levels describe cumulative epochs, dataset size or another
  consumer-defined quantity; cost maxima are separately declared in shared `cost_units`.
- Given completed feasible batches, when a promotion occurs, then survivors are ranked by measured mean and canonical
  identity breaks ties. The survivor count is `min(valid, max(minimumSurvivors, ceil(current/reductionFactor)))`.
  Failures remain in the report but cannot train continuation state or win promotion.
- Given enabled exploration and a non-greedy tail, when capacity permits, then at least one survivor comes from outside
  the ordinary greedy survivor set while the best candidate is preserved. The configured share is rounded upward and
  capped by available tail/survivor slots. No policy can guarantee that every late improver survives.
- Given one genome at multiple fidelities or repetitions, when measurements are requested, then identity binds the
  run, ordered initial cohort, plan, evaluator, fidelity and replicate. Search and confirmation have distinct identities
  and callback versions; the scheduler has no cross-fidelity evaluation cache.
- Given compatible state, when a higher search level begins, then the same candidate/replicate receives its own
  prior token and source sample identity. Wrong state versions restart; tokens from incomplete batches are discarded.
  Actual incremental or full-restart costs come from the evaluator, never an assumed discount.
- Given final survivors, when confirmation runs, then every request is full fidelity with **no search continuation
  token** and a fresh stream. Only a complete fresh confirmation can appear in `BestConfirmed`, and only after bracket
  completion. A search score alone cannot fill this property, even if confirmation fails or the budget expires.

## Costs, failures and evidence

Each actual measurement is reserved and reconciled through `EvolutionReplicateRunner<TGenome>`. Known failed work
keeps its returned cost; missing receipts conservatively charge the declared maximum. Maxima must be enforced by the
consumer's executor: ledger accounting does not terminate uncooperative work or prove that a broken producer stayed
inside its bounds. Cost overruns, cancellation and budget exhaustion return incomplete bracket evidence; fatal
out-of-memory errors propagate while admitted work retains its conservative charge.

The report retains every initial candidate ID, including candidates never dispatched before interruption; every
dispatched replicate batch; promotion source, rank and exploration reason; continuation acceptance/rejection counts;
and the independent ledger snapshot. `ChargedCostUnits` counts this bracket's measurements; the shared snapshot may
also contain other setup/proposal/compiler work. Candidate failures can be legitimately eliminated during a completed
bracket, so `IsComplete` is not a claim that every candidate succeeded or that all costs were exactly known. Inspect
the retained batches and unknown-cost counters. Only complete search batches are eligible to advance.

This complements the existing fixed cascade: a cascade screens an individual evaluation, while this scheduler
allocates fidelity across a population. A callback may compose an existing task/cascade, but must include all inner
work in its one receipt and use the supplied fidelity identity for any consumer cache. Do not wrap the same work in
another metering owner and double-charge it. Scheduler bookkeeping overhead is not a measured CPU-time charge.

## State and statistical boundaries

Continuation tokens are independently owned opaque byte sequences of at most 4096 bytes. They can reference a larger
consumer-owned checkpoint with a content hash, but the consumer must validate that external state, its environment
and storage access. Token properties expose metadata/hash, not payload; explicit copy methods return detached bytes.
The core does not execute, deserialize or fetch arbitrary external model state. Tokens can move between levels or
through an explicit coordinated settled-batch checkpoint (below). Worker leases and recovery of unjournaled in-flight
work remain separate work.

Search batches that reuse training state or were selected on early scores need not satisfy IID assumptions. Their
replication intervals are **exploratory**, not selection-corrected confidence. Final confirmation uses fixed fresh
batches and divides alpha across the maximum 64 candidates, under the declared bounded/fresh-IID measurement
assumptions. Stream separation and structural record checks do not prove physical independence, hidden-data isolation,
correctness or adequate statistical power. `BestConfirmed` is evidence for an application's promotion gate, not
automatic deployment or proof of improvement over a baseline. Keep confirmation feedback out of further search.

## Runnable example

```powershell
powershell -ExecutionPolicy Bypass -File eng/Test-MultiFidelity.ps1
dotnet run --project examples/MultiFidelitySearch -c Release -- 16 TestResults/quality/fidelity-example-new.json
```

The example compares greedy and exploratory allocation on aligned and deliberately late-improving synthetic curves.
Each run starts with eight identical candidates, executes 32 measurements (12 resumed and four independently confirmed),
and charges 92 incremental/full-restart steps plus 0.08 setup units under a 100-unit cap. These are declared synthetic
step costs, not trained-model performance or a real-workload speedup. The smoke requires correct allocation, identities,
accounting and exact replay; it does not require the exploratory policy to win each seed.

Full Hyperband bracket allocation, asynchronous promotions, durable worker leases, representative external workloads
and statistically confirmed release gains remain separate extensions. The generic callback contract supports real
evaluators, but these examples are not AiDotNet AutoML integration or a code-execution sandbox.

## Coordinated settled-batch checkpoint and resume

`RunCheckpointedAsync` uses the same promotion algorithm and semantic identity as `RunAsync`. After each fully
settled batch, its asynchronous sink receives an `EvolutionFidelityCheckpoint`; return `false` to pause before
further dispatch. A paused report never exposes `BestConfirmed`, even when the last confirmation batch just finished.
The immutable checkpoint exposes only run/version/count/checksum metadata through ordinary property serialization.
Call `ToJson()` explicitly to persist its sensitive ledger/evidence/model-token payload in a trusted store.

To resume, parse the saved checkpoint, restore `checkpoint.GetResourceState()` into a compatible fresh ledger,
and re-supply the exact run ID, ordered immutable canonical cohort, seed and plan/callback versions. Pass the
checkpoint to `RunCheckpointedAsync`. Current caps and the complete ledger state must match exactly. The scheduler
does not roll back spending, create free measurements or dispatch already settled sample identities.

Restoration validates every stored sample against its settled ledger receipt (including tombstones when diagnostic
receipt retention is zero), rebuilds statistics from accepted measurements, checks batch chronology against the
original deterministic promotions, and validates token source/replicate/version/hash and active membership.
The recorded batch prefix is replayed only as control-flow evidence: no evaluator callback or resource reservation
runs for that prefix. Actual evaluation starts only after that prefix has been validated. Original failures, sample
identities, promotion evidence and charges remain in the final report. Search tokens are cleared before confirmation.

Bounds: at most 576 settled batches, 64 initial candidates, 16 replicas, 4,096 bytes per current token and 16 Mi
UTF-16 characters per checkpoint envelope. Large provenance/token combinations can reach the envelope bound before
the population bound. Oversized capture fails after retaining already consumed work; it does not silently truncate.
Checksums detect corruption, not hostile forgery. Store permissions/authentication and atomic durable persistence are
the application's responsibility. There is no automatic model deserialization or arbitrary file access in the core.

Serialize the run and its ledger. Other in-flight reservations prevent capture; a sink that mutates the ledger is
rejected. Sink failures propagate before the next batch and measurement charges remain. A coordinated pause or
cancellation at a saved boundary can resume. **An old checkpoint is not permission to redispatch work after arbitrary
process death**: if anything was dispatched after capture, first reconcile its independently durable receipts/leases.
US-21 owns that distributed/in-flight protocol. Checkpoint I/O, startup and rehydration CPU are not automatically
metered; include them through an appropriate external accounting protocol before a whole-cost economic claim.

```csharp
EvolutionFidelityCheckpoint? saved = null;
var paused = await scheduler.RunCheckpointedAsync(runId, cohort, seed, async (checkpoint, token) =>
{
    await trustedStore.WriteAtomicallyAsync(checkpoint.ToJson(), token);
    saved = checkpoint;
    return false; // no next batch is dispatched
});
// In a new process: recreate compatible components and supply the same owned cohort.
var checkpoint = EvolutionFidelityCheckpoint.Parse(await trustedStore.ReadAsync());
freshLedger.RestoreState(checkpoint.GetResourceState());
var resumed = await recreatedScheduler.RunCheckpointedAsync(runId, cohort, seed,
    (next, token) => PersistNextBoundaryAsync(next, token), checkpoint);
```

The store methods in this sketch are application-owned, not APIs supplied by this package. The executable
`eng/Test-FidelityRecovery.ps1` uses three separate processes for baseline, saved pause and resume, and verifies
actual trained weights, data identities, every measurement, full report and ledger against uninterrupted execution.

## Actual incremental model-training workload

`examples/MultiFidelitySearch --regression` trains a four-feature linear model by full-batch gradient descent;
it does not manufacture scores from an epoch-count formula. Eight learning-rate/warmup configurations run at
4, 16 and 64 epochs on 128 training rows, with error measured on 64 held-out rows. A token contains owned weights,
completed epochs, genome/data/replicate identity and a weight hash. The training/data stream is fixed across
fidelities of each replicate. Confirmation starts new weights and separate training/held-out data at full fidelity.

The fixed-cohort comparison includes full-cohort retention, greedy halving, exploratory halving and restart-only
halving. Each has a 2,100-unit cap but stops after its finite bracket: **equal caps do not mean equal spent budgets**.
Actual epochs cost one declared unit each; 0.25 per measurement covers fixed data generation/scoring/token work;
initial cohort setup costs 0.08. Row visits are independently counted. These are synthetic prices, not measured CPU
time. The restart-only control isolates the saved repeated training without changing the selected trained result.
The authored synthetic dataset is not representative AutoML validation or a competitive-superiority benchmark.

```powershell
powershell -ExecutionPolicy Bypass -File eng/Test-RegressionFidelity.ps1
powershell -ExecutionPolicy Bypass -File eng/Test-FidelityRecovery.ps1
dotnet run --project examples/MultiFidelitySearch -c Release -- --regression 32 TestResults/regression-new.json
```
