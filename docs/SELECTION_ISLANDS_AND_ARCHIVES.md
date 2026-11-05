# Selection, islands and archives

This guide covers how a search picks parents, how islands divide and share the population, and the
archive interfaces an implementation can support. For run configuration, stopping and checkpoints,
see [Running, stopping and resuming a search](RUNNING_AND_RESUMING.md).

## Reading an archive

Every archive exposes an `IEvolutionArchiveView<TGenome>`: its descriptor definitions and their
hash, the optimisation direction, the number of occupied cells, a version that increases on every
change, the entries, the best entry, and `Get(cell)` for one `EvolutionCellKey`. Observers, reports
and selection policies read archives only through this view. `EvolutionArchiveSnapshot<TGenome>`
copies a view at one moment, so it can be handed outside the engine while the run carries on.

`EvolutionArchiveQuery` adds queries by a named metric rather than by the single quality score:
`BestBy`, `TopBy`, `WithMetric` and `MetricNames`, on an archive or on a whole run's result.

A multi-objective archive also implements `IEvolutionParetoArchiveView<TGenome>`. Its
`ParetoDefinition` is `null` for a scalar snapshot, and `InfeasibleEntries` lists the exploratory
candidates it keeps separately from the feasible front. See [Pareto search](PARETO_SEARCH.md).

Three optional interfaces let an archive survive a checkpoint:

- `ICheckpointableEvolutionArchive<TGenome>` restores an exact, versioned snapshot. Resume requires
  it.
- `IGrowableEvolutionArchive<TGenome>` extends `ICheckpointableEvolutionArchive<TGenome>` and also
  checkpoints descriptor ranges widened during the run by
  `EvolutionOutOfRangePolicy.Grow`.
- `ICheckpointableParetoArchive<TGenome>` restores a Pareto archive's deployable and exploratory
  populations without mixing their admission rules.

### Relative descriptors

A descriptor such as program diversity measures a candidate against other candidates. Its reading changes whenever
that reference set changes. A task with such descriptors implements `IRebasingEvolutionTask<TGenome>`:

- `MeasureRebasable` gives the current reading for one genome.
- `TryRebase` is called after every committed batch, with every island's entries, and may move the references.
- `CaptureRebaseState` and `RestoreRebaseState` carry the references through a checkpoint. The restore receives the
  checkpoint's completed-evaluation count, so a state that claims a later reading is refused.

The engine keeps every archive consistent with one reading:

- **At commit.** It overwrites each evaluation's relative descriptors with the current reading before filing it, so
  a slow or cached result is filed like a fresh one.
- **When `TryRebase` reports a change.** It re-measures every island through
  `IRemeasurableEvolutionArchive<TGenome>.RemeasureEntries` (`MapElitesArchive<TGenome>` implements it). Then it
  re-keys the cross-island elite index and each island's retained history before the next parent is drawn. An entry
  whose new values no cell can hold is dropped, never kept under the old reading. An adaptive axis observes only the
  values that changed, so a rebase does not count unchanged elites twice.

A rebasing task therefore requires remeasurable archives. The state is part of the checkpoint and of the
deterministic state hash. A checkpoint written without it is refused for a rebasing task, and the reverse is refused
too. Policy trials (`EvolutionPolicyEngineTrial`) refuse a rebasing task, because their task wrappers do not forward
the rebase.

## Choosing parents

A selection policy returns an `EvolutionSelection<TGenome>`: one parent entry and the inspiration
entries shown alongside it. `EvolutionEngineOptions.SelectionPolicy` (`EvolutionSelectionPolicyKind`)
picks a built-in policy, or you can pass your own `ISelectionPolicy<TGenome>` to the engine.

| Policy | Kind | Parent | Inspirations |
| --- | --- | --- | --- |
| `UniformEvolutionSelectionPolicy<TGenome>` | `Uniform` (default) | a uniformly random occupied cell | distinct, uniformly sampled |
| `RatioEvolutionSelectionPolicy<TGenome>` | `Ratio` | exploration, exploitation or third-branch draw by `EvolutionEngineOptions.Selection` ratios | top and diverse counts from the same options |
| `CuriosityEvolutionSelectionPolicy<TGenome>` | `Curiosity` | weighted by a bounded curiosity score that rises when a parent's offspring improve the archive | as uniform |
| `DoubleEvolutionSelectionPolicy<TGenome>` | `Double` | uniform | the highest-quality elites |

