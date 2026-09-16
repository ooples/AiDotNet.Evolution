# US-02: measurement calibration and proposed head-to-head budget

Status: **no new model-driven comparison has run; no superiority claim**.
The user requested calibration first, then a budget before further model calls.
US-02 remains open. The earlier negative selector experiment is preserved in
[SELECTION_DEVELOPMENT.md](SELECTION_DEVELOPMENT.md).

## What the no-model calibration established

Three development-only preflights used workload multipliers 1, 4 and 16:
90 containers, zero model calls, zero unknown container work. The first two
preflights used the initial Python resource probe; the third used the final
read-only `/bin/cat` probe. They are development diagnostics, **not comparable
optimization arms**. No selection/final-partition performance was opened in this work.

The archived 16x preflight contains five original solvers and five echo diagnostics,
two input instances per transaction, and three fresh-container samples per arm:

| Development task | Original median warm ms | Echo median warm ms |
|---|---:|---:|
| Base64 | 14.400 | 12.762 |
| SHA-256 | 6.424 | 8.050 |
| Connected components | 34.655 | 43.122 |
| Minimum spanning tree | 141.611 | 155.599 |
| Dijkstra | 371.694 | 23.181 |

These are **not before/after improvements**. Echo returns the input, so its output
shape and serialization cost differ from the original solver. It is an overhead
diagnostic, not an exact subtractable baseline or rigorous lower bound. Dijkstra
shows substantial work beyond this diagnostic; the other tasks do not establish
that isolated algorithm execution dominates the endpoint. Keep those controls;
do not silently select only favorable tasks or promise a strict win on primitives.

The archived 30 containers consumed 57.967 supervisor-seconds and 26.030 summed
container-lifecycle seconds; all completed with kernel counters and known work.
Peak cgroup memory was 49,516,544 bytes at most. This is not host-process peak RAM.

## Measurement and adversarial review

As a benchmark owner, I want measurements outside candidate control so that a
program cannot win by reporting fake clocks, memory, or correctness.

- Given an initialized candidate, when the host releases fresh inputs with a
  secret nonce, then an external monotonic clock measures through the complete
  response frame. Startup/imports/construction are excluded; input transfer,
  copying, solving and serialization are included. **Not isolated kernel time.**
- Given a nonroot candidate with no network/capabilities and read-only files,
  when resource probes execute, then only the trusted host requests read-only
  UID0 `/bin/cat` of kernel cgroup counters. Candidate execution never gains UID0.
  CPU and memory measurements include initialization/probe overhead and end at
  the post-response probe, not complete process or host lifetime.
- Given malformed output, a preprinted response, resource spoofing, a blocked
  input reader, or a timeout, when evaluation finishes, then it retains a bounded
  failure receipt. Candidate failure does not poison later admission; actual
  probe/infrastructure failure does. Missing counters are never replaced by zero.
- Given an unchanged/fallback solver, when comparing methods, then its normalized
  speedup is exactly one. Independent noisy original timings cannot manufacture
  a policy benefit. Warm repeats are not independent search samples.

