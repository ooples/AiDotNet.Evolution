# Durable external-work delivery (US-21)

`DurableEvolutionWorkCoordinator` is an optional, bounded, single-owner local-filesystem
delivery coordinator. It persists queued work, worker profiles, leases, resource reservations,
results and external-response provenance together. `EvolutionDurableSessionBridge<TGenome>`
now connects it to a live fingerprinted engine session. `EvolutionWorkProtocol` exposes a
trusted JSON worker/control endpoint, the native host has a separate `--durable` mode, and
TypeScript/Python clients and an optional NativeAOT C ABI use that protocol. It does not restore an evolution engine's
pending proposals or operator state. Local implementation/acceptance checks pass with
[pinned native and managed evidence](../benchmarks/evidence/external-work/8aaef1e/README.md).
Hosted CI, review and cross-repository dependency approval remain merge gates.

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
authorization to reset the real budget. Automatic fork creation is not provided.

### Attaching a live engine session

Create a strict `EvolutionSession<TGenome>` with caller task/evaluator fingerprints and an
engine-owned `IEvolutionGenomeCodec<TGenome>`. Create the coordinator with that session's
`RunId` and `CompatibilityHash`, then attach:

```csharp
var bridge = new EvolutionDurableSessionBridge<MyGenome>(session, coordinator,
    decodeVersionedResult, requirements, estimatedDeliveryCost, maximumDeliveryCost);
foreach (var originalAsk in await session.AskAsync(8, cancellationToken))
    bridge.Enqueue(originalAsk); // Retain the original object until durable success.

bridge.ReconcileExpiredWork(); // Periodically, and before dispatching.
// Workers claim, heartbeat, and commit through the coordinator.
int newlyDelivered = bridge.DeliverAvailableResults();
```

The bridge stores the original engine ticket separately from each physical worker lease.
Results return through the original ticket, never a lookup of the current attempt by numeric
ID. The encoded `EvolutionDurableEvaluationPayload` carries the full canonical identity,
engine codec payload and exact evaluation context. Int64 evaluation IDs and UInt64 random
seeds are decimal **strings** in that JSON envelope, preserving all bits across JavaScript.
The caller's evaluator fingerprint must include the result-decoding protocol version.

Each session has a unique `InstanceId`. Reopening a coordinator can reattach to that same
still-live session; a new session with the same run and compatibility is rejected. The
caller owns both lifetimes. Coordinator reconstruction is not full engine-process recovery.
Check persisted attachment before constructing a replacement engine where practical;
constructing a new engine may already begin work that a bridge cannot undo.

After an enqueue I/O failure, reopen the coordinator and retry the same retained ask object.
After a result-tell acknowledgement failure, replay is fenced: it cannot tell or charge the
source attempt twice. A replay count of zero does not prove that the first tell failed.
Decoder failures leave the raw durable result and receipt available for explicit handling;
no implicit success, refund or archive admission is inferred. Periodic cancellation polling
is cooperative and never proves physical work has stopped.

The authoritative work-state schema and contract are now **version 2**. Old version-1
journals are deliberately refused, not silently migrated: older code must not ignore the
new source-session fields and reinterpret attached work as an unbound queue. Keep old
artifacts with their pinned source when reproducing version-1 evidence. Schema omission,
old schema and incomplete source-ticket/acknowledgement state are rejected even when the
outer journal checksum is valid.

## Storage boundary

The store holds an exclusive `work.owner` handle. `work.current` contains a bounded UTF-8
state with revision/length/checksum, written to a unique same-directory temporary file,
flushed and atomically replaced. Resource and delivery state are one publication. An error
around publication faults the live writer; dispose and reopen to discover which state won.
`HasTemporaryCleanupFailure` records a failure to remove that writer's unique temporary
file without replacing the original publication exception. It is a per-instance diagnostic,
not persisted state and not permission to delete current work or reset its budget.

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

Latest verified runtime: **8aaef1e**, including US-20 cleanup fix **96d245e**.
**1,000 net10.0 / 1,000 net8.0 / 800 net471 tests pass**, zero skips;
coverage is **92.96% line / 79.64% branch**, passing the unchanged ratchet. All 139 native
TypeScript tests, six Python tests on each managed/native host, and six exported C ABI
tests pass. Strict .NET 10 native delivery, live-engine and shared-library builds emit
zero warnings. All three packaged DLLs match their explicit builds. The
[current evidence archive](../benchmarks/evidence/external-work/8aaef1e/README.md) retains
raw journals, checkpoint payloads, reports, logs and failed approaches with an offline verifier.
The following older counts describe implementation history, not the current test totals.

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
The additional engine bridge and worker/binding integration is described in the
[worker protocol contract](DURABLE_WORKER_PROTOCOL.md). Hosted CI and Linux/macOS execution
must be checked separately; local Windows evidence does not establish those outcomes.

[Pinned managed/native reports and authoritative snapshots](../benchmarks/evidence/external-work/75fd8be/README.md)
retain the process-crash evidence and its exact source/binary identities.

The schema-2 bridge/protocol integration additionally passes **985 net10.0 / 985 net8.0 /
785 net471 tests**, zero skips, with 92.95% line / 79.73% branch coverage and the unchanged
ratchet. This includes 18 live-session bridge cases, 17 JSON protocol cases and two host
framing cases (the host cases are excluded on net471). All **139 TypeScript tests** pass
against the Windows NativeAOT host. The six Python cases pass against both that native host
and its managed build; subcases exercise malformed responses, downgrade, timeout and bounds.
Both clients actually kill an owned coordinator child after a published dispatch and verify
recovered identity/reservation/receipt state. These are authored correctness/recovery checks,
not optimization-quality comparisons or evidence of exactly-once physical execution.

`examples/DurableSession` exercises the engine-owned codec and full random context through
a borrowed JSON protocol, coordinator reopen with the original engine still alive, fenced
result delivery into its archive, and refusal of a new matching engine instance. Its single
square-function evaluation consumes two authored cost units, not measured money or CPU.
Its managed run passes. The first strict net10.0 NativeAOT build **failed** IL2026/IL3050/IL2070
analysis in existing engine persistence, resource-metered variation, curiosity state,
measurement serialization and ownership inspection. The shipped net8.0 native host's passing
client tests did not resolve this broader engine-AOT boundary. The follow-up separates
non-generic checkpoint/proposal documents from generic engine classes and uses generated
serializer metadata for those documents, curiosity state and measurement origins. Three
compatibility tests compare generated JSON byte-for-byte with the previous encodings,
including nested records, optional provenance and exact decimals. The initial strict net10
native bridge probe subsequently builds and runs with **zero warnings**; the original failed
build remains part of the evidence history. No warning gate was disabled or suppressed.

When dynamic code support is disabled (including NativeAOT and managed hosts configured with
AOT feature switches), custom struct genomes must implement `IImmutableEvolutionGenome<T>`.
Missing trimmed field metadata is never treated as proof of deep immutability. Strings,
primitives, enums and the documented immutable BCL values remain implicit; ordinary
dynamic-code-enabled managed execution retains recursive value-field inspection. Additional
probe cases check explicit struct ownership and actual completed-boundary checkpoint
serialization/resume. They do not claim arbitrary partial-batch crash continuation.
