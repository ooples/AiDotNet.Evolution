# Running, stopping and resuming a search

This guide covers what happens around a search: how an `EvolutionEngine<TGenome>` run is configured,
why it stops, how to read what it did, and how to save and resume it. The search itself (archives,
selection and islands) is covered in [Selection, islands and archives](SELECTION_ISLANDS_AND_ARCHIVES.md).

## Configuring a run

`EvolutionEngineOptions` holds every run-level setting. The ones that decide how results are handled
are:

- **`ExecutionMode`** (`EvolutionExecutionMode`). `Deterministic` commits finished evaluations in
  evaluation-id order, so a seed replays the run whatever order the workers finish in.
  `Opportunistic` commits results as they arrive. It is faster when evaluation times vary, but it is
  not reproducible.
- **`FailurePolicy`** (`EvolutionFailurePolicy`). `Continue` records a failed candidate and carries
  on. `FailFast` stops the run at the first failure, with `EvolutionStopReason.CandidateFailure`.
- **`MaxRetries`** and **`RetryOn`** (`EvolutionRetryStatuses`, a flags enum over `Failed`,
  `TimedOut` and `Canceled`) decide which terminal statuses get another attempt. The default is
  `All`, and `None` makes every failure final on its first attempt.
- **`IslandAssignment`** (`EvolutionIslandAssignmentStrategy`), **`MigrationTopology`** and
  **`MigrationTrigger`** configure islands. See the selection guide.

Archive geometry is declared with `EvolutionDescriptorDefinition`. Its `OutOfRangePolicy`
(`EvolutionOutOfRangePolicy`) decides what happens to a value outside the declared bounds: `Reject`
drops the candidate, `Clamp` puts it in the edge bin, `OverflowBins` reserves explicit bins below
and above the range, and `Grow` extends the range in whole bins and re-keys existing elites. An
archive that grows implements `IGrowableEvolutionArchive<TGenome>` (`MapElitesArchive<TGenome>`
does), so its widened ranges survive a checkpoint.

When you do not know sensible bounds in advance, `EvolutionDescriptorCalibration` derives the whole
grid from what a seed population measured. For a single descriptor, `EvolutionDescriptorCalibrator`
collects observed values in any order with `Observe` and turns them into a fixed definition with
`Freeze`. `EvolutionDescriptorCalibrationOptions` sets the bin
count, the padding added around the observed range, the span used when every seed measured the same
value, and the out-of-range policy of the resulting definitions.

Every public collection and hash input is bounded by the constants in `EvolutionCollectionLimits`,
for example the most islands, archive dimensions, cascade stages and trace records a run may hold.
A request above a limit is refused up front instead of exhausting memory mid-run.

For typed parameter spaces, `EvolutionSearchPreset` picks the variation strategy explicitly:
`Mutation`, `UniformMixed`, `AdaptiveMixed` or `DiagonalCma`. A research preset never replaces the
simple default behind your back. See [Typed search spaces](TYPED_SEARCH_SPACES.md).

### Optional stages

- `ICascadeEvolutionTask<TGenome>` adds ordered, increasingly expensive evaluation stages to a task.
  `EvolutionEngineOptions.Cascade` (`EvolutionCascadeOptions`) enables them and sets each stage's
  threshold and timeout, and whether stages that reject a candidate count against the budget.
- `ICandidateRefiner<TGenome>` gets a chance to improve each proposal before it is evaluated. It
  receives an `EvolutionRefinementContext`, which carries the evaluation id and a `StableRandom`
  derived for that evaluation.
- `IEvolutionLatencyProfile` lets a task or operator declare that it waits on external latency (a
  model call, a remote build) rather than on CPU. When either one does, the engine's default
  dispatch becomes the proposal pipeline instead of batches, so proposals overlap evaluations.

## Why a run stopped

`EvolutionRunResult<TGenome>.StopReason` (`EvolutionStopReason`) says which limit ended the run:
`EvaluationBudgetReached`, `ProposalBudgetReached`, `GenerationLimitReached`, `TimeLimitReached`,
`TargetReached`, `EarlyStopped`, `NoCandidates`, `CandidateFailure` or `Canceled`.

`EvolutionRunResult<TGenome>.Counters` (`EvolutionRunCounters`) counts proposals, evaluation
attempts, completed evaluations, abandoned evaluations and every terminal status. Use it to check
that a budget was actually spent on evaluations rather than on rejected or failed proposals.

### Early stopping

`EvolutionEngineOptions.EarlyStopping` (`EvolutionEarlyStoppingOptions`) stops a run that has
stopped improving: after `PatienceEvaluations` evaluations without an improvement of at least
`MinimumImprovement` in the chosen metric. Every reading of the
criterion ends in an `EvolutionEarlyStoppingOutcome`: `Improved`, `NotImproved` or `Unmeasurable`.
A reading is unmeasurable when there is nothing to compare, and
`EvolutionEarlyStoppingUnmeasurableReason` says why: `MetricNotReported`, `EmptyFeasibleFront`,
`EmptyArchive` or `NoArchiveCells`.

