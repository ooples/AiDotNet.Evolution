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
- `ParetoArchive` also carries one scalar **reporting direction**, `Maximize` unless you pass another to its constructor; `new ParetoArchive<TGenome>(definition, EvolutionOptimizationDirection.Minimize)` is required when the task reports minimized scalar quality. Objective directions are per objective and independent of it. An evaluation whose direction disagrees with the archive's is refused entirely, so the two must be chosen together.
- A completed evaluation the front refuses for any reason other than feasibility - an out-of-bound or non-finite value, a vector of the wrong length, a missing quality, or a direction mismatch - is retained as an `objective_invalid` diagnostic in `EvolutionRunResult.RetainedFailures`, naming the objective index, name, offending value, declared bounds and reason. An empty front with zero retained failures therefore means the search found nothing, not that the contract was broken.
- Completed, finite, correctly identified evaluations with **no positive constraint violations** are eligible for the deployable front. Set `infeasibleCapacity` in `EvolutionParetoDefinition` to opt into a separate pool (one to 256 entries, zero disables it). Its members never enter `Entries`, `Best`, `Front`, `Sample`, `Get`, hypervolume, or default migration. `InfeasibleEntries` on archive snapshots and `InfeasibleExploration` on run results expose them explicitly as non-deployable; offline checkpoint records use `InfeasibleExploration` as their source.
- Exploration retention minimizes the largest reported violation, then the number of violated constraints, the ordered violation vector and ordinal identity. Task authors must report comparable violation scales; no implicit unit normalization or scalar-quality reward is applied. This is bounded feasibility-first exploration, not a certificate that a candidate can be repaired.
- Feasible capacity is two to 256 per island; feasible plus exploratory capacities total at most 4096 across islands. Feasible overflow keeps the more diverse members by fixed-bound normalized crowding distance, preserving objective extremes where capacity permits; ties use ordinal genome identity. Results depend deterministically on the proposal stream. A bounded streaming archive cannot recover previously evicted candidates or promise the complete historical Pareto front.
- One retained measurement per canonical genome. Repeated measurements must be aggregated by the evaluator; they do not create multiple front members. Storage cells are stable slots, not behavior bins. Island `Coverage` is slot occupancy for this archive, not behavioral diversity.
- `Best` is explicitly `ClosestToIdeal` (sum of squared normalized losses), `Lexicographic`, or `ScalarQuality`. Snapshotting preserves that choice. `EvolutionRunResult.ParetoFront` is the nondominated union of retained island entries; the representative is chosen from that union, not independently selected island winners.
- Default engine selection is uniform across the feasible front when exploration is disabled. With a nonempty enabled pool, the engine selects it with probability 0.1, or always while the feasible front is empty, allowing an infeasible-only start to find valid children. Supply `new ParetoEvolutionSelectionPolicy<TGenome>(probability)` to choose another probability; zero is fallback-only. Only a policy implementing `IInfeasibleExplorationSelectionPolicy<TGenome>` counts pool members as selectable material: an explicit custom policy that samples the feasible front alone leaves a pool-only island out of selection, and a policy that declines an occupied island moves the engine to the next island rather than ending the batch with `NoCandidates`. The probability is checkpoint-identified, and disabled exploration consumes no additional randomness. Default migration chooses diverse **feasible** front members using crowding and the configured topology/rate/repeated-migration rule. Explicit custom policies are responsible for their declared front semantics.
- Scalar global-elite and history indexes must remain disabled. Non-uniform built-in scalar selection is rejected unless a custom policy is explicitly supplied. Scalar `TargetQuality` and scalar best-quality early stopping require the explicitly chosen `ScalarQuality` representative.
- Set `EarlyStopping.Metric = EvolutionEarlyStoppingMetric.ParetoHypervolume` for front progress. Named evaluator metrics remain explicit optional stopping criteria. Hypervolume is exact for two and three objectives; higher dimensions throw rather than silently approximate. It uses **raw measured values**, fixed normalization and the reference point `(1,...,1)`. Empty fronts have volume zero. Occupancy/QD-score stopping is rejected for Pareto runs.