In ratio selection, `EvolutionSelectionOptions.ExploitationSource` (`EvolutionExploitationSource`)
decides where an exploitation draw comes from. `GlobalTopK` uses the best elites across every
island, `IslandTopK` uses the current island's best, and `IslandPreferredTopK` uses the current
island's entries among the global best when it has any and the whole global list otherwise, which is
OpenEvolve's `_sample_exploitation_parent`. `ExploitationEliteCount` sets how many of the best the draw
is uniform over.

`EvolutionSelectionOptions.EliteBranch` (`EvolutionEliteBranch`) decides what the third branch, drawn
with probability `EliteRatio`, returns: `IslandBest` (the default), `UniformIsland`, or `UniformGlobal`,
a uniformly random entry of the cross-island elite index. That is the bounded counterpart of OpenEvolve's
`_sample_random_parent`: the index keeps only the best `GlobalEliteCount` entries, where upstream draws from every
program in its database. With an empty index it samples the target island.
`EvolutionEngineOptions.CreateOpenEvolveDefaults()` and an imported OpenEvolve config use
`IslandPreferredTopK` over the 100-entry archive and `UniformGlobal`.

A policy that learns from results implements `IOutcomeAwareEvolutionSelectionPolicy<TGenome>`. The
engine calls `Observe` with each committed evaluation and its insertion result, and checkpoints the
policy's state through `CaptureState` and `RestoreState`, so a resumed run continues with the same
scores. The curiosity policy works this way.

A policy that needs the cross-island leaderboard implements
`IEliteIndexAwareEvolutionSelectionPolicy<TGenome>`. Before each selection the engine passes it the
current `EvolutionEliteRecord<TGenome>` list from the `EvolutionGlobalEliteIndex<TGenome>` and the
island being served. Set `EvolutionEngineOptions.GlobalEliteCount` to maintain that index.

## Islands

`EvolutionEngineOptions.IslandCount` splits the population into islands, each with its own archive.
`IslandAssignment` (`EvolutionIslandAssignmentStrategy`) decides where a new proposal lands.
`RoundRobin` assigns island `evaluationId % IslandCount` whatever the parent's island.
`InheritParent` keeps a child on its parent's island, falling back to round-robin for seeds.

Each island also keeps an `EvolutionIslandHistory<TGenome>`: a bounded population history that
evicts the worst entry first, deterministically, and is saved in checkpoints. At the end of a run,
`EvolutionIslandStatus` reports each island's generation, elite count, coverage, best and mean
quality, and history size.

### Migration

Every `MigrationInterval` units of `MigrationTrigger` (`EvolutionMigrationTrigger`:
`CommittedBatches`, or `IslandGenerations` for the highest per-island generation), the engine asks
the migration policy (an `IMigrationPolicy<TGenome>`, passed to the engine) for transfers. Each one
is an `EvolutionMigration<TGenome>` naming the source island, the destination island and the copied
entry.

- `RingMigrationPolicy<TGenome>` copies each island's best distinct elites to the next island.
- `TopologyMigrationPolicy<TGenome>` does the same along any `EvolutionMigrationTopology`: `Ring`
  (to island `i + 1`), `BidirectionalRing` (to both neighbours), `Star` (island 0 exchanges with
  every other island) or `FullyConnected`. `MigrationRate` migrates that fraction of each
  source island's elites (zero, the default, migrates the caller's fixed per-island maximum), and
  `PreventsRepeatedMigration` skips elites that themselves arrived by migration.
  `DestinationsFor(topology, source, islandCount)` returns the destinations a topology implies.
- `ParetoEvolutionMigrationPolicy<TGenome>` migrates diverse feasible front members rather than a
  scalar top-K, along a topology.

### Adaptive islands

`AdaptiveIslandSearch<TGenome>` gives each island its own strategy and allocates proposals to the
islands that are progressing. See [Adaptive islands](ADAPTIVE_ISLANDS.md). Its `Statistics` return an
`EvolutionIslandStatistics` per island (proposals, outcomes, fresh measurements, restart activity and
mean recent gain), and `RecentDecisions` returns the bounded history of `EvolutionIslandDecision`s:
which island and operator each generation used, and whether it was a restart.
