# Resource reservations and receipts

US-05's generic budget extension is opt-in. Existing `CostUnits` and evaluation-attempt counters keep their meanings;
neither is automatically a currency. The numeric runner now independently reconciles evaluator cost units and proposal
calls using this ledger. Its v2 records remain development evidence, not model-cost or CPU/GPU-time measurements.

```csharp
var ledger = new EvolutionResourceLedger("campaign/task/run", new EvolutionResources(
    new Dictionary<string, decimal> { ["cost_units"] = 100, ["input_tokens"] = 10000 }));

var meteredTask = new ResourceMeteredEvolutionTask<MyGenome>(task, ledger,
    maximumStageCostUnits: new[] { 1m }); // one maximum per cascade stage when applicable

// Pass meteredTask to EvolutionEngine. Meter other stage work at its actual charging boundary:
var candidate = await EvolutionResourceWork.RunAsync(ledger, "proposal/17/model/attempt/1",
    EvolutionResourceStage.Proposal,
    EvolutionResources.Of("input_tokens", 200),
    EvolutionResources.Of("input_tokens", 400),
    async cancellation =>
    {
        var response = await proposer.GenerateWithLimitAsync(400, cancellation);
        return new EvolutionResourceResult<MyGenome>(response.Genome,
            EvolutionResources.Of("input_tokens", response.InputTokens));
    });
```

The producer and response in the snippet are application-supplied. There is no built-in network call or paid provider.
Use `EvolutionResourceWork` around setup/tuning, proposals, refinement/repair, surrogate training/inference, novelty,
screening, evaluation and final confirmation. Give every retry, fidelity and replicate a distinct operation ID. Assign
one accounting owner to each actual charge: do not also charge a nested operation already included in an aggregate receipt.

## Given / When / Then

- Given concurrent requests, when a maximum is reserved, then admission atomically checks spent plus all in-flight
  maxima against every declared cap. Planning estimates may be lower; maximums must be enforceable by the producer.
- Given actual consumption, when a receipt settles, then the maximum is released and the actual amount is charged once.
  An identical duplicate is harmless; a conflicting receipt cannot change history. Failures and rejected work still cost.
- Given cancellation or an exception without a receipt, when the reservation is disposed, then its maximum is charged
  as **unknown**, not refunded or misrepresented as a measured amount. A process still running remains the producer's
  responsibility. Abandoned engine evaluators can keep their reservation until their asynchronous work actually ends.
- Given a producer exceeds its maximum, when its actual receipt arrives, then the full amount is retained, the violation
  is visible, and all further admission stops. The ledger cannot retroactively prevent an external contract violation.
- Given cascade rejection, when the engine refunds `MaxEvaluationAttempts`, then stage resource receipts remain charged.
- Given receipt-detail truncation, when a snapshot is read, then complete spent/reserved/settled/unknown counters and
  dropped-detail counts remain available. A bounded operation-identity table prevents replaying forgotten charges.
- Given a matching engine boundary and ledger snapshot, when both restore, then pending reservations and already-spent
  operations survive. Changing caps does not erase spending; a cap below existing commitments refuses further admission.

## Boundaries that matter

Resource accounting must **not** roll back with archive transactions: failed, cancelled and discarded search work is
still real consumption. Explicitly persist the ledger alongside the corresponding engine checkpoint, or use a durable
coordinator for stronger crash guarantees. `CaptureState` is not an automatic journal. Never replay pending external work
without resolving its receipt or lease; `GetPendingReservation` rebinds accounting, not permission to dispatch again.

The task adapter meters full calls or individual cascade stages, never cache hits. It does not silently instrument arbitrary
model code, compilation, canonicalization, setup, or refinement; those producers must use the stage helper. All resources
need declared caps. Token and currency conversion, wall-time measurement, hard process isolation and transport-specific
limits remain consumer responsibilities. Rejected reservations do not invoke the producer.

