# External session identity and retry safety

US-21's **local session and strict host/TypeScript contracts** are implemented. Durable worker delivery remains in progress. The host is not yet a distributed service or restart-safe queue.

Use the fingerprinted `EvolutionSession<TGenome>` constructor with an injected canonical identity function and `EvolutionExternalTaskIdentity(taskId, taskVersionHash, evaluatorVersionHash)`. The task version must cover canonicalization and representation semantics; the evaluator version must cover the evaluation/data/environment protocol. The library does not infer trustworthy versions from delegate names. Supply the matching `IEvolutionGenomeCodec<TGenome>` through the engine factory when persistence is required; its ID/version also participates in `session.CompatibilityHash`.

```csharp
using var session = new EvolutionSession<MyGenome>(
    task => new EvolutionEngine<MyGenome>(task, variation, archiveFactory, options,
        genomeCodec: codec),
    seeds,
    genome => canonicalizer.GetStableId(genome),
    new EvolutionExternalTaskIdentity("my-task", "task-and-canonicalizer-v1", "evaluator-v1"));

foreach (var work in await session.AskAsync(4, cancellationToken))
{
    var result = await EvaluateExternally(work, cancellationToken);
    bool accepted = session.TellAttempt(work.WorkIdentity!, result);
    // False means stale, duplicate, wrong-session/run, expired, or no longer outstanding.
}
```

The caller supplies the domain-specific objects in this example. Never use display text as identity unless it actually is the complete stable canonical representation. Immutable genome ownership remains the engine's existing contract.

## Work tickets

Every session-issued ask now includes a non-null `EvolutionWorkIdentity` with `RunId`, `EvaluationId`, one-based `Attempt`, and an opaque `LeaseId`. Return the entire ticket unchanged. JSON-round-tripped tickets are supported. An engine retry can reuse the evaluation ID, but never the old delivery token. Independently created sessions receive different tokens even if run/evaluation/attempt IDs are identical.

`TellAttempt` matches every ticket field while holding the same handover lock as work registration. A stale or conflicting ticket cannot remove a replacement attempt. Competing duplicate results settle at most one outcome. Completed/expired pending-state cleanup removes only the same pending object, not a newer replacement with its evaluation ID.

The three-argument constructor and `Tell(long evaluationId, result)` remain for local compatibility. **That legacy tell is accepted only for a first attempt, never a replacement**, and fingerprinted sessions reject it entirely. Numeric IDs alone do not guard cross-session results; external transports must use `TellAttempt`.

## Strict host and TypeScript mode

Supply `config.taskIdentity = { taskId, taskVersionHash, evaluatorVersionHash }` to require complete `workIdentity` tickets. The native host confirms `workIdentityVersion: 1`, `requiresWorkIdentity: true` and `compatibilityHash` in its open response; the TypeScript client refuses a silent downgrade to an older host. Asks carry `{ runId, evaluationId, attempt, leaseId }`. Copy that ticket unchanged into the corresponding scored result, alongside its matching top-level `evaluationId`; do not replace it by looking up the latest ticket for that numeric ID.

The host validates all ticket shapes in a tell batch before committing any prefix. Valid but stale/cross-session/mismatched/duplicate tickets return no acceptance. Strict sessions reject missing tickets; malformed tickets return a protocol error. `evaluationTimeoutMs` is optional and positive; nonnegative `maxRetries` requires strict mode when greater than zero. Each retry consumes another evaluation attempt, and neither timeout nor rejection terminates physical external work.

An explicit reflection-free parameter codec pins the ordered parameter names, resolved steps, bounds and integral flags in engine compatibility. It restores exact serialized numeric values without running quantization again. Caller task/evaluator versions and archive definitions participate separately. The archived `best` summary has no live work ticket.

Legacy host configurations without `taskIdentity` remain supported for first-attempt numeric-ID tells, but do not acquire strict guarantees. This opt-in transition is a protocol compatibility choice, not a recommendation for new external integrations.

## Timeouts and stopping

An expired queued entry may still have a queue permit while the next attempt is in retry backoff. `AskAsync` consumes such obsolete permits and continues waiting. It returns an empty batch only when the session closes; a timed-out queued attempt is not an end-of-run signal. Caller cancellation interrupts that wait.

The opaque local token does **not** implement a durable lease clock, heartbeat, cross-process coordinator, authentication, or worker termination. Local attempts are governed by engine timeout/cancellation. Work already dispatched may still consume resources after its result is refused. Rejection does not refund or erase that cost; the durable resource protocol must reconcile it against the original attempt's reservation, not a replacement's.

## Verification and remaining work

Two adversarial regressions were reproduced before fixing them: an old evaluation-ID-only tell completed attempt 2 after attempt 1 timed out, and an expired unasked entry caused `AskAsync` to return empty while retrying. Both passed alongside cross-session token rejection, JSON ticket round trips, 16-way duplicate results, task/evaluator/codec compatibility and input-bound tests: **915 tests on net10.0, 915 on net8.0 and 723 on net471** before host integration (legacy excludes native-host tests).

The subsequent strict-host slice passed **921 tests on net10.0** and **129 TypeScript tests with zero skips**, including real Windows x64 NativeAOT cross-session/retry cases and downgrade/missing-ticket fixtures. Native publication succeeded but emitted trimming/AOT warnings in existing core persistence/measurement/ownership paths; this is not a claim of warning-free AOT persistence or verified Linux/macOS execution.

Remaining US-21 functionality: persistent pending work/reservations/results, authoritative restart reconciliation, durable leases/heartbeats/cancellation, compatible-worker selection, durable-protocol binding integration, and exact partial-batch continuation only with persisted proposal/operator/external-response provenance. Otherwise continuation must be explicitly labeled a fork. A newest-valid algorithm-checkpoint fallback cannot alone authorize redispatch: it may omit already-dispatched work from a newer corrupted snapshot.
