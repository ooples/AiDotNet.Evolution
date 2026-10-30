# Container execution latency (E-2), measured at bc07231

Produced by the `sandbox-overhead` workflow, run 40706615405, on the hosted `ubuntu-latest` and
`windows-latest` runners: 500 evaluations (50 for cold and `docker run`, 100 for `docker exec`) of a
trivial Python candidate after warmup. Added latency is each arm's percentile minus the in-process
baseline's (0.14 ms p50 on Linux).

| Arm (Linux) | Added p50 | Added p95 |
|---|---|---|
| `ContainerExecutionEngine`, warm | **25.35 ms** | 28.88 ms |
| `ContainerExecutionEngine`, cold | 285.52 ms | 302.83 ms |
| `docker exec` of a fresh interpreter per candidate | 60.29 ms | - |
| `docker run --rm` per candidate | 146.12 ms | 167.85 ms |
| `ProcessProgramExecutionEngine` (unsandboxed) | 59.82 ms | 64.44 ms |
| `WarmPythonExecutionEngine`, fork (unsandboxed) | 2.69 ms | 2.95 ms |

**Verdict:** the warm container meets the E-2 target (added p50 at most 30 ms on Linux) in this run,
with 4.6 ms of margin, while isolating the candidate from the host's network, filesystem and
capabilities. It is cheaper than the unsandboxed process engine and less than half of a `docker exec`
per candidate, which is why the engine uses an in-container supervisor rather than one exec each.

Caveats:

- **One run.** The ca96524 evidence shows a 39% run-to-run swing on these shared runners for the process
  arm. The 4.6 ms margin is smaller than that swing could be, so a slower runner may miss the target;
  re-run the workflow to bracket it before treating the margin as settled.
- **Windows has no Linux-container daemon** on the hosted runner, so every container arm is `null` in
  `windows-latest.json`; only the process and warm-reused arms were measured there.
- **Where the warm cost goes** has not been profiled. The fork-path candidate itself is cheap (the
  unsandboxed fork worker adds 2.7 ms); the remainder is the Docker attach stream, the supervisor's
  per-candidate sweep and workdir wipe, and the stdin writer thread. No breakdown is claimed.