The task adapter distinguishes **its own pre-dispatch denial** from an exception thrown inside a dispatched producer.
Missing receipts become failed/cancelled outcomes with conservative maximum costs visible to both the ledger and engine;
successful retries retain those prior-attempt costs and the `resource_cost_unknown` diagnostic. A positive double cost
that rounds to decimal zero, or a cost above the ledger's representable bound, fails closed, charges the maximum as unknown
and retains the reported value in a diagnostic. A representable maximum overrun retains its actual cost and fails that
candidate's evaluation. Neither case can silently become a successful free candidate. Fatal runtime exceptions still
propagate after conservative settlement. This changes the adapter semantic version to `resource-metered-task-v2-fail-closed-costs`;
old adapter checkpoints are intentionally incompatible. Ordinary in-bound numeric fixture receipts are unchanged.

Individual concurrent reservations are serialized in arrival order. `ReserveBatch` now
admits a wave of 1..1024 requests in **ordinal operation-ID order**, under one ledger
lock, before any returned handle can be settled. IDs should include zero-padded
dispatch ordinals when numerical priority is intended. All requests are validated
before mutation; malformed, duplicate or previously admitted identities leave the
ledger unchanged. Budget denials are explicit null reservations. Different waves
still require a predetermined coordinator order.

```csharp
var wave = ledger.ReserveBatch(new[]
{
    new EvolutionResourceRequest("0001/refine", EvolutionResourceStage.Refinement,
        EvolutionResources.Of("cost_units", 1), EvolutionResources.Of("cost_units", 3)),
    new EvolutionResourceRequest("0002/novelty", EvolutionResourceStage.Novelty,
        EvolutionResources.Of("cost_units", 1), EvolutionResources.Of("cost_units", 3))
});
// Dispatch only non-null reservations. Reconcile or dispose every admitted handle.
// Savings from completed work fund the NEXT wave, not timing-dependent backfill.
```

Snapshots now expose `Stages`: complete admitted/settled/unknown counts and
spent/reserved vectors for setup/tuning, proposals, refinement, surrogate fitting
and scoring, novelty, screens, evaluation, confirmation and persistence. These
totals derive from the bounded operation ledger, not the truncated receipt queue;
snapshot cost is linear in retained operation identities and declared resources.
Do not use a snapshot as a high-frequency per-token polling API. Unknown charges
remain conservative maxima, not measured bills. Existing cascade tests verify
screening and full-stage totals with both values of `ChargeRejectedStagesToBudget`.

`EvolutionResourceWork.RunAsync` now throws `EvolutionResourceLimitExceededException`
after retaining an actual overrun. Previously the helper returned the stage value
despite the ledger's violation flag. No candidate/refinement/model value from that
failed call is returned as admissible. Direct reservation users still own producer
result validation. The exception does not undo charges or terminate remote work.

## Coordinated engine/accounting boundary

At a paused engine boundary, use `EvolutionResourceBoundary.Capture(ledger,
captureEngineCheckpointJson)`. It holds the ledger lock, rejects pending work before
calling the engine snapshot callback, and rejects a callback that mutates accounting.
Any reentrant work remains charged even when capture fails. `SaveNew(path)` flushes
one checksummed engine+ledger envelope then publishes it by same-directory rename,
refusing overwrite. Receipt truncation cannot detach spending from this envelope.

Retain the expected checksum externally, deserialize the envelope against it, and
restore its ledger and engine into fresh objects. Publish the pair only after both
validate. Current caps can change, but spending and duplicate protection survive;
lowering a cap below prior commitments prevents further admission. An integration
test resumes the actual engine from the paired envelope and compares its final
ledger state byte-for-byte with an uninterrupted run.

This is **not a crash journal or rollback-proof latest-pointer service**. Do not
resume an old envelope after unjournaled external spending. Pending external work
must be resolved before capture; an arbitrary engine callback is not automatically
paused by the ledger. Trusted checkpoint custody and external resource enforcement
remain required. File flush/rename does not promise portable directory fsync or
distributed exactly-once delivery.

This core scope supplies stage contracts, deterministic wave admission, coordinated
quiescent persistence, fail-closed overrun handling and evaluator integration.
Consumer-specific model/compiler/device metering and currency conversion still need
their own receipt/isolation evidence; this PR does not silently instrument arbitrary
consumer code or claim that every external producer already has a hard bound.