The crowding retention rule follows the diversity principle described in [pymoo's NSGA-II documentation](https://pymoo.org/algorithms/moo/nsga2.html). This archive is **not** a full NSGA-II population algorithm. Hypervolume uses the dominated-volume definition documented in [pymoo's indicators reference](https://pymoo.org/misc/indicators.html).

## Checkpoints and compatibility

Pareto runs write inner engine-state schema 8, carrying the complete objective order, bounds, directions, resolution, capacities and representative policy. Enabled exploration is saved in a separate collection, with disjoint genome/evaluation identities and storage slots; restored entries cannot be relabeled between the two populations. Legacy scalar state remains schema 6, or schema 7 when measurement provenance is present. New optional JSON fields are omitted for scalar results/snapshots/checkpoints.

Restore validates the same archive definition, exact storage slots, version, feasibility, uniqueness and nondominance transactionally. Offline `EvolutionEngine<TGenome>.ReadCheckpoint` returns front metadata through `EvolutionCheckpointContents<TGenome>.ParetoFront`. Corrupt front metadata and front invariants are rejected **before any task-owned genome codec executes**. Definition changes invalidate resume rather than reinterpret old objectives.

State-hash comparisons must use matching component identities, including the genome codec even when an uninterrupted control run does not save checkpoints. Runtime timing is excluded from the hash.

## Matched-budget campaign

```powershell
dotnet run --project examples/ParetoSearch -c Release -- <new-report.json> <40-character-source-commit>
```

The executable runs 180 experiments: two authored bounded quadratic tasks, 30 paired seeds, and three methods. Each receives 256 successful evaluation attempts, the same initial genomes, mutation code, worker count and scalar weights. Comparators are a scalar 32-bin MAP-Elites archive and a scalar single-best archive, both wrapped in a feasible-only admission guard; their capacity difference is reported explicitly. One task has a disconnected feasible region. This campaign leaves the optional exploration pool disabled, so it isolates feasible-front search. Exploration correctness is separately regression-tested; no measured quality improvement is claimed for enabling it.

Reports retain every final front's objective vectors and genome, counters, state hashes, declared bounds/reference point, source revision and runtime. Paired bootstrap intervals (2000 resamples) summarize hypervolume, each objective's minimum and best scalar quality. These are descriptive, not multiplicity-adjusted claims. Wall-clock measurements are observational. Results do not establish OpenEvolve superiority, representative algorithm-optimization performance, or safe consumer deployment.

The example refuses to overwrite a prior report. CI runs it and uploads the raw JSON as evidence; failure to consume a declared budget, evaluator failure, an empty front or an infeasible retained member fails the campaign.

## Regression evidence

## Known limitations

- Retained hypervolume can fall. Capacity overflow keeps the more diverse members by crowding distance, not the members that maximize volume, so an insertion that evicts an interior member can lower the reported volume. The archive optimizes bounded diversity, not a volume indicator.
- A genome retained in the exploration pool cannot later enter the deployable front. One retained measurement per canonical genome is the admission rule for both populations, so a repaired re-measurement of the same canonical genome is refused as a duplicate identity; re-measurement must be aggregated by the evaluator, or the repaired candidate must canonicalize differently.
- Malformed island archives reach `EvolutionRunResult` through the front constructor, whose `ArgumentException` names the parameter `entries` rather than the result's own `islands`.
- The enumeration values added for this feature - `EvolutionArchiveInsertionResult.RetainedForExploration`, `EvolutionCheckpointEntrySource.InfeasibleExploration`, `EvolutionEarlyStoppingMetric.ParetoHypervolume` and `EvolutionParetoRepresentative` - are release-note material; `CHANGELOG.md` is generated by release-please from the feature commits and does not mention them yet.

`ParetoArchiveTests` covers conflicting objectives, heterogeneous units/directions, transitive epsilon boxes, deterministic capacity eviction, snapshot representatives, exact 2D/3D hypervolume, feasibility, identity, bounds, checkpoint resume, pre-codec corruption rejection and accidental scalar policy/stopping rejection. Exploration tests exercise infeasible-only starts, controlled exploration after feasibility, atomic two-population restore, separate serialized results, capacity/identity/slot corruption, and deterministic resume. Existing scalar compatibility tests run in the same full suite.
