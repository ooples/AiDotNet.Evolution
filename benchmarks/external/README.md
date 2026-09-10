# Matched external numeric baseline

`ScipyDifferentialEvolutionMatched8` runs pinned SciPy differential evolution against the **same C# objective code**
as all six core methods. It introduces no library dependency into the core package. It uses local CPU only.

The bridge publishes the eight shared initial genomes in unit coordinates; SciPy evaluates those first, and the
C# service verifies every physical-genome identity before accepting initialization. Unit coordinates avoid
physical-bound normalization round-trip differences. Every subsequent evaluation executes afresh in C#, reserves
its cost before dispatch and records an independent ledger and trace. No Python reimplementation of the objectives
or PRNG is used. This is a local numeric protocol, not a candidate-code security sandbox or remote worker service.

## Fixed comparison settings

`best1bin`, mutation dithering `(0.5,1)`, recombination `0.7`, immediate updates, one worker, tolerance `0.01`, absolute
tolerance `0`, no vectorization, and NumPy `default_rng` seeded by the paired run seed. There were no tuning trials.
The supplied eight-member population overrides SciPy's usual dimension-scaled population; polishing is disabled to
avoid unbudgeted local minimization. These controls are **not SciPy's native population/polishing defaults**, nor a
claim that this configuration maximizes SciPy performance. See the [official API documentation](https://docs.scipy.org/doc/scipy-1.17.0/reference/generated/scipy.optimize.differential_evolution.html).

Evaluation caps must be multiples of eight, including initialization; generation count is `cap/8 - 1`. SciPy may
converge before the cap. This is successful under-budget termination, not failure or permission to add hidden runs.
The analyzer carries the terminal measured incumbent forward to the cap without inventing observations or charges.
All other early exits fail. A full-cap `maxiter` stop is valid even when SciPy's `success` flag is false.

Controller dispatches, SciPy `nfev`, C# evaluator calls, complete traces and resource receipts must agree. External
`proposal_calls` counts objective dispatches after initialization, not every internal numerical operation. Process
startup/IPC costs are not included in this evaluator-call endpoint: **no runtime, total-compute or memory comparison**.
Failures retain observed work; missing terminal accounting is explicitly unknown, not a fabricated zero-cost receipt.

## Run

Use an isolated Python environment; do not change the user's global packages. On Windows:

```powershell
python -m venv .local/external-env
.local/external-env/Scripts/python -m pip install -r benchmarks/external/requirements.txt
powershell -ExecutionPolicy Bypass -File eng/Test-ExternalBaseline.ps1 -Python .local/external-env/Scripts/python.exe
```

The smoke labels its working-tree provenance explicitly and checks exact same-platform replay. For a pinned pilot,
commit the relevant source, rebuild, and record both the full revision and evaluator binary hash:

```powershell
$revision = (git rev-parse HEAD).Trim()
dotnet build benchmarks/AiDotNet.Evolution.Quality -c Release --no-incremental
dotnet benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll 10 256 $revision TestResults/quality/core-new.json
.local/external-env/Scripts/python benchmarks/external/scipy_baseline.py --baseline TestResults/quality/core-new.json --evaluator benchmarks/AiDotNet.Evolution.Quality/bin/Release/net10.0/AiDotNet.Evolution.Quality.dll --output TestResults/quality/external-new.json
```

The normal runner requires a clean relevant source tree, matching baseline revision and assembly revision tag.
It records the binary SHA-256; this is build provenance, not cryptographic proof of a compiler/toolchain. Campaigns
retain all core and external rows plus the original input hash. Existing output files are never overwritten.
Protocol `numeric-development-v4-external` works with the paired analyzer and an explicit retrospective plan; smoke
and development fixtures cannot become representative/confirmatory evidence by changing a label. Separate native-
default/tuned external controls, broader tasks, OpenEvolve/model access and prospective confirmation remain open.
