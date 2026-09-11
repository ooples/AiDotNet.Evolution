# Concurrent proposal and evaluation pipeline

`EvolutionDispatchMode.Pipeline` is opt-in. It overlaps proposal backends with evaluator work using independently bounded worker pools and queues. The historical Batch and Continuous defaults and compatibility strings are unchanged when Pipeline is disabled.

## Scheduling contract

The scheduler plans a bounded **feedback wave** before dispatch. All identities, parent/inspiration selections and point-in-time archive snapshots are fixed for that wave. Proposal backends run concurrently only with an explicit capability; canonicalization, refinement, deduplication, archive insertion, selection feedback and operator learning remain on the single writer. Evaluators start as proposal results become available in identity order, without waiting for every proposal callback in the wave.

After all wave work settles, results commit in deterministic evaluation-ID order or explicitly opportunistic completion order. Learning/checkpoint calls never overlap proposal callbacks. This wave boundary deliberately limits staleness and protects stateful operators; it is **not** continuous asynchronous learning and can leave slots idle behind a slow proposal or final wave straggler.

There are three separate memory bounds:

- `WaveSize`: planned identities and immutable contexts, 1–4096.
- `MaxProposalConcurrency + ProposalQueueCapacity`: submitted proposal tasks, workers 1–256 and waiting capacity 1–256.
- `MaxDegreeOfParallelism + EvaluationQueueCapacity`: submitted evaluation attempts, with waiting capacity 1–256.

The planning buffer is not hidden inside the queue metric. One archive snapshot is shared per source island within a wave. Canonical genomes must obey the existing immutable ownership contract; snapshots copy archive membership/metadata, not arbitrary mutable internals of `TGenome`.

Continuous-mode `MaxInFlight` and `MaxInFlightPerIsland` must remain zero; configuring them with Pipeline fails rather than silently ignoring quotas. The pipeline uses its own buffers. Seeds are evaluated before variation can select from an empty archive. Retries use complete ID-ordered logical rounds with bounded submission; worker-sized retry chunks must not change who receives a later attempt under a tight budget.

## Configuration

```csharp
var options = new EvolutionEngineOptions
{
    Dispatch = EvolutionDispatchMode.Pipeline,
    MaxDegreeOfParallelism = 4,
    Pipeline = new EvolutionPipelineOptions
    {
        WaveSize = 16,
        MaxProposalConcurrency = 4,
        ProposalQueueCapacity = 4,
        EvaluationQueueCapacity = 4,
        MinimumProposalStartInterval = TimeSpan.FromMilliseconds(100),
        MinimumEvaluationStartInterval = TimeSpan.Zero,
        MaximumScheduleRecords = 4096
    }
};
```

Intervals throttle stage dispatch permits; they are cooperative scheduling limits, not hard real-time deadlines or an external provider quota service. Zero disables throttling; intervals are bounded at one minute. Cancellation interrupts waiting permits and queues. Running callbacks must cooperate with cancellation; use consumer-owned process isolation for untrusted or noncooperative workloads.

Ordinary `IVariationOperator<TGenome>` instances remain serialized regardless of the requested proposal-worker count. Implement `IDeterministicConcurrentVariationOperator<TGenome>` and return true only if overlapping calls use their own random streams/immutable inputs and do not mutate shared learning state or depend on arrival order. Merely being stateless-looking or lacking a checkpoint interface is not evidence of safe concurrency.

Pipeline contexts expose `ProposalIdentity` and `EvaluationId`. The v2 fingerprint includes run/configuration, allocated identity, parent/inspiration semantic evaluations, consumed artifacts and the complete semantic archive snapshot (including measured quality, descriptors, diagnostics and artifacts), generation and island. Elapsed timing is excluded. Archive version and genome IDs alone cannot detect changed recorded parent evidence. Legacy caller-created contexts keep those optional properties null. External responses must be indexed by stable request identity and retained if exact replay is required. The concurrency capability is pinned at construction and included in pipeline compatibility; changing it before dispatch fails.

## Shared resource reservations

Top-level `ResourceMeteredVariationOperator<TGenome>` and `ResourceMeteredEvolutionTask<TGenome>` participate automatically in coordinated admission. Use the same run-scoped ledger to constrain their combined resources. A costed source may opt into concurrency through `IDeterministicConcurrentCostedEvolutionProposalSource<TGenome>`; ordinary direct adapter calls remain serialized.

Before any wave callback starts, reservations are made in logical identity order for proposal work and first evaluation attempts/stages. Before each retry round, every selected attempt/stage is reserved before callbacks start. A callback refund therefore cannot change admission simply because one worker ran faster. Backend actual receipts still determine charges; missing receipts retain conservative maxima and maximum violations cannot produce successful candidates.

This is deliberately conservative. Pre-reserving later work can leave budget slack when an earlier proposal fails or a cascade screens it out. At the settled phase boundary, **undispatched** reservations are released with known zero consumption; dispatched work without a receipt is charged its maximum as unknown. No later stage is billed merely for being planned. Phases are bounded to 65,536 resource slots; oversized wave/stage combinations fail before dispatch.

Canonicalization, refinement, snapshot/queue/checkpoint overhead and other work outside those adapters are not automatically metered. Include them in explicit caller-owned accounting without double-charging. The backend must enforce its declared resource/isolation maxima; a ledger records an overrun but cannot physically stop arbitrary external code. The ledger must not have competing external writers during coordinated phases.

Automatic phase coordination applies to these **top-level adapters**. A custom or nested wrapper hiding its own ledger does not acquire deterministic resource admission by implication. Such integrations need their own coordinated protocol and tests. Logical outcomes and operation-ID-sorted receipts can reproduce across worker counts; chronological receipt order and elapsed-time diagnostics need not be byte-identical.

