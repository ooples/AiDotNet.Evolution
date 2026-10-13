# V1-25 (#119): reference-worker overhead per evaluation

Measured by `benchmarks/worker-overhead/measure_python.py` and `measure_typescript.mjs` on a Windows 11 development
machine against the managed host (`dotnet aidotnet-evolution-host.dll`, Release, net10.0) at main `a8a20f0`. The
hosted-runner runs (win-x64, linux-x64, osx-arm64, native AOT host) are uploaded by the `TypeScript binding`
workflow as `worker-overhead-<rid>` artifacts on every run that touches the bindings.

## Method

950 leases after 50 warm-up, each served by one `serve(..., max_leases=1)` call, the unmodified deployment path.
Overhead is that call's wall time minus the evaluator's own time (measured inside the evaluator), so it is the claim
round trip, envelope unwrapping, receipt encoding and the commit round trip. A read-only `status` request after each
lease measures the bare IPC round trip, which has no durable write.

## Results (ms)

| worker | overhead p50 | overhead p95 | p95, first tenth | p95, last tenth | IPC round trip p95 |
|---|---:|---:|---:|---:|---:|
| Python | 49.17 | **81.55** | 32.71 | 108.66 | 1.018 |
| TypeScript | 44.12 | **72.05** | 32.93 | 80.11 | 0.460 |

**Target p95 < 2 ms: not met.** The worker side is not where the time goes: a whole request/reply through either
client costs about 0.5-1 ms. Each lease makes two durable mutations (claim, commit), and on every mutation
`DurableEvolutionWorkCoordinator` deserializes the whole retained state, re-serializes it and
`EvolutionWorkJournal.Commit` writes it to a temporary file with `WriteThrough` plus `Flush(true)` and then
`File.Replace`s it. That is a full fsync'd state rewrite per claim and per commit, about 10-12 ms each here with a few hundred items retained, growing with
retained work (first tenth vs last tenth above).

Reaching < 2 ms needs a durability design change, not a worker change: an append-only journal (so cost stops growing
with retained work) and amortized fsyncs (batched claim/commit of several leases per request, or group commit), which
changes what the protocol acknowledges as durable. That decision is the maintainer's, so the harness reports the
number and does not gate on it.
