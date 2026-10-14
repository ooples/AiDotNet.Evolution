# V1-70 (#175): engine overhead, paired head-to-head at `65ced88`

Supersedes [`../5699058/`](../5699058/README.md), which left one target open (peak working set at 16
workers, 64.1 MB against a 64 MB target, measured with #197 applied locally rather than merged) and
#197 (`9e27171`) and #200 (`65ef0d2`) are both on `main` now. **Not a headline claim (R12):** a C#
controller against a Python one is expected to win on overhead. Raw rows: `head-to-head.json`.

Produced by the committed driver,
[`benchmarks/external/run_overhead_head_to_head.py`](../../../external/run_overhead_head_to_head.py),
which measures **both** systems - the previous attempt at this rerun measured only ours and carried
the OpenEvolve column forward, which review rightly refused as a basis for restating a paired bound.

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
that requires three consecutive readings of peak total CPU under 20% before starting: 6.5%, 11.6% and
6.1% before, 16.5% at the finish. An earlier attempt at this same measurement was discarded because
another session was building a solution with ~25 MSBuild workers throughout it.

## Results

| workers | ours us/eval | OpenEvolve us/eval | ours / theirs [95% CI] | ours peak MB | OpenEvolve peak MB |
| --- | --- | --- | --- | --- | --- |
| 1 | 46.4 | 28,346 | 0.0016 [0.0015, 0.0024] | 61.48 | 142.5 |
| 2 | 47.7 | 18,660 | 0.0026 [0.0025, 0.0028] | 61.39 | 208.8 |
| 4 | 49.4 | 12,554 | 0.0039 [0.0030, 0.0047] | 61.39 | 340.1 |
| 8 | 48.7 | 12,493 | 0.0039 [0.0023, 0.0061] | 61.39 | 601.2 |
| 16 | 47.4 | 11,369 | 0.0042 [0.0037, 0.0046] | 61.21 | 1,121.3 |

No resample produced a non-positive `T(2N) - T(N)` in any of the 90 rows.

**Per-repeat spread, stated rather than smoothed:** ours ranges 42.5-95.0 us at 1 worker and
18.0-79.3 us at 8, against medians of 46.4 and 48.7; theirs ranges 7,493-48,179 us at 1 worker. The
medians are what the targets are judged on, and the paired bootstrap is what the interval is built
from, precisely because single repeats on a desktop swing this far.

**One hang.** OpenEvolve deadlocked once (`n=600`, 16 workers, repeat 4); the driver killed it at
180 s, recorded it under `hangs` and re-measured the cell. Its memory also grows with the pool:
142 MB at one worker to 1.12 GB at sixteen, against our flat 61 MB.

## Targets

| metric | target | measured | status |
| --- | --- | --- | --- |
| our overhead per evaluation, 1-16 workers (N >= 8,000) | <= 100 us | 46.4-49.4 us | **met** |
| ours / OpenEvolve, 95% CI upper bound, every worker count | <= 0.05 | <= **0.0061** (worst, 8 workers) | **met, paired and measured** |
| our peak working set, 16 workers | <= 64 MB | **61.21 MB** | **met** |

Peak working set is flat across worker counts (61.21-61.48 MB), so the memory target is met at every
count rather than only at the one the metric names.

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
