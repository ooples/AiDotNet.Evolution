# Container execution latency (E-2), measured at 52baae6

Produced by the `sandbox-overhead` workflow, run 40924825097, on the hosted `ubuntu-latest` and
`windows-latest` runners, with the same arms and counts as the bc07231 evidence. Added latency is each
arm's percentile minus the in-process baseline's (0.11 ms p50 on Linux).

| Arm (Linux) | Added p50 | Added p95 | bc07231 added p50 |
|---|---|---|---|
| `ContainerExecutionEngine`, warm (zygote fork) | **3.62 ms** | 3.83 ms | 25.35 ms |
| `ContainerExecutionEngine`, cold | 384.07 ms | 392.91 ms | 285.52 ms |
| `docker exec` of a fresh interpreter per candidate | 62.96 ms | - | 60.29 ms |
| `docker run --rm` per candidate | 146.11 ms | 152.46 ms | 146.12 ms |
| `ProcessProgramExecutionEngine` (unsandboxed) | 58.05 ms | 61.04 ms | 59.82 ms |
| `WarmPythonExecutionEngine`, fork (unsandboxed) | 2.98 ms | 4.07 ms | 2.69 ms |

**Reading:** with candidates forked from a pristine zygote, the warm container adds 3.62 ms at p50,
0.64 ms above the unsandboxed fork worker in the same run, against the 30 ms E-2 target.

**Regression recorded here:** the cold arm rose by 98.6 ms against bc07231, because this commit starts
the zygote (a fresh interpreter with the candidate-side imports) before the first candidate runs.
Commit 461bcd7 forks the first candidate directly and starts the zygote after its reply; its own
evidence directory records whether that recovers the cold arm.

Caveats:

- **One run per commit** on shared runners; the ca96524 evidence shows run-to-run swings of up to 39% on
  the process arm, so the cold-arm difference is reported, not attributed beyond the mechanism above.
- **Windows has no Linux-container daemon** on the hosted runner, so every container arm is `null` in
  `windows-latest.json`.
