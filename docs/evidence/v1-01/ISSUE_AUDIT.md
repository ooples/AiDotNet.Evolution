# V1-01: open story issues reconciled with `main`

Story [#108](https://github.com/ooples/AiDotNet.Evolution/issues/108), epic
[#125](https://github.com/ooples/AiDotNet.Evolution/issues/125). Audited 2026-09-22 against
`main` at `7de3011`.

## Result

76 unchecked boxes were audited on #21 #22 #24 #29 #30 #35 #36 #37 #38 #41 #42 #43.

| class | count | action |
| --- | --- | --- |
| Dependency box whose issue is **closed** (stale) | 15 | ticked, with a link to the closing PR |
| Dependency box whose issue is still open | 14 | left open; it is real |
| Criterion box **met** by merged work | 7 | ticked, with a link to evidence |
| Criterion box that is a **real gap** | 40 | left open, and each one is owned by a v1 story |

Closing PRs for the stale dependencies: US-01 #19, US-02 #20, US-05 #23, US-08 #26 and US-09 #27
were closed by [#45](https://github.com/ooples/AiDotNet.Evolution/pull/45). US-21 #39 was closed by
[#60](https://github.com/ooples/AiDotNet.Evolution/pull/60).

## Criteria met by merged work

- **US-20 #38**, all three gates. [#59](https://github.com/ooples/AiDotNet.Evolution/pull/59)
  merged with 14/14 hosted checks green and a review by `ooples`. Its dependencies US-05 #23,
  US-08 #26 and US-10 #28 are closed, and foundation #15 is merged.
- **US-06 #24**, hosted checks. [#77](https://github.com/ooples/AiDotNet.Evolution/pull/77)
  merged with 12 checks green. The only non-success is `Validate PR title`, which was cancelled.
- **US-03 #21**, all three core criteria. They were re-executed for this audit rather than read
  off #75:
  - *A fast wrong candidate cannot win.* 39 C# admission/correctness/promotion tests pass
    (`--filter Admission|Correctness|Promotion|Feasib`, net10.0), along with the scalar-spoof
    fallback test `test_scalar_spoof_falls_back_only_after_original_passes_all_audits`.
  - *Independent checks on held-out, boundary, adversarial and randomized cases.*
    `test_program_correctness.py` passes against pinned AlgoTune `dff9914c`.
  - *Enforced process/resource isolation.* `test_docker_sandbox.py` and `test_warm_sandbox.py`
    pass against a real sandbox image. Memory, pids, network, read-only files, non-root and no
    privileges are all checked, and the kernel OOM-kill counter is confirmed to increment.

  Scope: this covers the 11-task Python warm boundary and C# archive admission, which is what
  #75 delivered. The v1 benchmark families (#111 #112 #113 #29) must each supply their own
  oracle, and their acceptance criteria require it.

## Real gaps found

1. **The warm sandbox could not run on cgroup-v1 hosts. Fixed in this PR.** `warm_sandbox.py`
   read only v2 counters (`cpu.stat`, `memory.peak`). This machine's Docker Desktop (WSL2 kernel
   `5.15.167.4`) reports `CgroupVersion=1`, so every warm evaluation raised
   `cat: /sys/fs/cgroup/cpu.stat: No such file or directory`. Every local campaign in the v1
   plan would have failed. The two tests that exposed it had only ever run where an image was
   configured, which is why the defect went unseen.
   - The sandbox now takes the cgroup version from the Docker **daemon** (never from inside the
     untrusted container) and reads the equivalent v1 lifetime counters: `cpuacct.usage` in ns,
     and `memory.max_usage_in_bytes`, which includes page cache exactly as `memory.peak` does.
   - **The v2 identity is byte-identical.** It is copied into manifests that are digested and
     compared (`warm_evaluator.py`, `warm_screening.py`), so changing it would silently
     invalidate every existing v2 digest. A regression test pins it. Only v1 hosts are marked
     (`cgroup_version: "1"`), so v1 and v2 measurements are never pooled unknowingly.
   - Measured on v1, the fix gives the same evidence strength: the limit is exactly 536,870,912
     bytes, the allocating child exits with -9, and `oom_kill` goes from 0 to 1.
   - A test was also vacuous on v1. "Kernel counters are not writable" wrote to the v2 path,
     which does not exist there, so the write failed for the wrong reason. It now uses the paths
     this host's probe actually reads, and first asserts that they exist.
2. **US-06 #77 merged with no review record.** Its review box stays open, and a post-merge review
   is required before #24 can close.

## Verification

```
python -m pytest benchmarks/external/{test_warm_sandbox,test_docker_sandbox,test_warm_correctness,
  test_program_correctness,test_warm_study,test_warm_budget,test_warm_confirmation,test_program_audit}.py
  -> 83 passed, 1 skipped (needs built host + pinned OpenEvolve: V1-03 #110)
```

Environment: `EVOLUTION_SANDBOX_IMAGE` was built from `benchmarks/external/sandbox/Dockerfile`
with the repository root as its context, and `EVOLUTION_CORRECTNESS_UPSTREAM` pointed at AlgoTune
`dff9914c`.

The SciPy, pyribs, suite and OpenEvolve baseline tests are not claimed here. They need the
isolated benchmark environment and a pinned OpenEvolve, which V1-03 (#110) sets up, and none of
them imports a module this PR changes.
