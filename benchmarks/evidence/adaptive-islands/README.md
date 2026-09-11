# Fixed adaptive-island development pilot

Production revision: `a6199ee53d34f176e45f2acd3776557b469a0cd4`. Embedded example assembly: `1.0.0+a6199ee53d34f176e45f2acd3776557b469a0cd4`.

The restart mechanism escapes the deliberately separated local basin; this experiment does **not** establish that adaptive allocation is better than uniform allocation. Both restart policies reach quality >= 0.95 in all 30 separated-basin runs, while neither non-restart policy escapes its initial quality 0.5. The adaptive-versus-uniform intervals include zero on both fixtures. On the easy quadratic, adaptive allocation has a worse observed tail, and its restart ablation has a tiny negative quality effect. Keep the feature opt-in; do not promote a universal default from these authored examples.

## Complete primary evidence

- [Compressed raw artifact](a6199ee/raw.json.gz): every primary run's measurements/quality curve, full resource receipts, policy state, allocation decisions, counters and validation hashes; no primary runs omitted.
- [Auditable summary](a6199ee/summary.json): every one of the 240 primary runs, source/binary hashes, compressed/uncompressed hashes, costs and per-island statistics.
- [All paired contrasts](a6199ee/analysis.json): generated with the repository's existing paired-bootstrap interval implementation; 10,000 resamples, seed 42, 95% percentile intervals, no task resampling.

Raw SHA256: `d6036a670065ca96270edcc1393dc77ec8ee68ffe55831aabb3041063b4637bb`. Gzip SHA256: `7472b37fb0f51f224c3bc91be86adbb03deb7f7d52101b7fe4489c1117d68ae9`. Lossless compression reduces 34,643,228 bytes to 1,749,357 bytes; the analyzer verifies both hashes before reporting results.

The design is 2 authored one-dimensional objectives x 4 fixed policies x 30 paired seeds x 128 true objective calls, including two identical initialization points (0.2, 0.21) near the local basin. There are **30,720 primary evaluator calls**, 30,720 additional independent replay calls and 1,024 aligned checkpoint-validation calls: **62,464 local CPU objective calls total**. All 240 primary/replay pairs and all eight checkpoint cases pass, with no failed run, unknown cost, unsettled reservation or budget overrun. Every run uses all 128 calls and preserves at least its measured seed quality. Replay and checkpoint cases validate determinism and accounting; they are not additional independent search seeds in the intervals.

Policy settings are frozen in the example source: two small/large triangular mutations, separate uniform restart children, 16-proposal epochs, minimum one proposal per island, 16-outcome reward windows, gain scale 0.01, diversity weight 0.1, stagnation window 12, and six restart proposals per phase. Migration interval is 16; proposals are serial and deterministic. Each island retains one scalar elite. This particular archive has no extra diversity-cell opportunity after initialization; allocation-floor/new-cell reward behavior is also covered by unit tests.

This is retrospective **development** evidence, not preregistered confirmation, independent correctness qualification, generalization, measured runtime scaling, or an OpenEvolve result. Only objective-call work is priced; proposal/setup/checkpoint CPU is not included in those units. Windows local .NET 10 runs used a process-local two-processor/workstation-GC/768-MiB heap limit after unrelated host memory pressure; no hardware performance claim is made. Unadjusted intervals are diagnostic, not multiplicity-corrected release gates. Differences are absolute quality units, not percentages of arbitrarily shifted scores.

## Results

| Fixture | Method | Median final quality | Worst final quality | Reached >=0.95 |
| --- | --- | ---: | ---: | ---: |
| ShiftedQuadratic1 | Uniform | 0.9999999 | 0.9999917 | 30/30 |
| ShiftedQuadratic1 | Adaptive | 0.9999999 | 0.9991476 | 30/30 |
| ShiftedQuadratic1 | UniformRestart | 0.9999999 | 0.9999917 | 30/30 |
| ShiftedQuadratic1 | AdaptiveRestart | 0.9999998 | 0.9991476 | 30/30 |
| SeparatedBasins1 | Uniform | 0.5000000 | 0.5000000 | 0/30 |
| SeparatedBasins1 | Adaptive | 0.5000000 | 0.5000000 | 0/30 |
| SeparatedBasins1 | UniformRestart | 0.9999925 | 0.9951342 | 30/30 |
| SeparatedBasins1 | AdaptiveRestart | 0.9999932 | 0.9997699 | 30/30 |

| Fixture | Contrast | Mean paired quality difference | Diagnostic 95% interval |
| --- | --- | ---: | --- |
| ShiftedQuadratic1 | Adaptive - Uniform | -0.00002799469 | [-0.00008515806, 8.933292e-7] |
| ShiftedQuadratic1 | AdaptiveRestart - UniformRestart | -0.00002799322 | [-0.00008518577, 9.157206e-7] |
| ShiftedQuadratic1 | UniformRestart - Uniform | -5.803457e-8 | [-2.141523e-7, 7.834337e-8] |
| ShiftedQuadratic1 | AdaptiveRestart - Adaptive | -5.656091e-8 | [-1.241802e-7, -1.365429e-8] |
| SeparatedBasins1 | Adaptive - Uniform | 0.000000 | [0.000000, 0.000000] |
| SeparatedBasins1 | AdaptiveRestart - UniformRestart | 0.0001497739 | [-0.00002566637, 0.0004804735] |
| SeparatedBasins1 | UniformRestart - Uniform | 0.4998283 | [0.4995028, 0.4999930] |
| SeparatedBasins1 | AdaptiveRestart - Adaptive | 0.4999780 | [0.4999596, 0.4999910] |

The separated-basin fixture intentionally rewards escape; successful restarts here do not predict their value on arbitrary objectives. The tiny negative quadratic restart contrast remains visible rather than being discarded because its practical scale is small.

## Reproduce

From the production revision, create a new raw output (do not overwrite evidence):

```powershell
$env:DOTNET_PROCESSOR_COUNT='2'
$env:DOTNET_gcServer='0'
$env:DOTNET_GCHeapHardLimit='0x30000000'
dotnet run --project examples/AdaptiveIslandSearch -c Release -p:UseSharedCompilation=false -- 30 128 TestResults/new-island-pilot.json
```

Use the evidence tooling from this PR to export and analyze it:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File eng/Export-IslandEvidence.ps1 -InputFile TestResults/new-island-pilot.json -Revision a6199ee53d34f176e45f2acd3776557b469a0cd4 -OutputDirectory TestResults/new-island-evidence
python benchmarks/analysis/analyze_islands.py TestResults/new-island-evidence/summary.json
python -m unittest discover -s benchmarks/analysis -v
```

The exporter refuses existing outputs; the analyzer rejects missing/duplicate runs, failed accounting, mismatched initialization and replay, and altered raw bytes. Assembly hashes can differ across builds; compare recorded provenance and deterministic search outputs, not an unsupported cross-build binary-equality claim.
