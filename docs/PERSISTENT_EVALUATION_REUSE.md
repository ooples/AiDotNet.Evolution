# Persistent evaluation reuse

US-23 now has an opt-in persistent measurement store, exact-key validation, sample-aware freshness policy and
metered lookup/write coordination. It is separate from [seed repertoires](WARM_START_REPERTOIRES.md), checkpoint
resume and the engine's run-local memo. It does not establish a competitive win or complete the full user story.

## User stories and contracts

As an experiment owner, I want only applicable evidence reused, so changed code, data or hardware cannot silently
inherit an old score.

Given an `EvolutionReuseScope`, a canonical candidate and current task/codec, when
`EvolutionEvaluationCacheKey.CreateAsync` runs, then it checks current identities, current canonicalization,
exact codec bytes and a bounded round trip without evaluating the candidate. The lookup key includes all twelve
scope facets, canonical identity, exact UTF8 payload hash and an explicit measurement/replication-policy version.
Changed applicability, payload or measurement policy produces a different key. Custom codec/canonicalizer work
requires caller-owned accounting and isolation; the accepted payload is limited to 64 KiB, not its execution cost.

As an experiment owner, I want original measurements retained, so a cache hit cannot manufacture statistical power.

Given fresh, feasible completed evidence with matching [measurement origin](MEASUREMENT_ORIGIN.md), when an
`EvolutionEvaluationCacheRecord` is created, then it retains original sample IDs/count, statistics, time, original
cost, scalar/direction/descriptors/objectives/metrics and a digest of separately retained raw evidence. It excludes
diagnostic/artifact bodies and genome contents. `AsReused` marks `PersistentReuse`, retaining original metadata and
charging only explicitly supplied current work. Failed, infeasible, origin-less, already reused or wrong-scope
results cannot become new stored evidence. The checksum proves integrity, not authenticity or correct measurement.

As a benchmark owner, I want explicit freshness and confirmation controls, so old timing data does not become
fresh confirmation evidence.

Given an explicit `EvolutionEvaluationReusePolicy`, when `Check` receives the requested key, record and caller's
clock, then it returns a typed decision for missing/mismatched evidence, future observation time, expiration,
insufficient original samples or absent/excessive standard error. Age is measured from the original observation,
not cache insertion time. Deterministic reuse is an explicit caller declaration; `ExistingSamples` additionally
requires declared uncertainty and never makes old observations independent. Maximum age and optional uncertainty
thresholds are policy inputs, not inferred guarantees of stationarity. `forceFresh` or `Disabled` skips lookup.

As an operator, I want bounded, auditable persistence, so storage failure cannot become a fabricated cache hit.

Given an absolute private directory, when `DirectoryEvolutionEvaluationStore` writes evidence, then it uses
bounded strict checksummed JSON, a short unique sibling temporary file, flush and atomic move/replacement.
Reopening reads the persisted record. Within this process, a shared lexical-root gate enforces capacity and
newer-observation replacement. Identical, older/equal-time and capacity-refused writes return false. Conflicting
evidence for the same original sample set throws and preserves the existing record. Malformed, oversized,
wrong-key or invalid-UTF8 files fail closed and are not silently overwritten. No automatic eviction is performed.

As an experiment owner, I want honest total work accounting, so cache savings do not hide setup or storage costs.

Given `EvolutionPersistentEvaluationCache` and a shared ledger, when a store call is requested, then it reserves
before dispatch and charges exactly one `cache_store_invocations` unit for each dispatched store method, including
failures, cancellation and fatal exceptions. A denied reservation dispatches nothing. Operation identities cannot
be charged twice, including after explicit ledger restore. Known recoverable store failures return typed failure
decisions; cancellation and fatal exceptions propagate. A failed write never relabels a fresh evaluation as a hit.

`cache_store_invocations` is a logical method-call count, **not physical I/O, CPU, bytes, elapsed time or money**.
The coordinator does not re-bill original `cost_units`. Separately meter validation, actual evaluation, model calls,
raw-evidence checks and any consumer-specific physical resources. Restore the ledger and engine at the same boundary.
No model call, provider configuration, paid API, OS isolation or implicit external service is part of this feature.

