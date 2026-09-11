# US-14 numeric surrogate cost-ratio pilot

Production revision: `c770896d361c71604d39e2ac9745b587ebca0f57`.
Protocol: `synthetic-surrogate-example-v3-cost-ratios`.

## What the pilot establishes

All 240 scheduled runs completed with true-measurement-only archive winners, no unknown charges, no open
reservations, no dropped receipts and no cost overruns. A separate execution of all 240 runs produced
byte-identical JSON. This verifies deterministic integration and the declared accounting protocol;
**it does not establish better quality or competitor superiority**. All 18 unadjusted paired 95% intervals
include zero. Observed regressions and tail losses are retained. The adapter remains experimental/default off.

## Fixed campaign and accounting

Two authored deterministic four-dimensional objectives × four methods × ten paired seeds (0–9) × three
evaluator tariffs. Each run begins with the same eight true-evaluated candidates for its seed. Ordinary search
generates one proposal; the other methods generate four. The old example uses heuristic leave-one-out screening;
the new package uses genome-group-disjoint fitting, residual calibration and validation. Both surrogate policies
reserve 20% random exploration, fit at most 32 recent records, and use optimism 0.1. No settings were tuned after
examining this campaign.

Evaluator tariffs are 0.1, 1 and 10; total caps are respectively 6.4, 64 and 640. Every method has an equal cap
**within** each scenario. Caps across tariffs deliberately differ: this tests model/proposal cost relative to a
fixed nominal evaluation allowance, not equal absolute spending across scenarios. Setup, generated/rejected
proposals, all fitting/calibration/validation work, inference and true evaluation are charged. Budget reservations
are conservative, so spending can finish below the cap. All prices are declared synthetic work tariffs; they
are not measured elapsed CPU, energy or API dollars. Scheduler/OS overhead is not separately measured.

Primary runs made 13,627 objective calls (Ordinary 3,680; UniformPool 3,400; SurrogatePool 3,266;
ValidatedPool 3,281). Full replay made another 13,627: **27,254 total pilot objective calls**.
Numeric reliability reports record 1,499 accepted fits, 480 insufficient-distinct-genome fallbacks and
254 validation-coverage failures. There are 1,369 numeric acquisitions; accepted fits can still encounter
domain/budget fallback, so fit acceptance is not an acquisition count.

## Results

Mean final loss (lower is better). Every paired interval uses 10,000 percentile bootstrap draws of the ten seed
differences, fixed bootstrap seed 42, no task resampling. These are retrospective, unadjusted diagnostics across
18 comparisons, not preregistered confirmation or a multiplicity-controlled release gate.

| Task | Evaluator tariff | Ordinary | Uniform pool | Historical surrogate | Validated surrogate | Validated − ordinary, paired 95% interval |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| ShiftedQuadratic4 | 0.1 | 0.00315741 | 0.00911712 | 0.00464976 | 0.0120675 | 0.00891014 [-0.000483685, 0.0212656] |
| ShiftedQuadratic4 | 1 | 0.00315741 | 0.00293735 | 0.00302775 | 0.00255962 | -0.000597789 [-0.00261492, 0.00163131] |
| ShiftedQuadratic4 | 10 | 0.00315741 | 0.00258852 | 0.00268904 | 0.00231656 | -0.000840854 [-0.00286312, 0.00144133] |
| RippledQuadratic4 | 0.1 | 0.272316 | 0.237951 | 0.298313 | 0.257182 | -0.0151339 [-0.102191, 0.0630980] |
| RippledQuadratic4 | 1 | 0.262746 | 0.200699 | 0.270568 | 0.246864 | -0.0158827 [-0.0981284, 0.0589677] |
| RippledQuadratic4 | 10 | 0.262746 | 0.200699 | 0.270568 | 0.246864 | -0.0158827 [-0.0981284, 0.0589677] |

The complete [analysis](analysis.json) includes all contrasts against uniform pools and the historical surrogate,
win/tie/loss counts, median/worst loss and reported evaluator/spending counts. For example, at tariff 0.1 on
ShiftedQuadratic4, the validated model's worst loss is 0.0578053 versus ordinary search's 0.00772433.
On RippledQuadratic4 at tariff 1, its mean loss is 0.246864 versus uniform-pool search's 0.200699.
These unfavorable outcomes are not removed because another comparator looks better.

The analysis assigns failed/incomplete/invalid rows worst-support loss 8 and retains them in the denominator;
there were none in this campaign. Structural schedule corruption is rejected rather than silently paired.

## Complete evidence and reproduction

- [raw.json.gz](raw.json.gz): lossless full JSON, including every decision, prediction, validation report,
  measured outcome and resource receipt. Compressed size 4,135,857 bytes; decompressed size 38,307,179 bytes.
- [summary.json](summary.json): all 240 scheduled rows, compact stage costs and raw hash.
- [analysis.json](analysis.json): all 24 task/tariff/method summaries and 18 paired contrasts.
- Raw SHA256: `f548d050d9bffb368c925ad5de53331c01d2ae0f8c74c67ed06d370e529d9ad7`.
- Gzip SHA256: `e2a8a02bfbdbd631850d129b9a82b1e6c8891c7db62c05d957b5da993d6ea5f9`.

At the pinned revision, build with .NET 10 and run (output paths must be new):

```powershell
dotnet run --project examples/SurrogateSearch -c Release -- --cost-ratios 10 64 TestResults/pilot-new.json
powershell -ExecutionPolicy Bypass -File eng/Export-SurrogateEvidence.ps1 -InputPath TestResults/pilot-new.json -OutputPath TestResults/summary-new.json -RawGzipPath TestResults/raw-new.json.gz
python benchmarks/analysis/analyze_surrogate.py TestResults/summary-new.json TestResults/analysis-new.json
powershell -ExecutionPolicy Bypass -File eng/Test-SurrogateSearch.ps1
```

An independent rebuild can have a different assembly hash; exact replay above used the same pinned build.
Inspect runtime/assembly metadata before comparing raw digests across environments. The compressed raw hash
can be verified after decompression; gzip container bytes are not a semantic identity for a rebuilt campaign.

## Limits and failed development attempts

Only two synthetic deterministic tasks, ten search seeds, one fixed configuration and assumed tariffs.
This is not an expensive/noisy consumer workload, calibrated single-draw uncertainty, a deployment holdout,
matched competitor tuning or evidence against OpenEvolve. Repeated search-data validation can be adaptively biased.
Uniform exploration allocates probability, not guaranteed coverage of every late improvement.

Initial full-suite verification before this campaign had two Windows file-move tests fail with
`Insufficient system resources exist to complete the requested service`; both passed unchanged in a focused
retry and subsequent full runs. A test-only array `Reverse()` expression compiled under net10 but failed under
net8; explicit `Enumerable.Reverse` resolved the overload and preserved non-mutating intent. Neither was hidden
as a discarded search result. The final audited suite passed 703 tests on each of net10/net8/net471.

