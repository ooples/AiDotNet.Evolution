# Measurement origin and copied evidence

US-23 needs persistent reuse to save work without misrepresenting old measurements as fresh samples.
`EvolutionMeasurementOrigin` is the provenance foundation for that integration, not a persistent cache itself.

## Producer contract

Attach immutable metadata with `EvolutionTaskResult.WithMeasurementOrigin(origin)`. Existing constructors and
results without origin remain supported. The producer declares:

- The exact `EvolutionReuseScope.StableKey`, original run/evaluation labels and globally stable original sample IDs.
- Original observation time, acquisition cost and versioned cost unit/statistics policy.
- Optional standard error and an all-or-none confidence interval with its confidence level.
- `Measured`, `PersistentReuse`, `RunLocalReuse` or `MigrationCopy` origin.

One record retains 1–256 distinct sample IDs, bounded printable Unicode labels and at most 512 KiB of strict UTF8
JSON. `SampleSetHash` depends on the scope and sorted original IDs, not their ordering or copy destination.
`AsReused` preserves original IDs, count, uncertainty, time and cost; it cannot relabel a copy as `Measured`.
It does not prove the producer is truthful, observations are independent, confidence intervals are calibrated,
hardware is attested, or timing measurements remain representative. Labels must not contain secrets.

`OriginalCostUnits` is acquisition provenance. `EvolutionTaskResult.CostUnits` and the resource ledger account
for the current operation. Never charge acquisition cost again merely because evidence was copied, and never
omit repertoire construction/acquisition cost from a fair warm/cold comparison.

```csharp
var origin = new EvolutionMeasurementOrigin(
    scope.StableKey, originalRunId, originalEvaluationId, originalSampleIds,
    observedAt, acquisitionCost, "worker-calls-v1", "fixed-sample-policy-v1");
return measuredResult.WithMeasurementOrigin(origin);
```

This example annotates a genuine measurement. A persistent store must separately validate its exact key,
freshness, force-fresh policy, correctness requirements, storage integrity and current lookup/validation costs.

## Engine and file behavior

Given origin-bearing evidence, when the engine strips optional artifacts, rebuilds descriptors or copies a
cached result with zero current cost, then original provenance remains attached. A run-local hit is marked
`RunLocalReuse`; island migration is marked `MigrationCopy`. `EvolutionCacheStatus` still describes only the
engine's run-local memo, not whether a task itself reused external evidence.

Given an origin-bearing checkpoint, when it is resumed or read offline, then archive, history, elite and cache
metadata is restored. Malformed origin is rejected during package-bound validation before custom genome decoding.
The inner engine payload is schema 7 when retained origin exists, and schema 6 otherwise. Older engine readers
reject schema 7. Null fields are omitted; default hashing does not append an origin component. Origin-bearing
state hashes include its exact metadata, including observation time: callers must not expect replay across
different observation timestamps to have identical state hashes.

Given an origin-bearing evaluation, when tracing writes JSON Lines or JSON, compressed or uncompressed, then
the record retains its typed origin and uses record schema 2. Records without origin remain schema 1. The JSON
document container and summary retain their own existing schema 1; record versions are independent. The new
reader rejects unsupported record versions and schema/origin mismatches. **Legacy trace readers ignored schema
versions and may silently discard new fields**: provenance-aware analysis must use the updated reader. Generic
`JsonSerializer` deserialization of task/evaluation objects is not a replacement for these explicit file readers.

Given origin in a cascade stage, when cascade merging runs, then it fails with
`cascade_measurement_origin_unsupported`, retaining the received origin, diagnostics and current costs.
Stage-specific combined provenance is not implemented; do not silently erase it or claim cascade reuse support.

## Independent replication

Given an origin-bearing callback receipt, when `EvolutionReplicateRunner` executes a fresh batch, then it accepts
only `Measured` origin with exactly one sample ID equal to the supplied `EvolutionReplicateContext.SampleIdentity`.
All supplied origins in the batch must share a scope. A reused receipt, aggregate, wrong/duplicate ID or changed
scope makes the batch `InvalidMeasurement`: no successful mean, standard error or interval is exposed, but the
dispatched receipt, origin and current charge remain available. Unknown costs retain their conservative charge.
Callbacks without origin retain the existing explicit fresh-IID producer contract; metadata cannot detect a lie
or prove physical independence. Runner semantic identity advances to `fresh-replicate-runner-v2`.

## Verification and remaining work

All 566 core tests pass independently on net10.0, net8.0 and net471, including 35 new origin cases. Net10 coverage
is 7,916/8,627 lines (91.76%) and 4,708/6,074 branches (77.51%); the new origin class has 100% executable-line coverage.
Normal all-target library build succeeds without warnings/errors. Tests use authored deterministic fixtures,
not live model calls or external benchmark superiority evidence.

The restart test caught a separate origin-loss bug in `CopyWithZeroCost` after cache storage/checkpoint propagation
was already implemented; the unchanged failing assertion passed after fixing that copy path. The suite also covers
migration, compressed trace formats, default schema 6, malformed checkpoint metadata, invalid metering receipts,
cascade failure and refusal to count declared reused evidence as independent samples.

[Persistent evaluation reuse](PERSISTENT_EVALUATION_REUSE.md) now implements separate storage, exact-key validation,
freshness/force-fresh controls and store-call accounting, with an explicit caller-metered engine example.
Consumer-facade integration and representative fair warm/cold campaigns remain; provenance alone does not establish them.
