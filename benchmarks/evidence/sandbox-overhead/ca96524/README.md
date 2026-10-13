# V1-76 (#181): what process isolation costs per evaluation

Measured by `benchmarks/EvolutionSandbox` through the `Sandbox overhead` workflow, on the hosted
runners, at main `ca96524`. Raw JSON beside this file; the earlier run is kept under
[`../9f2ae1a-pr224`](../9f2ae1a-pr224) so the runner variance is visible rather than averaged away.

## Method

Four modes, same trivial Python candidate, 500 evaluations after 50 warm-up:

| mode | what it is |
|---|---|
| baseline | one long-lived interpreter that execs each candidate and replies on a pipe — what OpenEvolve does |
| process | `ProcessProgramExecutionEngine`: a fresh interpreter per candidate, job-object / `ulimit` bounded |
| warm | `WarmPythonExecutionEngine`: a forked child per candidate on Linux, a reused interpreter on Windows |
| docker | a fresh `docker run --rm --network none` container per candidate (image pulled first, so the pull is not timed) |

Added latency is sandbox minus baseline **at the same percentile**. The runner's own CPython is
resolved through `os.path.realpath` so no launcher shim is timed.

## Results

Added latency per evaluation, in milliseconds:

| mode | OS | target | run at `ca96524` | run at `9f2ae1a` (PR #224) | verdict |
|---|---|---|---:|---:|---|
| process p50 | Linux | <= 30 | **62.25** | 44.77 | **misses, both runs** |
| process p95 | Linux | — | 66.96 | 51.12 | |
| process p50 | Windows | <= 60 | **35.09** | 25.31 | **meets, both runs** |
| process p95 | Windows | — | 43.64 | 30.28 | |
| warm p50 | Linux | <= 5 | **2.785** | 1.937 | **meets, both runs** |
| warm p50 | Windows | <= 5 | **0.106** | 0.042 | **meets, both runs** |
| docker p50 | Linux | — | 150.9 | 116.5 | ~2.4x the process sandbox |
| docker p95 | Linux | — | 159.6 | 145.7 | |

So the safety guarantee's price is bounded and known: **a fresh process per candidate costs 35 ms
(Windows) to 62 ms (Linux) at p50**, a container costs ~151 ms, and **reusing a warm worker costs
under 3 ms on either OS** — which is what satisfies the story's second acceptance criterion
through its own escape clause ("or a warm-worker mode is added that meets them").

## Caveats, stated rather than buried

- **Runner variance is large.** The same code measured 44.77 ms and 62.25 ms for Linux process p50
  on two runs a day apart — a 39% swing on shared hardware. Neither number is "the" overhead; the
  pair brackets it. The Linux target is missed in both, which is why the verdict does not depend on
  which run is quoted.
- **Docker is Linux-only here.** The hosted Windows runner has no Docker daemon, so the story's
  "in the Docker sandbox ... on Linux and Windows" is measured on Linux only. Measuring it on
  Windows needs a self-hosted runner with Windows containers.
- **A negative p95 appears in the earlier run** (Windows warm p95 = -1.829 ms): the reused worker
  beat the long-lived baseline in the tail. That is a measurement artefact of comparing two
  different pipe-reply paths at the tail, not a speed-up, and it is why p50 carries the verdict.
- **Do not use a developer machine for this.** The same benchmark on this project's Windows dev box
  reported 166.5 ms added p50 — 4.7x the hosted runner's 35.09 ms — because the store CPython and
  the on-access scanner inflate process creation. The hosted numbers above are the authoritative
  ones.