## Checkpoints, cancellation and audit

Engine checkpoints are captured only at settled wave boundaries. Resume recreates the same algorithmic state at a compatible boundary; changing the wave/scheduling semantics refuses incompatible resume. Restore the matching resource ledger separately when using metered adapters. Increasing a limit after a partial wave intentionally changes a feedback boundary; it is not an equivalence claim to a differently partitioned uninterrupted run.

Cancellation drains owned proposal/evaluator tasks before disposing gates and rolling back uncommitted engine state. Actual resource charges are never rolled back. An old engine checkpoint is **not permission to repeat already-dispatched external work**: reconcile subsequent ledger/backend work before live resumption. Durable in-flight leases and arbitrary crash recovery belong to US-21.

`engine.PipelineReport` returns an immutable diagnostic snapshot containing run/compatibility identity, the invocation's first allocation cursor, configured buffer limits, effective worker counts, queue/active peaks, stage-entry counts, summed callback durations, completed/aborted waves and bounded logical snapshot/commit/abort records. Stage-entry counts include adapter budget denials and are not necessarily physical model/evaluator calls. Busy seconds are not CPU measurements or financial receipts. Timings never drive deterministic learning.

`MaximumScheduleRecords` bounds retention. `DroppedScheduleRecords > 0` makes `IsScheduleComplete` false; truncated evidence must not be presented as a complete replay. Records describe this invocation, so a resumed invocation does not magically contain a previous process's log. Even a complete logical schedule still requires original external responses and matching component versions to replay. Opportunistic mode records its actual different commit semantics, not timing-independent reproducibility.

## Verification scope

The pipeline tests exercise controlled generator/evaluator overlap, serialized learning, immutable held snapshots, timing/worker-count replay, bounded retries under tight budgets, small evaluation limits, cancellation/drain, settled-wave checkpoint resume, rate gates, opportunistic commit logging, record truncation, shared-resource admission and known-zero versus unknown cleanup.

These contracts are necessary but are not a throughput, quality or competitor-superiority claim. A source-pinned repeated benchmark with retained physical work, declared costs, scheduling overhead, replay evidence and unfavorable outcomes is still required before US-20 delivery is ready for review. The story remains in progress.

## Authored performance and replay protocol

`examples/ProposalPipeline` executes real bounded Sphere and Rugged numeric objectives, with ZeroLatency, ProposalBound and Mixed artificial callback-delay profiles. Each task/profile/seed compares Batch, Continuous, PipelineSerial and PipelineConcurrent with the same eight starting points, 32-proposal/32-evaluation/40-work-unit caps and four evaluator slots. Pipeline waves and Batch batches have size eight; the continuous window is eight. Actual evaluation calls and charges remain visible if duplicates reduce spending. Continuous has different feedback timing, so identical quality is not assumed.

The development campaign is declared as eight search seeds and three timing repetitions per seed: 576 live cases, each followed by JSON-round-tripped recorded-response replay that executes zero physical backend calls. Four separately retained warmups precede the campaign. Rotating method order reduces fixed order bias but cannot control this shared host. The runner records exact example/core assembly versions and SHA-256, runtime/OS/process architecture, elapsed time, process CPU time and managed allocations around engine execution. These process-wide counters include runtime/background activity; CPU granularity can report zero for short runs. Setup, evidence serialization and offline replay are not included in the engine timing window; raw receipts disclose separate setup work and simulated replay accounting.

`benchmarks/analysis/analyze_pipeline.py` first pairs methods within task/seed/repetition, then averages fixed tasks and timing repeats inside each seed. It resamples those eight seed clusters, not the 576 rows, with 10,000 bootstrap draws and Bonferroni adjustment for 18 profile/comparator/quality-or-timing endpoints. Failed cases receive zero quality; incomplete timing pairs suppress the affected timing comparison. Missing/duplicate scheduled identities or mismatched cohorts fail validation. Median/p95 completed-only timing and allocation summaries remain descriptive. This is retrospective development evidence, not a confirmatory experiment or a default-promotion decision.

```powershell
dotnet build examples/ProposalPipeline -c Release
# Build from the recorded clean commit; do not label dirty output as pinned evidence.
dotnet run --project examples/ProposalPipeline -c Release --no-build -- 8 3 new-campaign.json
python benchmarks/analysis/analyze_pipeline.py --input new-campaign.json --source <full-built-commit> --output-dir new-evidence-directory
python benchmarks/analysis/analyze_pipeline.py --verify-evidence new-evidence-directory
```

The evidence directory contains every live/replay response, receipt, schedule and failure in `raw.json.gz`, plus a recomputable `analysis.json` and both raw/compressed hashes. Hashes detect inconsistency, not malicious replacement by a trusted artifact writer. CI runs real C# output through the analyzer, unit-tests failure/corruption handling, and rejects three tampered response tapes. An empty/failed archive is retained with zero best quality rather than causing evidence serialization to drop the failed run.

For cross-build default-mode overhead, the existing `engine-profile-v1` suite exercises variation after eight seed evaluations. On multi-group Windows machines, set `AIDOTNET_PROFILE_PROCESSOR_GROUP` explicitly (a bounded integer 0–63; the OS must support the requested group). Identical masks such as `F` do not identify the same CPUs in different groups. `compare_pipeline_defaults.py` rejects mismatched processor groups, runtime controls, missing/failed cases and changed logical work/state/quality. Three repeated fresh-process measurements per case remain descriptive when source groups run sequentially on a shared host.
