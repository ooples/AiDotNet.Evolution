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

Concurrent reservations are serialized in arrival order. For reproducible cutoff decisions, reserve in a predetermined
dispatch order or use a fixed single-worker campaign. Worker-timing-dependent admission must not be claimed to have the
same deterministic guarantees as an unconstrained evaluator. The numeric harness fixes one worker and exact unit costs.

This extension supplies the generic contracts and evaluator integration; US-05 remains open until the model/compiler,
refiner and experiment adapters each demonstrate complete accounting of their own work and shared budget enforcement.
