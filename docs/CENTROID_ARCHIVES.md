# Fixed-centroid archives

Roadmap US-22: optional bounded high-dimensional partitions. `CentroidArchive<TGenome>` implements the existing
archive and checkpoint contracts. The current sparse grid remains the default and was already capacity-bounded.

```csharp
var definition = new CentroidArchiveDefinition(
    new[] {
        new EvolutionDescriptorDefinition("latency", 0, 100, 10),
        new EvolutionDescriptorDefinition("size", 0, 1000, 10)
    },
    new[] { new[] { 0.25, 0.75 }, new[] { 0.75, 0.25 } });
// Supply this factory to EvolutionEngine<MyGenome>:
Func<int, IEvolutionArchive<MyGenome>> factory = _ => new CentroidArchive<MyGenome>(definition);
```

Coordinates are normalized to [0,1] using descriptor bounds. Sites are supplied in normalized coordinates and copied
immutably; descriptor and site order are semantic. Nearest squared Euclidean distance routes to a site; exact ties
use the lowest site index. A cell keeps the deterministic best completed, feasible scalar evaluation. Original named
descriptors remain on evaluations; cell keys contain one site index, not one index per descriptor. One genome cannot
occupy multiple cells. Missing/nonfinite values reject; out-of-range values use explicit Reject or Clamp. Growth and
overflow bins are deliberately unsupported. At most 10,000 sites and one million coordinates are accepted.

Routing is a bounded O(KD) scan, not an approximate-neighbor index. The supplied sites may come from an independently
fitted CVT, but arbitrary sites do not constitute a fitted centroidal tessellation. See the original
[CVT-MAP-Elites paper](https://arxiv.org/abs/1610.05729). Normalization and site preparation must be frozen before a
comparison, and their fitting costs included when reporting end-to-end resources.

## Checkpoints and reporting

Given fixed geometry, when a checkpoint resumes through the same archive factory, then cells and state match.
`DefinitionHash` includes normalization, ordered sites, routing semantics and quality direction. Keep the complete
definition in the experiment configuration; the engine checkpoint does not regenerate or infer missing centroids.
Changed geometry refuses old checkpoints. Restore stages and validates every entry before publishing any state.

Given a changed partition, when `CentroidArchive<TGenome>.Project(source, newDefinition)` succeeds, then it returns a
new archive with versioned geometry and deterministic collision resolution, leaving the source untouched. An
unplaceable elite rejects the whole projection. This is an offline operation, not live engine reconfiguration.
It cannot recover candidates discarded earlier or transfer old evaluation validity to a different task.

`IEvolutionArchiveCellCount` lets non-grid archives declare their physical cell count. Engine status, coverage-based
stopping and immutable snapshots preserve K instead of multiplying irrelevant grid bin counts. Existing archives
without that optional contract retain the grid interpretation. Occupancy under different partitions is not directly
comparable: project both retained repertoires into one independently frozen reference definition.

## Development comparison

```powershell
powershell -ExecutionPolicy Bypass -File eng/Test-ArchivePartition.ps1
dotnet run --project benchmarks/AiDotNet.Evolution.Quality -c Release -- --archive-partition 10 256 <full-source-revision> <new-report.json>
```

The matched runner uses two 12-dimensional synthetic tasks, eight identical starting genomes, the same mutation and
uniform parent selection, the same evaluation/proposal caps and 32 retained-elite slots. Both methods are scored on
one independent 32-site reference partition. It retains every run, trace and independent evaluator/resource counter;
failed/incomplete runs receive zero reference utility. The manifest records complete search/reference definitions.
CI checks exact replay, accounting, capacity and paired initialization, not which archive wins.

Equal elite-slot caps do **not** establish equal measured RAM: centroid coordinates and indexes also consume memory.
The pilot uses frozen uniform Voronoi sites, not fitted CVT. Common-reference utility sees retained elites only.
Controlled memory/latency scaling and representative quality confirmation remain open; no default change is justified
by execution smoke tests alone.
