# Robustness against OpenEvolve's defect classes (V1-83, #185)

Commit `c30c875`. Each of the eight defect classes found in the v1 readiness review was run on both sides:
our robustness suite, and the same scenario driven through OpenEvolve's own `ProgramDatabase` and
`Evaluator` by [`benchmarks/external/openevolve_defect_probe.py`](../../../external/openevolve_defect_probe.py).
OpenEvolve's outcome is evidence, not a gate. The
[`openevolve-defects`](../../../../.github/workflows/openevolve-defects.yml) workflow reruns the probe in CI
against the pinned 0.3.2 and 0.4.0 releases.

| # | Defect class | OpenEvolve 0.3.2 | OpenEvolve 0.4.0 | Ours | Our test |
| --- | --- | --- | --- | --- | --- |
| D1 | A NaN score freezes its cell | reproduced | reproduced | passes | `ArchiveDefectClassTests.D1_…` |
| D2 | Programs rejected as not novel leak | reproduced | reproduced | passes | `EngineDefectClassTests.D2_…` |
| D3 | Cell losers join the island | reproduced | reproduced | passes | `ArchiveDefectClassTests.D3_…` |
| D4 | Cells drift as bin ranges adapt | reproduced | reproduced | passes | `ArchiveDefectClassTests.D4_…` |
| D5 | Results depend on worker completion order | reproduced | reproduced | passes | `EngineDefectClassTests.D5_…` |
| D6 | Timeouts do not stop the evaluation | reproduced | reproduced | passes | `HungCandidateDefectClassTests.D6_…` |
| D7 | Requested bins are overridden | reproduced | reproduced | passes | `ArchiveDefectClassTests.D7_…` (1, 10, 37 bins) |
| D8 | Declared options that do nothing | reproduced | reproduced | passes | `OptionCoverageTests.D8_…` |

## Metrics

| metric | baseline | target | measured |
| --- | --- | --- | --- |
| defect classes with a passing test on ours | 0 of 8 | 8 of 8 | **8 of 8** ([`ours.json`](ours.json)) |
| public options exercised by a test | unmeasured | 100% | **311 of 311** (`OptionCoverageTests`) |

## What each OpenEvolve scenario observed

These are the exact observations in [`openevolve-defects-0.3.2.json`](openevolve-defects-0.3.2.json), produced by
the committed probe in the `openevolve-defects` CI workflow (Python 3.12.3, Linux). 0.4.0 produced the same results
apart from D6's process id and heartbeat count ([`openevolve-defects-0.4.0.json`](openevolve-defects-0.4.0.json)).

- **D1**: a NaN occupant kept its cell against challengers scoring 1, 100 and 1e9. As a control, the same
  challengers displaced a finite 0.5 occupant of that cell, so they do land in it.
- **D2**: with the novelty check returning "not novel", the program stayed in `programs` and was in no
  island, because `add()` stores the program before it checks novelty.
- **D3**: a program that lost its cell comparison was still an island member, and owned no cell.
- **D4**: the same stored program moved from cell 50 to cell 5 after one wider value arrived.
- **D5**: one cell, two equal-fitness programs: applied A then B, the owner is A; applied B then A, it is B.
  `process_parallel.py` applies futures in the order it finds them done, so worker timing decides the
  archive.
- **D6**: `evaluate_program` returned a timeout after 1.00 s. One second later the hung evaluation's
  heartbeat had gone from 20 to 36 and the child it detached was still running: neither was stopped.
- **D7**: 10 requested bins per dimension (archive size 10000, two dimensions) became 100.
- **D8**: `prompt.use_meta_prompting`, `evaluator.memory_limit_mb`, `evaluator.cpu_limit`,
  `evaluator.distributed` and `diversity_metric: feature_based` are read nowhere outside `config.py`.

## Method notes

- D1–D7 drive OpenEvolve's classes the way its controller does. D2 forces the novelty verdict by replacing
  `_is_novel`, because the real verdict needs an embedding model. D8 is a static scan of the installed
  package.
- The D8 check on our side compiles the sources and tests together and resolves each test reference to its
  property symbol, so a same-named member on another type does not count. It covers every public settable
  property of a public type named `*Options`, wherever it is declared. Its limit: any reference in a test
  counts, without proving the test asserts on the option's effect. Every option it flagged got a
  behaviour test that sets the option and asserts on the difference (for example `PromptOptionBehaviourTests`,
  `JudgeAndMetricOptionBehaviourTests`, `CSharpCostOptionBehaviourTests` and the work-server option tests).
- Ours ran on net10.0 on Windows ([`ours.json`](ours.json), regenerated from this suite). OpenEvolve ran in
  CI on Linux.
