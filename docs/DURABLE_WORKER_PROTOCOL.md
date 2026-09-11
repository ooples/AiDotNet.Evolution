# Durable worker/control protocol v1

`EvolutionWorkProtocol` exposes the same coordinator through bounded JSON messages. It can
own a coordinator opened by `open`, or borrow an existing coordinator shared with an
`EvolutionDurableSessionBridge<TGenome>`. The latter lets an application host external
workers while keeping the original engine session alive. The application must periodically
reconcile expired source work and deliver committed results through the bridge.

The NativeAOT executable supports a separate `--durable` mode. This mode owns **delivery
state only**: it never constructs or restores an evolution engine. Its journal can recover
pending work, leases, reservations and receipts after process restart. Lost engine/operator
state still requires an explicitly labeled search fork, retained campaign costs and unresolved
liabilities. There is no automatic fork, budget reset or exact partial-batch continuation.

This is a trusted local pipe/library protocol, not an authenticated network service. A
remote deployment must add access control, transport security, worker isolation and durable
physical-operation receipts. Anyone with control access can enqueue/cancel work or declare
costs; a lease token is correlation, not authorization.

## Wire contract

Each request is one UTF-8 JSON object followed by a newline, carrying `protocol: 1`, a
positive JavaScript-safe numeric `id`, and `op`. Replies echo the ID and protocol and include
`ok`. Failed requests include `error` and a reconciliation reminder. Duplicate and unknown
request fields are rejected. Requests/replies are bounded to 16 MiB in UTF-8, with the
coordinator's separate smaller limits on each payload, provenance and canonical ID.

All **evaluation IDs** are canonical nonnegative Int64 decimal **strings**. Every resource
amount is a canonical nonnegative decimal **string**, with no exponent, leading zero,
trailing fractional zero or precision loss. For example `"0"`, `"12"`, `"0.125"` are valid;
`12`, `"01"`, `"1.0"`, `"1e0"` are not. The core's decimal range and resource bounds also
apply. Int32 attempts, delivery numbers and option counts remain JSON numbers. The bridge
evaluation envelope separately preserves UInt64 root/stream seeds as decimal strings.

```json
{"id":1,"protocol":1,"op":"open","config":{"directory":"/owned/run-work","runId":"run-7","compatibilityHash":"caller-pinned-contract","limits":{"evaluation_calls":"100"}}}
```

`config` optionally supplies `maximumWorkItems`, `maximumDeliveriesPerWork`,
`maximumStateBytes`, `maximumPayloadBytes`, `maximumWorkers`, and `leaseDurationMs`.
Defaults and bounds are the same as `EvolutionWorkCoordinatorOptions`. Reopening must
match the original run, compatibility, limits and all options exactly. Old work-state
schema 1 is incompatible with current schema 2; wire protocol version is a separate contract.

| Operation | Request fields in addition to the envelope | Successful reply |
| --- | --- | --- |
| `open` | `config` | Status and explicit delivery-only guarantees. |
| `enqueue` | `job`: `evaluationId`, `attempt`, `canonicalGenomeId`, `payload`, `estimated`, `maximum`; optional `tags`, `minimumResources` | `enqueued`: true if new, false if identical work already exists. Conflicting work is rejected. |
| `claim` | `worker`: `workerId`, `compatibilityHash`; optional `tags`, `capacity`, `maximumConcurrentWork` | `available` and `lease` (or null). **Unavailable now is never search completion.** |
| `heartbeat` | Original `identity`, `workerId` | `status`: `renewed`, `expired`, `canceled`, `completed`, `unknown-lease`. |
| `cancel` | `evaluationId`, `attempt` | `canceled` boolean. No physical termination or refund is implied. |
| `commit` | Original `identity`, `workerId`, `payload`, `provenance`, `actual`, `outcome` | `disposition`: `accepted`, `duplicate`, `stale`, `duplicate-stale`, `unknown-lease`, `budget-violation`. |
| `result` | `evaluationId`, `attempt` | Logical `result` or null; non-consuming. |
| `delivery` | Original `identity`, `workerId` | That physical delivery's receipt/result or null, including stale/overrun receipts. |
| `unsettled` | `workerId`; optional `afterLeaseId` | One `lease` or null, `nextAfterLeaseId`, `reconciliationOnly: true`. |
| `status` | None | Run/compatibility, original `sourceSessionId` or null, recovered flag, spent/reserved/admitted/settled/denied, maximum-violation flag, delivery-only guarantee. |
| `close` | None | `closed: true`. Releases an owned store, not reservations. Borrowed coordinators remain caller-owned. |

A lease contains `identity` (`runId`, string `evaluationId`, `attempt`, `leaseId`), `workerId`,
`canonicalGenomeId`, `payload`, `deliveryNumber` and ISO-8601 UTC `expiresAt`. A heartbeat
renews the deadline in the coordinator; the prior lease object is not a live deadline view.
Receipt `outcome` must be `completed`, `failed`, `rejected` or `canceled`, with actual resource
amounts. Unknown actual consumption is **not** accepted as a fabricated zero receipt.

`unsettled` uses a single-lease keyset page to bound payload amplification. Pass its
`nextAfterLeaseId` to continue; a null lease ends that scan, not the search. Concurrent new
claims can sort before the cursor: restart the scan when reconciling new claims. Settlement
of a prior page cannot shift an offset and silently skip the next original lease.

## Clients and recovery discipline

The TypeScript package exports `DurableWorkClient`, exact wire types and
`parseDurableEvaluationPayload`. The Python package exports `DurableWorkClient` and
`parse_evaluation_payload`; it needs no runtime Python dependencies. Both launch only their
owned local host process, verify protocol and run/compatibility, bound requests/replies,
validate operation-specific responses, and refuse lossy numeric resource amounts. Python
serializes calls; TypeScript permits at most 32 outstanding requests.

Timeouts include blocked writes and reads. A transport/protocol failure tears down the
owned endpoint and requires explicit reopening/reconciliation. There is **no automatic
physical retry**. Persist the physical operation's execution ID and final receipt before
submitting it; after a lost acknowledgement inspect `delivery`, then resend only the exact
same receipt if needed. Expiry or cancellation retains the original reservation until an
actual receipt arrives. A resumed worker normally uses a new incarnation ID.

An accepted delivery result still requires the caller's pinned decoder and independent
evaluation validation before archive admission. `status.searchState` is `not-owned` and
`supportsExactSearchContinuation` is false. None of these responses claim a finished search,
representative optimization gains or competitor superiority.

See [durable accounting and engine bridge](DURABLE_EXTERNAL_WORK.md),
[TypeScript usage](../bindings/typescript/README.md), and [Python usage](../bindings/python/README.md).