## Runnable engine integration

Supply an absolute output directory that does not exist:

```powershell
dotnet run --project examples/PersistentEvaluation -c Release -- C:\experiments\new-persistent-evaluation-run
```

The example freezes a plan before work, retains raw authored evidence, reopens the store for each phase and runs
the real engine over the same eight deterministic candidates. It checks all three ledgers and emits complete
origin-bearing traces plus `report.json`:

| Phase | Actual evaluator calls | Store invocations | Key-validation calls | Sample origin |
| --- | ---: | ---: | ---: | --- |
| Cold | 8 | 16 | 8 | New observations |
| Warm | 0 | 8 | 8 | Same original IDs |
| Force-fresh | 8 | 8 | 8 | Different new IDs |

Each phase still has eight engine evaluation attempts. **An engine attempt is not necessarily a new measurement.**
The cold acquisition cost remains part of warm-start accounting; the demonstration does not claim that free prior
information is available or that these method counts establish a real wall-clock advantage. All phases select the
same authored quadratic optimum. This is a cache integration check, not an algorithm-discovery campaign.

Disable `EvolutionEngineOptions.EnableEvaluationCache` when the task must check freshness/force-fresh on every
dispatched evaluation: an outer run-local memo returns before task invocation and cannot be overridden inside the
task. Duplicate proposal rejection is still an engine search policy, not a fresh replication mechanism. Use the
fresh replicate runner for independently identified confirmation samples.

The lookup result exposes `EvidenceSha256` for separately metered consumer validation. The store does not fetch,
authenticate or validate the external raw artifact, nor rerun a correctness oracle. Consumers remain responsible
for updating all scope facets and satisfying their correctness/promotion policy before reuse is admissible.

## Boundaries and verification

The directory must be privately controlled. Filesystem aliases, malicious modification, external writers,
cross-process capacity/newest-writer ordering and cross-process revocation require separate coordination; atomic
file replacement alone does not provide them. Cancellation cannot preempt an arbitrary custom store/codec. A
changing clock or store contents is external state, not a deterministic checkpoint replay guarantee. Raw evidence,
labels and metric names must not contain secrets; hashes are not redaction of guessable data or authentication.

All 594 core tests pass on net10.0/net8.0/net471, including 28 persistent-storage/key/policy/accounting cases.
Net10 coverage is 8,172/8,884 lines (91.99%) and 4,866/6,242 branches (77.96%); the four new source files cover
255/257 executable lines. Production/test-copy hashes match on all targets; whitespace verification passes.
The final example passes all cold/warm/force-fresh assertions. CI now runs it and retains JSON/trace artifacts.
Hosted current-head validation and independent review remain separate from these local results.

Remaining US-23 work includes public consumer-facade/CLI integration and representative, predeclared warm/cold
campaigns with equivalent admissible priors, acquisition cost, stochastic freshness checks and independent final
confirmation. Seed repertoires plus this deterministic example are not a substitute for that evidence.

The [noisy equivalent-prior campaign](NOISY_REUSE_CAMPAIGN.md) now implements the
core ablation runner, raw sample/receipt audit, separated acquisition/reuse costs,
force-fresh/expiration checks and fresh-stream winner confirmation. The companion
AiDotNet provider adds production raw scalar evidence validation to the existing
fitness facade. The pinned campaign now passes 1,152 runs and 358,400 distinct acquired
observations; current core tests pass 661/661 on all three targets. The companion
[AiDotNet PR #2182](https://github.com/ooples/AiDotNet/pull/2182) passes 963 integration
tests each on .NET 10/.NET 8 and 22 provider tests against the actual legacy assembly.
The [evidence](../benchmarks/evidence/noisy-reuse/3c0a680/README.md) includes fresh confirmation,
equivalent priors and hardened receipt auditing. CLI wiring, representative cross-system
comparisons and published-package integration remain separate roadmap/dependency work;
neither these authored tasks nor deterministic replay establish competitor superiority.
