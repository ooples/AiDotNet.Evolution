# Container execution latency (E-2), measured at 8f90bf2

Produced by the `sandbox-overhead` workflow, run 40958714804, on the hosted `ubuntu-latest` and
`windows-latest` runners, with the same arms and counts as the earlier evidence. 8f90bf2 carries 461bcd7, which forks
the first candidate directly and starts the zygote only after that candidate replies. Added latency is each arm's
percentile minus the in-process baseline's (0.09 ms p50 on Linux).

| Arm (Linux) | Added p50 | Added p95 | 52baae6 added p50 | bc07231 added p50 |
|---|---|---|---|---|
| `ContainerExecutionEngine`, warm (zygote fork) | **3.62 ms** | 3.90 ms | 3.62 ms | 25.35 ms |
| `ContainerExecutionEngine`, cold | 295.73 ms | 311.56 ms | 384.07 ms | 285.52 ms |
| `docker exec` of a fresh interpreter per candidate | 57.88 ms | - | 62.96 ms | 60.29 ms |
| `docker run --rm` per candidate | 131.47 ms | 139.52 ms | 146.11 ms | 146.12 ms |
| `ProcessProgramExecutionEngine` (unsandboxed) | 53.85 ms | 57.63 ms | 58.05 ms | 59.82 ms |
| `WarmPythonExecutionEngine`, fork (unsandboxed) | 2.70 ms | 3.80 ms | 2.98 ms | 2.69 ms |

**Reading:** the cold arm is back to 295.73 ms, 10.21 ms above the
pre-zygote bc07231 and 88.34 ms below 52baae6, whose zygote started before the first
candidate. The warm arm is 3.62 ms against the 30 ms E-2 target.

Caveats:

- **One run per commit** on shared runners. In this run the process and `docker run` arms are also 4-15 ms faster
  than at 52baae6, so part of the cold difference may be runner variance; the mechanism above accounts for the rest.
- **Windows has no Linux-container daemon** on the hosted runner, so every container arm is `null` in
  `windows-latest.json`.