`EvolutionRunResult<TGenome>.EarlyStopping` (`EvolutionEarlyStoppingReport`) totals the readings
by outcome. Check `UnmeasurableReadings` before trusting an `EarlyStopped` result: a criterion that
could never measure anything did not observe a plateau.

## What the run produced

### Choosing the champion when quality is noisy

`result.Best` is the entry with the highest measured quality. When quality comes from a small sample, that is often
the program that fit the sample's noise: in E-4's pilot, on 80 selection rows, the measured best was worse than the
seed on 400 held-out rows in 3 of 6 cells. `EvolutionChampionSelection.SelectSignificant` picks the best entry whose
gain over a reference (usually the seed) exceeds `k` combined standard errors, reading each entry's standard error
from an evaluator metric, and returns `null` when none does, so the reference stands. The CLI applies it when
`search.championStandardErrors` and `search.standardErrorMetric` are set.

Besides the island archives, the result exposes:

- `GlobalElites`: when `EvolutionEngineOptions.GlobalEliteCount` is positive, the engine maintains an
  `EvolutionGlobalEliteIndex<TGenome>`, a bounded top-K leaderboard across islands whose entries are
  `EvolutionEliteRecord<TGenome>` (the island and the archive entry).
- `IslandStatuses`: an `EvolutionIslandStatus` per island, with occupancy, coverage and quality.
- `InfeasibleExploration`: `EvolutionInfeasibleEntry<TGenome>` candidates kept for exploration.
  They are never reported as elites or deployed.
- `StateHash`: a deterministic hash of the final state. Equal hashes mean equal runs.

`StableRandomState` is the serializable state of a `StableRandom`. Checkpoints use it to resume
every random stream exactly where it stopped.

## Artifacts

An evaluation can return artifacts, such as a program's captured output. Evaluator text is
untrusted: `EvolutionArtifactSanitizer.Sanitize` strips terminal control sequences and
credential-shaped substrings, and `WouldRedact` reports whether it would change anything. Content
too large (or not text) to keep inline goes to an `IEvolutionArtifactStore`, set through
`EvolutionArtifactOptions.Store`. `DirectoryEvolutionArtifactStore` is the shipped store: it keeps
content in one directory addressed by its hash, with optional retention that `Prune` enforces.

## Saving and resuming

Give the engine an `IEvolutionCheckpointStore` and set `EvolutionEngineOptions.CheckpointInterval`.
Two stores ship:

- `JsonEvolutionCheckpointStore` keeps one atomic JSON file per run, plus the previous valid
  snapshot, so a crash mid-write never loses the last good state.
- `DirectoryEvolutionCheckpointStore` keeps numbered, checksummed snapshots in one directory.
  `EvolutionCheckpointRetentionOptions` decides how many to keep: `KeepLast` recent snapshots plus
  `KeepBest` of the highest quality. `ListCheckpoints` returns an `EvolutionCheckpointDescriptor` per
  file, newest first, including files that failed to load, with each one's sequence, size, validity
  and best quality.

A store can also implement `IEvolutionCheckpointSegmentStore`, which keeps append-only segments beside
its checkpoints: `WriteSegmentAsync` writes one atomically and `OpenSegmentAsync` reads it back.
A checkpoint lists the segments it needs in `EvolutionCheckpoint.SegmentIds` (`WithSegmentIds` sets
them), and the store must keep every segment a retained checkpoint names. `DirectoryEvolutionCheckpointStore`
stores them under its `segments` subdirectory and deletes those no retained snapshot names;
`InMemoryEvolutionCheckpointStore` keeps those of its latest checkpoint.

`EvolutionEngineOptions.CheckpointFormat` decides whether the engine uses them. The deduplication set, the
evaluation cache and the island archives grow as a run goes on, so an inline checkpoint rewrites all of them on
every save: at 10,000 elites of 4 KB programs that was a 46 MB file per save. Segments are an append-only journal
of that state instead. In [the E-1 measurement](../benchmarks/evidence/checkpoint/e1/README.md), save p95 at 10,000
elites went from 2,339 ms to 99 ms and resume from 4.1 s to 1.7 s.

- **Each save appends one delta segment** with only what changed since the previous save: genomes added to or
  forgotten from the deduplication set, cache entries set or removed, and, per island, the archive cells that
  gained or replaced an elite and the cells that were emptied. An unchanged elite is never written again.
- **Every `CheckpointCompactionInterval` segments (32 by default) the next save writes a base segment** that
  restates the whole state, and the checkpoint stops naming the older chain. The interval bounds how many deltas a
  resume replays; a smaller one makes resume read less and more saves pay for a full base.
- **The checkpoint payload names its chain**: each segment's identifier, whether it is the base, and its SHA-256.
  The payload keeps everything else (counters, random state, global elites, island histories, each island's
  version and descriptor grid). It records where the archives are, so a resume knows what to rebuild.
