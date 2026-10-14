# V1-70 (#175): engine overhead and peak working set at `65ced88`

Supersedes the **aidotnet column** of [`../5699058/`](../5699058/README.md), which left one target open:
peak working set at 16 workers was 64.1 MB against a 64 MB target, and that figure was taken with #197
applied locally rather than merged. #197 (`9e27171`) and #200 (`65ef0d2`) are both on `main` now, so this
is the figure the target asked for. **Not a headline claim (R12):** a C# controller against a Python one is
expected to win on overhead. Raw rows: `head-to-head.json`.

Measured at `main` `65ced88` on the same workstation as `5699058`: AMD Ryzen 9 3950X (16 cores / 32
threads), 64 GB RAM, Windows 11 Pro 10.0.26220, .NET SDK 10.0.402, Release.

## Method

Unchanged from `5699058` for our side: a null variation and a null evaluator in-process, one run per fresh
process, `islands=1`, steady-state cost per evaluation `(T(2N) - T(N))` divided by the **completed**
evaluations the two runs differ by, which cancels startup. `N = 8,000`. Nine repeats per worker count; the
per-worker figure is the median of the nine per-repeat values and the interval is a 10,000-draw percentile
bootstrap of that median (seed 20261014).

Allocation is reported two ways, because the difference that cancels startup for timing cancels it for
allocation too: the table's column is the whole-run figure `AllocatedBytes / Evaluations` of the 16,000
run (16,834 B/eval), and `head-to-head.json` also carries the steady-state figure from the same
difference as the timing (16,810 B/eval). They agree to 0.15%, so nothing here turns on the choice.

**No resample yielded a non-positive difference** at any worker count (`nonpositive_differences: 0` in every
summary row), which is the condition the first acceptance criterion names.

## Results

| workers | ours us/eval [95% CI] | ours peak MB (median) [min-max] | ours alloc B/eval (whole run) |
| --- | --- | --- | --- |
| 1 | 45.9 [41.7, 48.9] | 61.40 [61.14-61.57] | 16,834 |
| 2 | 47.2 [45.9, 49.2] | 61.34 [61.16-61.53] | 16,834 |
| 4 | 46.2 [44.8, 50.6] | 61.34 [60.96-62.22] | 16,834 |
| 8 | 47.1 [45.5, 51.9] | 61.32 [61.02-61.37] | 16,834 |
| 16 | 44.8 [35.8, 46.6] | 61.23 [61.06-61.73] | 16,834 |

Peak working set is now flat in the worker count and **below the target at every count**, not only at 16:
61.2-61.4 MB against 82.7-82.9 MB at `5699058`. Per-evaluation cost fell from 59.9-65.3 us to 44.8-47.2 us.

## Targets

| metric | target | measured | status |
| --- | --- | --- | --- |
| our overhead per evaluation, 1-16 workers (N >= 8,000) | <= 100 us | 44.8-47.2 us | **met** |
| ours / OpenEvolve, 95% CI upper bound, every worker count | <= 0.05 | <= 0.0082, measured at `5699058` | **met at `5699058`**, not re-measured here |
| our peak working set, 16 workers | <= 64 MB | **61.23 MB** (median of 9; worst repeat 61.73 MB) | **met** |

## What this run is not

- **OpenEvolve was not re-run.** Its medians and peaks in `head-to-head.json` are carried from `5699058`
  (OpenEvolve 0.3.2 `411fb59`, Python 3.13). The ratio target was already met there with a paired bootstrap
  interval; a ratio against a carried denominator is not a paired measurement, so `head-to-head.json` records
  it as `ratio_ours_over_theirs_carried_denominator` and does not restate the prior interval as if it were
  re-measured. Our median fell, so the bound is **expected** to hold, but that is an expectation and not a
  result: the 0.0082 interval came from paired per-repeat ratios, and a numerator from a different run set
  cannot establish where a new paired interval's upper bound would sit. Our per-repeat spread is also wider
  here in relative terms (30.1-51.3 us at 16 workers against 56-70 us before), which is the reason not to
  treat the old bound as carried forward.
- **One workstation, not a fleet.** The same machine as `5699058`, which is what makes the two comparable;
  both are Windows, and the figures are machine-dependent.
- **Nine repeats, one N.** `N = 8,000` with the `2N = 16,000` run supplying the peak, matching the earlier
  protocol so the columns can be compared cell by cell.

## Reproducing

The committed driver for the full head-to-head is
[`benchmarks/external/run_overhead_head_to_head.py`](../../../external/run_overhead_head_to_head.py); it needs
`--upstream <pinned OpenEvolve checkout>` because it measures both systems and bootstraps the paired ratio.
This run did **not** use it: the pinned checkout on this machine could not import OpenEvolve
(`ModuleNotFoundError: dacite`), and installing upstream's dependency set is a separate step. So our side was
measured with the loop below, which is the same per-run command the driver issues for `aidotnet`, and the
OpenEvolve column stayed carried. Re-running the driver once the checkout's dependencies are installed is
what would replace the carried column with a paired measurement.

```powershell
dotnet build benchmarks/EvolutionOverhead/EvolutionOverhead.csproj -c Release -f net10.0
$dll = "benchmarks\EvolutionOverheadin\Release
et10.0\EvolutionOverhead.dll"
foreach ($w in 1,2,4,8,16) { foreach ($r in 0..8) { foreach ($n in 8000,16000) {
  $j = dotnet $dll $n $w 1
  if ($LASTEXITCODE -ne 0) { throw "run failed: workers=$w repeat=$r n=$n" }
  ($j | ConvertFrom-Json) | ConvertTo-Json -Compress | Add-Content rows.jsonl
} } }
```

Each run prints one JSON object with `Evaluations` (completed, not requested), `Seconds`,
`PeakWorkingSetBytes` and `AllocatedBytes`, and exits non-zero if the completed count falls short of the
budget, which is what `evaluations_verified` in `head-to-head.json` records. The per-worker medians,
the min-max ranges and the 10,000-draw percentile bootstrap of the timing median (seed 20261014) are then
computed over the nine `(small, large)` pairs per worker count; `rows` in `head-to-head.json` carries every
pair, so any reported figure can be recomputed from it without re-running anything.
