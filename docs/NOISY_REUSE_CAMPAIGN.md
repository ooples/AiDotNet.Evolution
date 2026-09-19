# Noisy reuse with equivalent prior information

US-23 now has a real-engine controlled ablation in `examples/PersistentEvaluation`.
`AlwaysFresh` and `ExistingSamples` receive byte-identical admissible repertoires and
prior cache records. These are two configurations of this engine, not competitors.
The no-reuse control deliberately takes new measurements. Both use the same proposal
streams within each phase; different phases have explicitly separated random streams.

The fixed development design has 32 seeds, two scalar objectives, four phases
(cold, warm, force-fresh, expired), two controls, then a fully paid replay. Each
group first obtains 16 five-observation measurements, exports eight archive seeds
without fitness, and revalidates them for each warm run. Both warm configurations
receive the same 80-observation acquisition cost attribution. That shared prior is
physically acquired once per task/seed/execution, not charged twice to campaign totals.
Cold runs have no imported seeds or cache records. Copies are hashed before search
can refresh any records, so equivalent prior inputs remain auditable afterward.

Every search has a 64-aggregate dispatch limit, a 320-scalar-observation cap and a
256-proposal cap. A five-observation cache hit costs no new observations, retains
all five original IDs and uncertainty, and is not a new independent replicate.
The engine's run-local memo is disabled. Logical cache/validation calls, proposal
calls, raw reads/writes, prior copying and measured observations are reported
separately; these are not CPU, filesystem-time, money or equal-actual-spend claims.
After selection, each search winner receives **25 separately charged confirmation
observations** on disjoint random streams. Their raw observations, mean and standard
error are retained, and never fed back into search. This is fresh procedural confirmation
of the selected genome, not independent replay or held-out representative task evidence.

```powershell
dotnet build examples/PersistentEvaluation -c Release
dotnet examples/PersistentEvaluation/bin/Release/net10.0/PersistentEvaluation.dll --campaign 32 <full-built-source-revision> <absolute-new-output-directory>
python benchmarks/analysis/analyze_reuse.py <output-directory> --output <new-summary.json>
```

Use `eng/Test-ReuseCampaign.ps1` for the two-seed CI smoke. The complete plan is
written before acquisition, including source/core/worker identities and the one
primary endpoint: warm-control physical observation savings, averaged across the
two tasks within seed. The analyzer preserves independent seed blocks; no cache
lookup or replay increases statistical sample size. It checks raw observation
hashes/mean/standard error, original IDs and clocks, candidate identity, receipt
charges, complete case schedules, imported seeds and initial prior-record hashes.
Replay receives distinct acquisition identities and physically repeats all work;
only quality/decision/accounting fields are compared for deterministic replay.

The earlier pre-confirmation two-seed smoke passed 72 runs including priors and
20,800 physical observations; its raw outputs remain retained as development history.
Warm reuse saved 40 observations per paired run in that smoke, against an 80-observation
prior construction cost. This does not demonstrate net first-run savings versus a
no-prior cold search or general quality superiority. The v3 smoke also passed:
72 runs and **22,400** physical observations, including 800 independently streamed
confirmation observations per execution and fully repeated replay acquisition.
All 22,400 original sample IDs are distinct; reuse does not add samples.
The full source-pinned campaign passed at `3c0a6803d02bd0b6848a02a47137dec6db703ac4`:
**1,152 runs and 358,400 distinct physical observations**, including 161,280 search,
5,120 prior and 12,800 confirmation observations per execution. Replay matched all
compared quality/accounting fields. Warm reuse saved 40 observations per run on every
seed (paired-seed bootstrap interval [40, 40]); the eight imported five-observation
records make this a controlled work-saving check, not evidence of general search
quality improvement or net cold-start savings. An adversarial audit follow-up at
`1f41623e0e44f6abf580d112e20b1bdce9fe486f` rejects hidden charged resources and invalid
confirmation receipts; it reproduces the original campaign summary unchanged.
See [retained evidence and offline verification](../benchmarks/evidence/noisy-reuse/3c0a680/README.md).

These objectives use procedural random noise, not real elapsed-time noise or
representative held-out production tasks. Best observed quality and fresh confirmation
remain descriptive; only the saved-work endpoint has a predeclared interval. Production raw scalar evidence for program fitness
is supplied by the companion AiDotNet `DirectoryProgramSampleEvidenceStore`, which
independently validates observations through the existing fitness-reuse facade.
Metadata and hashes cannot prove honest acquisition, physical independence or
stationarity; correctness and applicability remain separate gates.
