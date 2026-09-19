# US-21 process-recovery evidence

Runtime/harness source: `75fd8be9cdb827a158ab2a678bd4eafdb4abae41`.
Both reports embed that exact core informational-version revision and executed-binary SHA-256 hashes.

[recovery.zip](recovery.zip) retains **10 files**: a report and the two final authoritative
`work.current`/`work.owner` pairs for each managed and NativeAOT run. Size **6,010 bytes**;
SHA-256 `2224c822fc03545073c4a3fd10e0240f262d396a74541bce53e3947e354969e6`.
All four current-state envelopes were independently checksum-checked and their logical
result/resource totals recomputed from the stored ledger, rather than accepted solely from
the report's `verified` flag.

The authored harness actually terminated three child processes per run:

1. Coordinator after dispatch and reservation publication, while holding its owner lock.
2. Coordinator after result/receipt publication, before graceful disposal.
3. Worker after evaluating but before reporting its final receipt; a new incarnation then retried.

Each run observed **three actual square-function evaluations** through per-process counters.
Lease expiry uses an explicitly advanced injected UTC clock; process termination is real.
This is correctness/recovery evidence, not a throughput, search-quality or competitor benchmark.

| Recovered case | Settled authored `cost_units` | Still-reserved liability | Verified outcome |
| --- | ---: | ---: | --- |
| Coordinator dispatch/result crashes | 2 | 0 | Original ticket/result survives; replayed receipt does not charge again. |
| Lost worker and replacement | 2 | 5 | Replacement result survives; the missing original receipt does not become free budget. |

Both runs used Windows build 26200, .NET 10.0.12, one declared .NET processor,
workstation GC and a 768 MiB managed heap cap. Native execution SHA-256:
`d575777a3b0270e140e25ec88ccc77ab226e05edc2a23fe2533a54ee364ae02d`.
Managed harness DLL SHA-256:
`8356eef0715e5213d00a4b93d7cfab10c11f264adc73972af87d616cd643eb88`;
managed core DLL SHA-256:
`2f353a3a5a835d1e0c73dba34bb225439d544eabbecb13421a27dccd5c25c6fe`.
The standalone NativeAOT build emitted no warnings; this does not clear the separate
general native host's other engine-persistence trimming warnings.

The same implementation passed 948 net10.0 / 948 net8.0 / 750 net471 tests, with zero skips;
coverage was 92.85% line / 79.73% branch against the unchanged ratchet. Full solution format
verification and three-target package content/dependency validation passed. Package DLL
bytes were compared with the rebuilt target DLLs. No packages were published remotely.

Reproduce from the pinned source:

```powershell
dotnet build examples/DurableWork -c Release -m:1 -p:UseSharedCompilation=false
dotnet examples/DurableWork/bin/Release/net10.0/DurableWork.dll verify TestResults/durable-managed
dotnet publish examples/DurableWork -c Release -r win-x64 -m:1 -p:UseSharedCompilation=false
./examples/DurableWork/bin/Release/net10.0/win-x64/publish/DurableWork.exe verify TestResults/durable-native
```

The archive reports retain original local snapshot-directory paths for provenance; the
raw snapshots themselves are included under portable `managed/` and `native/` prefixes.
Checksums are not signatures or proof against a hostile operator. Filesystem power loss,
backup rollback, network filesystems, Linux/macOS and deployed remote services were not
verified here. Earlier unpinned managed/native smoke runs are retained locally under
`TestResults` but are not relabeled as these pinned runs.

`SupportsExactSearchContinuation` remains false, and both reports explicitly say delivery-only
recovery requires a fork for partial-batch search continuation. Durable engine and binding
integration remain unfinished: this evidence does **not** close US-21 or the full roadmap.