- **Resume reads the payload, then the base, then each delta in order**, checking every segment against its
  SHA-256 and applying it strictly: a delta that adds a genome twice, removes one that is absent or empties a cell
  that holds nothing is refused as corrupt. The rebuilt state lists each island's elites in cell order, exactly as
  an inline payload does, and then goes through the same validation, so a segmented resume reaches the same state
  hash as an inline one.

A crash cannot leave a half-written record that a resume reads. A store writes each segment atomically
(`DirectoryEvolutionCheckpointStore` writes a temporary file, flushes it and renames it into place), and a segment
counts only once a saved checkpoint names it with its digest. A process killed after a segment reached the store
but before its checkpoint was saved leaves an orphan that nothing names: resume uses the previous checkpoint, never
reads the orphan, and the next save reuses its identifier and replaces it. A named segment that is missing or
altered fails its SHA-256 check and the resume is refused rather than reinterpreted.

With `EvolutionCheckpointFormat.Auto`, the default, checkpoints stay inline, self-contained and readable by older
engines while they are small: until the deduplication set and cache together reach `CheckpointSegmentThreshold`
entries (50,000 by default), or an inline payload reaches about a megabyte. After that the run uses segments for
the rest of its life. `Inline` always writes self-contained checkpoints; `Segmented` always uses segments and
requires a segment store. A Pareto archive's front is bounded and stays in the payload in either format. A segmented
checkpoint needs its store: copy a `DirectoryEvolutionCheckpointStore` directory whole to move the run, or choose
`Inline` when checkpoints must travel on their own. Neither the format nor the compaction interval is part of the
compatibility hash, so a run can resume under a different one.

**Formats and versions.** The payload's schema version says which layout it uses. Inline payloads are versions 6
to 9. A segmented payload whose segments hold only the deduplication set and cache adds 4 (10 to 13), the layout
engines wrote before the archive journal; its segments are segment schema 1. A payload whose archives are in the segments as well
adds 8 (14 to 17), with segment schema 2. All three still load. A run resumed from a version 10 to 13 checkpoint
writes a new base at its next save and continues in the current layout. Engines that predate the archive journal
refuse versions 14 to 17 as "written by a different engine version" instead of resuming with empty archives, so a
run that has moved to the current layout cannot be resumed by an older engine. Keep that run with an engine at
least as new, or write it with `Inline`.

For a long run on .NET 6 or later, also let the garbage collector return memory it no longer needs:
set `DOTNET_GCConserveMemory=7` in the environment, or add it to the application's runtime configuration:

```json
{ "configProperties": { "System.GC.ConserveMemory": 7 } }
```

A run's live memory stays bounded once its deduplication set is bounded, but by default the collector keeps
freed memory committed, so the process's working set can sit well above what the run holds. The soak
evidence in `benchmarks/evidence/soak` is measured both ways.

Both stores have `ForOutputDirectory(outputDirectory, runId, ...)`, which places files through
`EvolutionOutputLayout`. The layout derives every path a run writes (the checkpoints and traces
folders, the checkpoint path and trace paths) from one output directory and run id, so the CLI and
library runs agree on where things are.

To resume, set `EvolutionEngineOptions.Resume` and run a new engine with the same options and store.
The archives must implement `ICheckpointableEvolutionArchive<TGenome>` (`MapElitesArchive<TGenome>`
does, through `IGrowableEvolutionArchive<TGenome>`). A Pareto archive implements `ICheckpointableParetoArchive<TGenome>`, which restores its
deployable and exploratory populations separately. A checkpoint that does not match the engine's
compatibility hash is refused with `InvalidDataException` rather than silently forking the run.

To inspect a checkpoint without resuming it, call
`EvolutionEngine<TGenome>.ReadCheckpoint(checkpoint, genomeCodec)`, or, for a segmented checkpoint,
`ReadCheckpointAsync(checkpoint, genomeCodec, store)` with the store that wrote it, which replays its segments
first. `ReadCheckpoint` refuses a checkpoint whose archives are in segments rather than report it as empty. Each recovered candidate is an
`EvolutionCheckpointEntry<TGenome>` giving the island, the archive entry, and where it was held
(`EvolutionCheckpointEntrySource`: the island archive, the global elite index, the island history
or the infeasible-exploration pool).

## Traces

`EvolutionTraceObserver<TGenome>` streams one `EvolutionTraceRecord` per evaluation to a
crash-safe trace file, in the `EvolutionTraceFormat` you choose (`JsonLines` or a single `Json`
document). An `EvolutionTraceSummary` is kept beside it, with the records written and dropped and the
bytes written. `EvolutionTraceFile.Read` (or `ReadAsync`) returns an `EvolutionTraceReadResult`: the
records, whether the file was complete, its format and compression, and the summary if one was
written. A trace cut off by a crash still reads, and its `IsComplete` is false, so you never mistake
a partial trace for a whole run.
