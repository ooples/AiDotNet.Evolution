# Pareto search

`ParetoArchive<TGenome>` is an opt-in archive for competing objectives. Its answer is a set of feasible tradeoffs, not one scalar winner. Existing MAP-Elites and centroid archives keep their scalar ordering and checkpoint shape.

```csharp
var definition = new EvolutionParetoDefinition(new[]
{
    new EvolutionObjectiveDefinition("milliseconds", EvolutionOptimizationDirection.Minimize, 0, 1000),
    new EvolutionObjectiveDefinition("accuracy", EvolutionOptimizationDirection.Maximize, 0, 1)
}, capacity: 64, representative: EvolutionParetoRepresentative.ClosestToIdeal);

// Evaluators must return Objectives in this exact order. Positive ConstraintViolations reject admission.
// Pass this factory to EvolutionEngine<TGenome>:
IEvolutionArchive<MyGenome> Archive(int island) => new ParetoArchive<MyGenome>(definition);

// After the run:
var tradeoffs = result.ParetoFront!;
var fastEnough = tradeoffs.Query("milliseconds", 0, 25);
double retainedHypervolume = tradeoffs.Hypervolume();
```

## Explicit semantics

- Two to eight uniquely named objectives, independently minimized or maximized. Fixed finite bounds map each objective to normalized loss: zero ideal, one worst. Out-of-bound evaluations are rejected, not clipped. Choose defensible bounds before running either method in a comparison.
- Resolution zero uses exact normalized dominance. Positive resolution is a fixed normalized epsilon-box width; strict dominance compares box coordinates. Equal boxes retain a deterministic lexicographic raw-objective representative. This is a transitive box order, not a pairwise tolerance rule and not an uncertainty model. Within-box tradeoffs can be discarded intentionally.
- Completed, finite, correctly identified evaluations with **no positive constraint violations** are eligible. The feasible archive does not retain infeasible candidates. An engine-managed, separately checkpointed infeasible exploration pool is not implemented in this slice.
- Capacity is two to 256 per island, at most 4096 total island slots. Overflow keeps the more diverse members by fixed-bound normalized crowding distance, preserving objective extremes where capacity permits; ties use ordinal genome identity. Results depend deterministically on the proposal stream. A bounded streaming archive cannot recover previously evicted candidates or promise the complete historical Pareto front.
- One retained measurement per canonical genome. Repeated measurements must be aggregated by the evaluator; they do not create multiple front members. Storage cells are stable slots, not behavior bins. Island `Coverage` is slot occupancy for this archive, not behavioral diversity.
- `Best` is explicitly `ClosestToIdeal` (sum of squared normalized losses), `Lexicographic`, or `ScalarQuality`. Snapshotting preserves that choice. `EvolutionRunResult.ParetoFront` is the nondominated union of retained island entries; the representative is chosen from that union, not independently selected island winners.
- Default engine selection is uniform across the feasible front. Default migration chooses diverse front members using crowding and the configured topology/rate/repeated-migration rule. Neither uses scalar top-k. Explicit custom policies are responsible for their declared front semantics.
- Scalar global-elite and history indexes must remain disabled. Non-uniform built-in scalar selection is rejected unless a custom policy is explicitly supplied. Scalar `TargetQuality` and scalar best-quality early stopping require the explicitly chosen `ScalarQuality` representative.
- Set `EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume` for front progress. Named evaluator metrics remain explicit optional stopping criteria. Hypervolume is exact for two and three objectives; higher dimensions throw rather than silently approximate. It uses **raw measured values**, fixed normalization and the reference point `(1,...,1)`. Empty fronts have volume zero. Occupancy/QD-score stopping is rejected for Pareto runs.

The crowding retention rule follows the diversity principle described in [pymoo's NSGA-II documentation](https://pymoo.org/algorithms/moo/nsga2.html). This archive is **not** a full NSGA-II population algorithm. Hypervolume uses the dominated-volume definition documented in [pymoo's indicators reference](https://pymoo.org/misc/indicators.html).

## Checkpoints and compatibility

Pareto runs write inner engine-state schema 8, carrying the complete objective order, bounds, directions, resolution, capacity and representative policy. Legacy scalar state remains schema 6, or schema 7 when measurement provenance is present. New optional JSON fields are omitted for scalar results/snapshots/checkpoints.

Restore validates the same archive definition, exact storage slots, version, feasibility, uniqueness and nondominance transactionally. Offline `EvolutionEngine<TGenome>.ReadCheckpoint` returns front metadata through `EvolutionCheckpointContents<TGenome>.ParetoFront`. Corrupt front metadata and front invariants are rejected **before any task-owned genome codec executes**. Definition changes invalidate resume rather than reinterpret old objectives.

State-hash comparisons must use matching component identities, including the genome codec even when an uninterrupted control run does not save checkpoints. Runtime timing is excluded from the hash.

## Matched-budget campaign

```powershell
dotnet run --project examples/ParetoSearch -c Release -- <new-report.json> <40-character-source-commit>
```

The executable runs 180 experiments: two authored bounded quadratic tasks, 30 paired seeds, and three methods. Each receives 256 successful evaluation attempts, the same initial genomes, mutation code, worker count and scalar weights. Comparators are a scalar 32-bin MAP-Elites archive and a scalar single-best archive; their capacity difference is reported explicitly. One task has a disconnected feasible region.

Reports retain every final front's objective vectors and genome, counters, state hashes, declared bounds/reference point, source revision and runtime. Paired bootstrap intervals (2000 resamples) summarize hypervolume, each objective's minimum and best scalar quality. These are descriptive, not multiplicity-adjusted claims. Wall-clock measurements are observational. Results do not establish OpenEvolve superiority, representative algorithm-optimization performance, or safe consumer deployment.

The example refuses to overwrite a prior report. CI runs it and uploads the raw JSON as evidence; failure to consume a declared budget, evaluator failure, an empty front or an infeasible retained member fails the campaign.

## Regression evidence

`ParetoArchiveTests` covers conflicting objectives, heterogeneous units/directions, transitive epsilon boxes, deterministic capacity eviction, snapshot representatives, exact 2D/3D hypervolume, feasibility, identity, bounds, checkpoint resume, pre-codec corruption rejection and accidental scalar policy/stopping rejection. Existing scalar compatibility tests run in the same full suite.