Why kernel counters: sampled Docker memory usage is not peak memory, and Docker's
cgroup-v2 API does not supply the v1 `max_usage` field. The implementation reads
actual `memory.peak` and `cpu.stat`; see the
[Docker API](https://docs.docker.com/reference/api/engine/version/v1.46/) and
[kernel cgroup-v2 documentation](https://cdn.kernel.org/doc/html/latest/admin-guide/cgroup-v2.html).

Host correctness validation uses the pinned upstream reference outside the candidate
container. It is **not an independently derived mathematical oracle**. Fresh audits
improve coverage but do not prove correctness on every input. Earlier independent
graph oracles remain intact; they are not silently replaced in prior evidence.

## Proposed budget, not authorization to start

The staged driver is `benchmarks/external/run_warm_study.py`.
All proposed provider work uses the subscription-only Codex transport; no API-key
fallback, paid model requests, or additional subscription purchase is proposed.
Calls and reported tokens consume subscription allowance; its remaining allowance
and reset behavior are not inferred from token totals.

| Stage | Design | Maximum model calls |
|---|---|---:|
| Native tuning | 5 development tasks × 2 searches × 2 profiles × 2 methods × 4 proposals | 160 |
| Variability calibration | 3 selection tasks × 4 searches × 6 tracks; 4 calls per track except one-shot | 252 |
| Final comparison | 3 final tasks × N searches × the same six tracks | 63 × N |

**Recommend authorizing 412 calls first**, then stopping to show the derived final
budget before opening final performance data. Profiles are frozen after development;
selection data estimates variability, not another round of profile selection.
The default cumulative ceiling is 412. Raising it for a subsequently registered
phase requires an explicit `--budget-authorization` reference to the user's new
approval; the executor never enlarges a running phase's budget.

The final planner uses a minimum of 12 independent searches per task, a 20% relative
latency effect, 80% nominal power, family alpha 0.05 over 12 task/contrast comparisons,
and an upper pilot-variance estimate. These assumptions are explicit, not guarantees:
four selection searches provide weak variance information, and variance may change
on unseen families. **The no-model preflight cannot determine search variability.**

| Final searches per task (N) | Additional final calls | Total including tuning/calibration |
|---:|---:|---:|
| 12 (minimum, not yet justified) | 756 | 1,168 |
| 20 | 1,260 | 1,672 |
| 32 | 2,016 | 2,428 |
| 48 | 3,024 | 3,436 |

For the 1,168-call minimum design, the declared evaluation caps allow approximately
7,768 containers if every candidate reaches all samples/audits. At the observed
1.93 supervisor-seconds per reference container, that alone is about 4.2 hours,
before model generation, controllers, host validation and scheduling overhead.
The prior 252-call cold study took 75.96 minutes for its four search/diagnostic
blocks, excluding subsequent audits. A **rough planning allowance is 7–12 hours
total**, or **2–4 hours for the first 412 calls**, not a runtime guarantee or a cap.
Generated slow/invalid programs, larger tasks and quota waits can increase this.
The previous experiment averaged about 10,050 reported tokens per call; extrapolating
gives roughly 4.1 million tokens for 412 calls or 11.7 million for 1,168. This is
not a measured future cost, dollar bill, or subscription-quota conversion.

## Frozen experiment requirements

As a maintainer, I want a prospective comparison that can lose honestly.

- Given the published registration hash, when execution starts, then image,
  checkout pins, Python helpers, controller binaries, instances, profiles,
  complete schedule and budgets are bound before dispatch. An exclusive marker
  prevents restarting that output directory. External registration/review must
  also prevent starting a new directory to hide an unfavorable repeat.
- Given matched native profile grids, when tuning finishes, then each method
  chooses its own profile using equal task/search-seed weighting and fresh-instance
  speedups; defaults win exact ties. Two tested profiles are not exhaustive tuning.
- Given the selection results, when calculating N, then use paired search-level
  variation, never container samples as independent n. Refuse a final grid that
  exceeds the registered cumulative cap; do not truncate it after inspecting results.
- Given completed final results, when reporting, then retain all planned cells,
  failures, fallbacks, costs and audits. Show original/deployed latency, throughput,
  response-scope CPU/peak memory, calls/tokens, search wall/evaluator time and paired
  multiplicity-adjusted intervals. Raw search receipts preserve progress trajectories.
- Given a latency interval excluding parity, when interpreting it, then do not
  infer superiority on memory, tokens, CPU, correctness, other tasks or all OpenEvolve
  configurations. The report deliberately emits `claim: none`; resource endpoints
  are descriptive, not multiplicity-tested all-metric superiority gates.

Before final registration, review catalog holdout exposure across prior work, the
upstream-validator limitation, timing-floor diagnostics and power assumptions.
An exact provider snapshot is still unreported. A model alias plus balanced order
does not prove a fixed backend model. Budget approval alone does not resolve these
limitations or certify US-02 as complete.

## Evidence and reproduction

[warm-calibration.tar.xz](warm-calibration.tar.xz): 419,712 bytes;
SHA256 `fc10b37408dc9c23e859e25dacc9e2502584c01e36413d49cfb5a4d86e5f11c8`.
All 303 files / 117 unique blobs were verified and actually restored, 11,895,094 bytes.
The archive includes the pre-dispatch plan with artifact hashes, every input,
container configuration, stdout/stderr and terminal receipt. Earlier 1x/4x
diagnostics remain locally under `TestResults/us02-warm-calibration-01` and `-04`.

```powershell
python benchmarks/analysis/program_evidence.py --archive docs/evidence/us02/warm-calibration.tar.xz --sha256 fc10b37408dc9c23e859e25dacc9e2502584c01e36413d49cfb5a4d86e5f11c8 --output TestResults/warm-restored
```

The calibration can be rerun into a **new** output directory with
`calibrate_warm_panel.py --output ... --upstream <pinned AlgoTune> --image <immutable image> --multiplier 16`.
Do not run heavy build/test work beside timing measurements. A rerun is a new
development measurement, not a replacement for this archived result.

## Verification

One final Release solution build passed with zero warnings/errors. Core tests:
654 net10.0, 608 net8.0, 608 net471; analysis: 91; existing/new program contracts: 21;
subscription transport: 7; actual OpenEvolve adapter: 1; cold Docker isolation: 11.
The warm tests cover codec bounds, sandbox attacks, incomplete grids, source
mutation, budget changes, fallback noise and search-level statistics.
An additional end-to-end warm fixture exercised **all six actual controller tracks**
through 48 real containers with six scripted responses, zero provider calls,
passing fresh validation/audits and zero unknown work. This is plumbing/security
verification, not an optimization experiment.
