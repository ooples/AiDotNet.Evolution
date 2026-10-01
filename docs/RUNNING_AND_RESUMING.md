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

`EvolutionEngineOptions.CheckpointFormat` decides whether the engine uses them. The deduplication set and
evaluation cache grow with every distinct genome a run evaluates, so an inline checkpoint repeats all of
them on every save. With `EvolutionCheckpointFormat.Auto`, the default, checkpoints stay inline, self-contained
and readable by older engines until the two together reach `CheckpointSegmentThreshold` entries (50,000 by
default). After that each save writes one segment with only what changed, and every 32 segments a base
segment restates both. `Inline` always writes self-contained checkpoints; `Segmented` always uses segments and
requires a segment store. A segmented checkpoint needs its store: copy a `DirectoryEvolutionCheckpointStore`
directory whole to move the run, or choose `Inline` when checkpoints must travel on their own. On resume the
engine checks each segment's SHA-256, and the format is not part of the compatibility hash, so a run can resume
under a different one.

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
`EvolutionEngine<TGenome>.ReadCheckpoint(checkpoint, genomeCodec)`. Each recovered candidate is an
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
