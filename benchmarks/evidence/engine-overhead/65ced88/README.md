# V1-70 (#175): engine overhead, paired head-to-head at `65ced88`

Supersedes [`../5699058/`](../5699058/README.md), which left one target open (peak working set at 16
workers, 64.1 MB against a 64 MB target, measured with #197 applied locally rather than merged) and
#197 (`9e27171`) and #200 (`65ef0d2`) are both on `main` now. **Not a headline claim (R12):** a C#
controller against a Python one is expected to win on overhead. Raw rows: `head-to-head.json`.

Produced by the committed driver,
[`benchmarks/external/run_overhead_head_to_head.py`](../../../external/run_overhead_head_to_head.py),
which measures **both** systems - the previous attempt at this rerun measured only ours and carried
the OpenEvolve column forward, which review rightly refused as a basis for restating a paired bound.
The driver now also **alternates which system runs first** (even repeats ours first, odd repeats
theirs), so going first - and paying the cold page-cache and cold-JIT cost of it - is not a fixed
property of one column. Every row records its order in `ran_first`, and each summary cell carries the
split as `repeats_aidotnet_first` / `repeats_openevolve_first` (5/4 across 9 repeats).

Machine: AMD Ryzen 9 3950X (16 cores / 32 threads), 64 GB RAM, Windows 11 Pro 10.0.26220, .NET SDK
10.0.402 Release; OpenEvolve 0.3.2 (`411fb59`) on Python 3.13 in a venv carrying its `pyproject`
dependencies plus `psutil`.

## Method

`--repeats 9 --workers 1,2,4,8,16 --ours-n 8000 --openevolve-n 300`. Steady-state cost per evaluation
is `(T(2N) - T(N))` divided by the evaluations the two runs **actually completed**, which cancels
startup; the driver refuses a cell whose completed difference falls below `n/2`. Each repeat runs the
two systems back to back, so the ratio is the median of per-repeat **paired** ratios with a 4,000-draw
percentile bootstrap interval (seed 7). A non-positive difference or a hang is re-measured, never
averaged in.

**The machine was quiet, and that is recorded rather than assumed.** The run is wrapped in a guard
that requires three consecutive readings of peak total CPU under 20% before starting: 16.6%, 8.8% and
9.7% opened the gate, after it had reset six times over the preceding four minutes. The sample taken
immediately after the driver exited read 40.2%, with the last OpenEvolve child processes still tearing
down - that sample is after the final measurement, not during one. An earlier attempt at this same
measurement was discarded because another session was building a solution with ~25 MSBuild workers
throughout it.

## Results

| workers | ours us/eval | OpenEvolve us/eval | ours / theirs [95% CI] | ours peak MB | OpenEvolve peak MB |
| --- | --- | --- | --- | --- | --- |
| 1 | 48.3 | 26,917 | 0.0018 [0.0016, 0.0021] | 61.34 | 142.7 |
| 2 | 49.9 | 20,813 | 0.0024 [0.0022, 0.0028] | 61.38 | 208.6 |
| 4 | 49.9 | 15,790 | 0.0033 [0.0020, 0.0097] | 62.12 | 340.2 |
| 8 | 47.2 | 11,835 | 0.0039 [0.0033, 0.0046] | 61.46 | 601.7 |
| 16 | 47.7 | 9,164 | 0.0049 [0.0039, 0.0058] | 61.46 | 1,121.4 |

None of the 90 measurements produced a non-positive `T(2N) - T(N)`. The 4,000 draws per worker count
are bootstrap resamples of those 9 paired measurements - resampling, not 4,000 further measurements,
so the interval reports how far the median of 9 repeats could move, not a 4,000-run precision.

**Alternating the order did not move the verdict.** Against the fixed-order run it replaces, our
medians shifted by under 3 us (46.4-49.4 -> 47.2-49.9) and the ratios by under 0.001 at every worker
count; the direction is not consistent, so the order effect is below this machine's repeat-to-repeat
noise rather than a correction in our favour. Their column moved further (9,164 vs 11,369 us at 16
workers) while the ratio there still rose slightly, which is a reminder that the reported ratio is the
median of per-repeat paired ratios and not the quotient of the two medians in the table.

**Per-repeat spread, stated rather than smoothed:** ours ranges 43.6-66.4 us at 1 worker and
27.2-177.7 us at 4, against medians of 48.3 and 49.9; theirs ranges 19,082-31,145 us at 1 worker and
4,163-24,723 us at 4. Every per-system range is in the JSON as
`summary[].aidotnet_us_per_eval_range` / `openevolve_us_per_eval_range`. That 4-worker spread is also
why the widest interval is there: the medians are what the targets are judged on, and the paired
bootstrap is what the interval is built from, precisely because single repeats on a desktop swing
this far.

**Two hangs.** OpenEvolve deadlocked twice (`n=600` at 8 workers, repeat 8; and at 16 workers,
repeat 7); the driver killed each at 180 s, recorded them under `hangs` and re-measured the cell. Its
memory also grows with the pool: 143 MB at one worker to 1.12 GB at sixteen, against our flat 61 MB.

## Targets

| metric | target | measured | status |
| --- | --- | --- | --- |
| our overhead per evaluation, 1-16 workers (N >= 8,000) | <= 100 us | 47.2-49.9 us | **met** |
| ours / OpenEvolve, 95% CI upper bound, every worker count | <= 0.05 | <= **0.0097** (worst, 4 workers) | **met, paired and measured** |
| our peak working set, 16 workers | <= 64 MB | **61.46 MB** | **met** |

Peak working set is flat across worker counts (61.34-62.12 MB, the maximum at 4 workers rather than
16), so the memory target is met at every count rather than only at the one the metric names.

## What this run is not

- **One workstation.** The same machine as `5699058`, which is what makes the two comparable; both
  are Windows, and the figures are machine-dependent.
- **Nine repeats at one N** (8,000 ours, 300 theirs, with the `2N` run supplying the peak), matching
  the earlier protocol so the columns compare cell by cell.
- **Not a quality claim.** Both systems run a null LLM and a null evaluator; this measures
  orchestration cost, nothing about the programs either one would find.

## Reproducing

```powershell
dotnet build benchmarks/EvolutionOverhead/EvolutionOverhead.csproj -c Release -f net10.0
python -m venv oe-venv
./oe-venv/Scripts/python.exe -m pip install openai pyyaml numpy tqdm flask dacite psutil
./oe-venv/Scripts/python.exe benchmarks/external/run_overhead_head_to_head.py `
  --upstream <pinned openevolve checkout at 411fb59> --output head-to-head.json `
  --repeats 9 --workers 1,2,4,8,16 --ours-n 8000 --openevolve-n 300
```

The venv matters: a bare checkout fails at `import dacite` in `openevolve/config.py` before any
timing happens. Run it on a quiet machine, and check `machine_was_quiet` in the JSON before trusting
a timing column.
