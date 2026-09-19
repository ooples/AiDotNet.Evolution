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

`ProjectWithReport` also returns immutable remap provenance: source/target definition hashes,
mutation versions, offered/retained elite counts, and collision discards. No target/report is
returned on validation or version-overflow failure. The report remains a snapshot if the
caller later changes the returned archive. Store it alongside the full target geometry and
checkpoint configuration; the target definition hash is the engine's compatibility boundary.
This is traceable offline remapping, not live reconfiguration or a proof of fitness validity.

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

## Measured resource-budget comparison

`eng/Test-ArchiveResources.ps1` exercises a fresh worker process for every paired case and
replay. It also forces a 1 MiB failure before evaluation and retains 64-dimensional grid
configuration failures separately. The default grid guard limits the logical product to
10 million cells; it is not evidence of a dense memory allocation. The paired quality plan
uses 12 and 20 dimensions, where both defaults are valid, with 32 elite slots and identical
evaluation/proposal limits. The centroid route uses all dimensions, including a separate
64-dimensional support probe.

The full development campaign is fixed at 32 seeds, two tasks, two dimensions, 256 evaluations
per case, and a declared 256 MiB observed peak-resident budget for both methods. Each case has
its own process; the runner records the plan before execution, alternates method order and
retains failures/timeouts. Replay incurs new physical evaluations and is accounted separately.
No extra seeds are added in response to significance. A single primary endpoint averages
paired common-reference utility differences over the four contexts within each seed, then
bootstraps those seed blocks. Per-context/resource summaries are descriptive.

[Process.PeakWorkingSet64](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.peakworkingset64?view=net-10.0)
measures process-lifetime peak resident memory, including shared pages and startup. It is
not retained archive bytes or total allocated bytes. The v2 worker observes it at construction,
before search, every 16 `Evaluated` events and after common-reference projection,
records the observation count/cadence, stops admission on an observed overrun, and
assigns the failed case zero utility while preserving actual charges. This is an observed
budget gate, **not an OS-enforced allocation ceiling**; an overrun can happen before it is
observed. Artifact metadata hashing/JSON serialization is outside the measured boundary.
The OS retains the lifetime peak between observations; admission may continue between
checks, and the final observation determines the memory verdict. Per-event `Process.Refresh`
polling was rejected after the first pinned campaign hit 60-second worker timeouts on Windows
(100 diagnostic observations took 3.98 seconds). That interrupted campaign is retained as
failed instrumentation evidence; v2 keeps its tasks, seeds, budgets and analysis unchanged.
The grid worker does not allocate unused search centroids to hide their cost. Runtime,
binary hashes, GC mode, CPU/wall time, allocations and memory observations are retained.

```powershell
dotnet build benchmarks/AiDotNet.Evolution.Quality -c Release
python benchmarks/analysis/run_archive_resources.py --worker benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll --output TestResults/archive-primary --revision <full-built-source-revision>
python benchmarks/analysis/run_archive_resources.py --verify TestResults/archive-primary
```

The pinned full campaign at `f990516a31c5e6c9f2e7f7110373581a9d460cfc` completed
all 512 cases with no failures or unknown calls: 65,536 primary evaluations and 65,536
additional replay evaluations. Quality replay was exact. The pooled centroid-minus-grid
utility difference was **-0.00058978**, with paired-seed bootstrap 95% interval
**[-0.00504834, 0.00419849]**: inconclusive, not equivalence or superiority.
Median process peak RSS across the four contexts was 44.95–46.09 MB for the grid and
44.80–45.37 MB for centroids (decimal MB); both stayed within the declared 256 MiB cap.
These descriptive process measurements include instrumentation/runtime overhead, not
isolated retained archive memory. The host was not exclusive; latency is not a controlled
performance claim. No default change is justified.

[Raw primary/replay, failed instrumentation and verification evidence](../benchmarks/evidence/archive-resources/f990516/README.md)
retains the fixed plans, all observations/traces, immutable remap reports, byte checksums,
low-memory/64D support probes and source-pinned test/package receipts. Representative
held-out tasks, fitted CVT sites and competitor comparisons remain separate validation
work; this authored campaign establishes the implementation's paired-budget reporting,
not a general quality advantage.
