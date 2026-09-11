# Durable external-work delivery (US-21, in progress)

`DurableEvolutionWorkCoordinator` is an optional, bounded, single-owner local-filesystem
delivery coordinator. It persists queued work, worker profiles, leases, resource reservations,
results and external-response provenance together. It is not yet wired into the native host
or TypeScript binding, and it does not restore an evolution engine's pending proposals or
operator state. US-21 remains incomplete until those integrations and verification are done.

## Delivery and accounting

```csharp
using var coordinator = new DurableEvolutionWorkCoordinator(
    directory, runId, compatibilityHash,
    EvolutionResources.Of("evaluation_calls", 100));

coordinator.Enqueue(evaluationId, attempt, canonicalGenomeId, codec.Serialize(genome),
    new EvolutionWorkRequirements(new[] { "cuda" }, EvolutionResources.Of("gpu_slots", 1)),
    EvolutionResources.Of("evaluation_calls", 1),
    EvolutionResources.Of("evaluation_calls", 1));

var worker = new EvolutionWorkerProfile(workerIncarnationId, compatibilityHash,
    new[] { "cuda" }, EvolutionResources.Of("gpu_slots", 1));
EvolutionWorkLease? lease = coordinator.Claim(worker);
// After executing under the declared maximum, return the original complete identity:
coordinator.Commit(lease!.Identity, worker.WorkerId, serializedResult, responseProvenance,
    EvolutionResources.Of("evaluation_calls", 1));
```

The example's caller supplies domain variables, canonicalization, codec, evaluation and
provenance. Handle a null claim before doing physical work. The compatibility fingerprint
must cover task, evaluator/data/environment, canonicalization and codec versions. A result
marked accepted by delivery is not independently verified correct: the engine/evaluator
must still validate it before archive admission. The coordinator's ledger meters its
declared **evaluation deliveries**, not automatically every proposal, storage or campaign stage.

| Event | Logical work | Original physical resource reservation |
| --- | --- | --- |
| Claim | A new run/evaluation/attempt/lease ticket is durably assigned. | Maximum reserved before the lease is returned. |
| Heartbeat before expiry | Lease deadline extends durably. | Unchanged. |
| Expiry | Retry becomes eligible up to the delivery limit; new lease token required. | Remains reserved, even if a replacement is dispatched. |
| Cancellation | No future result from canceled work becomes the logical result. | Remains reserved until a final actual receipt. |
| Late receipt | Stale result cannot update the replacement. | Settles the original reservation exactly once. |
| Repeated identical receipt/result | Duplicate, not another update. | No second charge; conflicting duplicates are rejected. |
| Maximum violation | Offending result is not accepted; further admission stops. | Reported overrun is retained and charged, not clipped. |

The engine attempt and physical delivery number are distinct: several leases may deliver
one engine attempt, but each delivery has a different token and reservation. Expiry does
not prove that an external operation stopped. Missing receipts can therefore keep resources
reserved indefinitely. They are not reported as measured spend or silently refunded.

Worker tags are case-sensitive and all required tags must match. Hardware capacity and
maximum concurrency are checked across that incarnation's **unsettled** deliveries,
including expired work. A worker cannot enlarge/change its profile while it owns unresolved
work. A restarted process should use a new incarnation ID unless reconciling an old physical
operation; the external supervisor is responsible for actual hardware isolation.

`GetUnsettledDeliveries` supports reconciliation, not permission to repeat an operation.
Worker-side durable execution IDs and receipts are needed to avoid executing the same
physical job twice after a worker crash. Check heartbeat/cancellation before continuing;
coordinator result idempotency alone cannot guarantee exactly-once physical execution.

## Recovery and explicit search forks

Reopen the same directory with exactly the same run, compatibility, resource limits and
options. `WasRecovered` distinguishes recovered delivery from a fresh coordinator.
`GetResult` and `GetDeliveryResult` are non-consuming reads, so lost acknowledgements can
be reconciled without dispatching again. Backwards UTC clock movement is refused, including
on recovery; do not reset the stored clock to bypass that check.

`SupportsExactSearchContinuation` is **false**. `SearchContinuationGuarantee` explicitly
labels this as delivery-only recovery requiring a fork for partial-batch search continuation.
Do not attach old logical results to a new engine merely because their numeric IDs coincide.
The integration must retain the original full session ticket, validate compatibility, and
report a new/forked trajectory if proposal/operator/search state was lost. A fork must also
carry forward campaign spend and unresolved liabilities; creating a fresh directory is not
authorization to reset the real budget. Automatic engine/fork integration is still pending.

## Storage boundary

The store holds an exclusive `work.owner` handle. `work.current` contains a bounded UTF-8
state with revision/length/checksum, written to a unique same-directory temporary file,
flushed and atomically replaced. Resource and delivery state are one publication. An error
around publication faults the live writer; dispose and reopen to discover which state won.

Missing, corrupt or incompatible current state is a hard failure. There is no newest-valid
checkpoint fallback: an older snapshot could forget already dispatched work and charges.
The owner marker prevents a missing published file from being mistaken for a fresh run.
Completed/canceled work and worker profiles remain bounded tombstones rather than being
evicted to make duplicate protection disappear. Admission stops at retention/state bounds.

This is a **local filesystem, process-crash** contract. Checksums are not authentication;
worker declarations and the storage directory must be trusted. Network-filesystem locking,
hostile rollback/backup restoration, and directory durability after power loss are not
guaranteed. A production service needs authentication, deployment isolation and an appropriate
durable storage provider; this library does not claim those from an in-process API.

## Verification

The initial implementation passed 17 coordinator tests and 10 journal tests on net10.0,
including pre/post-publication failures, lost acknowledgements, stale receipt settlement,
concurrent duplicates, capacity matching, cancellation, clock rollback and corruption.
Existing 25 ledger tests also passed after its state serialization moved to source generation.
The full suites subsequently passed **948 net10.0, 948 net8.0 and 750 net471 tests**, all with
zero skips. Generated-code-excluded coverage was 92.85% line / 79.73% branch, passing the
unchanged repository ratchet. The legacy target excludes the native-host test surface.

The real-process harness additionally kills a coordinator after publishing a dispatch,
kills another after publishing its result, and kills a worker before its final receipt.
It verifies recovered identities/receipts and retained lost-worker liability. Run it with:

```powershell
dotnet build examples/DurableWork -c Release -m:1 -p:UseSharedCompilation=false
dotnet examples/DurableWork/bin/Release/net10.0/DurableWork.dll verify TestResults/durable-work
```

This is authored crash-recovery evidence, not a search-quality or competitor benchmark.
The cost units are declared test units, not measured CPU time or money. A standalone Windows
x64 NativeAOT build and the same three-process-kill recovery probe also passed; that build
emitted no warnings. This is specific to the durable coordinator and its source-generated
ledger/work serialization, not all engine persistence paths in the separate native host.
Hosted CI, Linux/macOS and durable engine/binding integration remain unverified or unfinished.
